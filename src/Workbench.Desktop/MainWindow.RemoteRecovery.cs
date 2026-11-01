using System.Text.Json;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop;

/// <summary>An exact bounded set, not a high-water mark: late lower sequences are still presented.</summary>
internal sealed class PresentedEventWindow(int capacity = 4096)
{
    private readonly HashSet<long> _seen = [];
    private readonly Queue<long> _order = new();
    private string? _sessionId;
    private long? _representedThrough;
    internal int Count => _seen.Count;

    internal void SelectSession(string sessionId)
    {
        if (_sessionId != sessionId) Reset(sessionId, []);
    }

    internal void Reset(string sessionId, IEnumerable<AgentEvent> represented, long? representedThrough = null)
    {
        _sessionId = sessionId;
        _representedThrough = representedThrough;
        _seen.Clear(); _order.Clear();
        foreach (var evt in represented) if (evt.SessionId == sessionId) Accept(evt);
    }

    internal bool Accept(AgentEvent evt)
    {
        if (evt.SessionId != _sessionId) return false;
        if (evt.Sequence <= 0) return true; // Compatibility events have no durable identity.
        if (_representedThrough is { } sourcePrefix && evt.Sequence <= sourcePrefix) return false;
        if (!_seen.Add(evt.Sequence)) return false;
        _order.Enqueue(evt.Sequence);
        while (_order.Count > Math.Max(1, capacity)) _seen.Remove(_order.Dequeue());
        return true;
    }

    internal void AdvanceRepresentedPrefix(long throughSequence)
    {
        // Only a coherent snapshot or a fully enumerated forward journal page establishes a prefix.
        // An arbitrary live event never calls this method, so late lower live IDs remain eligible.
        _representedThrough = Math.Max(_representedThrough ?? 0, throughSequence);
    }
}

public partial class MainWindow
{
    private readonly PresentedEventWindow _presentedEvents = new();
    private readonly Queue<(string Method, JsonElement Parameters)> _recoveryNotifications = new();
    private string? _recoveringSessionId;
    private HostClient? _recoveringHost;
    private int _recoveringGeneration;
    private bool _recoveryOverflow;
    private bool _legacyMessageRecovery;
    private bool _applyingRecoveryEvent;
    private long _latestBufferedEventSequence;
    private long _snapshotAppliedInputSequence;
    private readonly HashSet<string> _snapshotUserInputs = new(StringComparer.Ordinal);
    private long _approvalNotificationRevision;
    internal const int MaximumRecoveryNotifications = 2048;

    // Called at the end of RestoreSession, after its message projection has been installed.
    internal void RestoreRemotePresentation(AgentSession loaded, IReadOnlyList<AgentEvent> represented)
    {
        _presentedEvents.Reset(loaded.Info.Id, represented);
        _pendingApproval = null;
        ApprovalPanel.IsVisible = false;
        // A completed assistant message is saved before its completion event is emitted. Link that
        // already restored card so a late completion updates it instead of creating a second card.
        if (loaded.Messages.LastOrDefault() is { Role: "assistant", Content.Length: > 0 } last)
            _assistantEntry = _entries.LastOrDefault(entry => entry.Label == "ASSISTANT" && entry.Text == Limit(last.Content, 24000));
        SetRunState(loaded.Info.Status is "running" or "waiting_approval");
        ShowLoadedStatus(loaded.Info.Status);
        if (loaded.Info.Status == "waiting_approval") LocalizedControl.Text(RunStateLabel, "AwaitingApproval");
    }

    // Called before OnNotification interprets any event. Buffer during an asynchronous page restore so
    // a terminal event cannot disappear merely because its session was not selected until the RPC returned.
    internal bool AcceptPresentationNotification(string method, JsonElement parameters)
    {
        if (method == "approval.requested") _approvalNotificationRevision++;
        if (method is not ("agent.event" or "approval.requested")) return true;
        if (parameters.ValueKind != JsonValueKind.Object) return true;
        var sessionId = parameters.TryGetProperty("sessionId", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!_applyingRecoveryEvent && _recoveringHost == _host && sessionId == _recoveringSessionId && _recoveringSessionId is not null)
        {
            if (method == "agent.event" && parameters.TryGetProperty("sequence", out var sequence) && sequence.TryGetInt64(out var number))
                _latestBufferedEventSequence = Math.Max(_latestBufferedEventSequence, number);
            if (_recoveryNotifications.Count >= MaximumRecoveryNotifications) { _recoveryNotifications.Dequeue(); _recoveryOverflow = true; }
            _recoveryNotifications.Enqueue((method, parameters.Clone()));
            return false;
        }
        if (method != "agent.event") return true;
        var evt = parameters.Deserialize<AgentEvent>(Wire.Json);
        if (evt is null || evt.SessionId != _session?.Info.Id) return false;
        _presentedEvents.SelectSession(evt.SessionId);
        if (!_presentedEvents.Accept(evt)) return false;
        // Steering text already committed in this snapshot is a user message, not another new card.
        // Unapplied queues are separately projected and registered below, so their confirmation still
        // updates the corresponding card rather than being guessed from a sequence maximum.
        if (_applyingRecoveryEvent && evt.Type == "input.steered" && evt.Text is { } text && _snapshotAppliedInputSequence > 0 &&
            _snapshotUserInputs.Contains(text) && _steeringEntries.GetValueOrDefault(evt.RunId)?.Any(item => item.Text == text) != true)
            return false;
        return true;
    }

    private void BeginRemoteRecovery(HostClient host, string sessionId, int generation)
    {
        _recoveringHost = host; _recoveringSessionId = sessionId;
        _recoveringGeneration = generation;
        _recoveryNotifications.Clear(); _recoveryOverflow = false;
        _legacyMessageRecovery = false; _latestBufferedEventSequence = 0;
    }

    internal void PrepareMessageRecovery(SessionView view)
    {
        _legacyMessageRecovery = view.MessageEventSequence is null;
        _presentedEvents.Reset(view.Info.Id, [], view.MessageEventSequence);
        _snapshotAppliedInputSequence = view.RunCheckpoint?.LastAppliedInputSequence ?? 0;
        _snapshotUserInputs.Clear();
        foreach (var message in view.Messages.Where(message => message.Role == "user")) _snapshotUserInputs.Add(message.Content);
    }

    internal void ApplyRecoveredJournalPage(SessionEventReplayPage page)
    {
        if (!page.Available) throw new InvalidDataException(page.UnavailableReason ?? L.Text("HistoryUnavailable"));
        _applyingRecoveryEvent = true;
        try
        {
            foreach (var evt in page.Items) OnNotification("agent.event", Wire.Element(evt));
            _presentedEvents.AdvanceRepresentedPrefix(page.NextAfterSequence);
        }
        finally { _applyingRecoveryEvent = false; }
    }

    private async Task ReplayRemoteJournalAsync(HostClient host, SessionView view, int generation)
    {
        if (view.MessageEventSequence is not { } cursor)
        {
            AddEntry("RECOVERY", "", "#E4B499").LocalizeText(() => L.Text("RemoteServiceLegacyReplayUnavailable"));
            return;
        }
        async Task ReplayToAsync(long? upper)
        {
            while (true)
            {
                var page = await host.RequestAsync<SessionEventReplayPage>("session.events.since", new
                {
                    sessionId = view.Info.Id, afterSequence = cursor, throughSequence = upper, limit = 100
                });
                if (host != _host || generation != _loadGeneration || _session?.Info.Id != view.Info.Id) return;
                if (!page.Available) throw new InvalidDataException(page.UnavailableReason ?? L.Text("HistoryUnavailable"));
                if (page.ThroughSequence < cursor || upper is { } captured && page.ThroughSequence != captured ||
                    page.Items.Count > 100 || page.Items.Any(evt => evt.SessionId != view.Info.Id || evt.Sequence <= cursor || evt.Sequence > page.ThroughSequence) ||
                    !page.Items.Select(evt => evt.Sequence).SequenceEqual(page.Items.Select(evt => evt.Sequence).Order()) ||
                    page.Items.Select(evt => evt.Sequence).Distinct().Count() != page.Items.Count ||
                    page.NextAfterSequence != (page.Items.Count > 0 ? page.Items[^1].Sequence : cursor) || page.HasMore && page.NextAfterSequence <= cursor)
                    throw new InvalidDataException("The service returned an inconsistent journal replay cursor.");
                upper ??= page.ThroughSequence;
                ApplyRecoveredJournalPage(page);
                cursor = page.NextAfterSequence;
                if (!page.HasMore) return;
            }
        }
        await ReplayToAsync(null);
        // If finite replay took long enough to overflow its live queue, recover that already-observed
        // source range once more against another fixed boundary. Continuing high volume stays explicit.
        if (host == _host && generation == _loadGeneration && _recoveryOverflow && _latestBufferedEventSequence > cursor)
        {
            var observedBoundary = _latestBufferedEventSequence;
            _recoveryOverflow = false;
            await ReplayToAsync(observedBoundary);
        }
    }

    private void CompleteRemoteRecovery(HostClient host, string sessionId, int generation)
    {
        if (_recoveringHost != host || _recoveringSessionId != sessionId || _recoveringGeneration != generation) return;
        var buffered = _recoveryNotifications.ToArray();
        var overflow = _recoveryOverflow;
        var legacy = _legacyMessageRecovery;
        _recoveringHost = null; _recoveringSessionId = null;
        _recoveryNotifications.Clear(); _recoveryOverflow = false;
        if (host != _host || _session?.Info.Id != sessionId) return;
        foreach (var notification in buffered)
        {
            if (legacy && notification.Method == "agent.event" && notification.Parameters.TryGetProperty("type", out var type) &&
                type.GetString() is "message.delta" or "message.completed") continue;
            OnNotification(notification.Method, notification.Parameters);
        }
        if (overflow) AddEntry("RECOVERY", L.Text("RemoteServiceRecoveryLimited"), "#E4B499");
    }

    private async Task RestorePendingRemoteApprovalAsync(HostClient host, string sessionId, int generation)
    {
        if (!host.Connection.DurableService || host != _host || generation != _loadGeneration || _session?.Info.Id != sessionId) return;
        var revision = _approvalNotificationRevision;
        var pending = await host.RequestAsync<ApprovalRequest[]>("approval.pending", new { sessionId });
        if (host != _host || generation != _loadGeneration || _session?.Info.Id != sessionId) return;
        if (_session.Info.Status is not ("running" or "waiting_approval")) return;
        var approval = pending.FirstOrDefault(item => item.SessionId == sessionId && item.RunId == _session.RunCheckpoint?.RunId)
            ?? pending.FirstOrDefault(item => item.SessionId == sessionId);
        if (approval is not null)
        {
            if (revision != _approvalNotificationRevision && _pendingApproval is { } live && live != approval.Id) return;
            // This only displays a registered request. No response, continuation, command or run is sent.
            OnNotification("approval.requested", Wire.Element(approval));
            SetRunState(true);
            LocalizedControl.Text(RunStateLabel, "AwaitingApproval");
            LocalizedControl.Text(StatusLabel, "RemoteServiceApprovalRestored");
        }
        else if (revision == _approvalNotificationRevision)
        {
            _pendingApproval = null;
            ApprovalPanel.IsVisible = false;
        }
    }

    internal static bool CanRecoverRemoteConnection(HostConnectionProfile previous, HostConnectionProfile next) =>
        previous.DurableService && next.DurableService && previous.Kind == next.Kind && previous.ServiceId == next.ServiceId &&
        previous.Destination == next.Destination && previous.DataDirectory == next.DataDirectory;

    internal void PresentHostDisconnect(HostConnectionProfile connection, string message)
    {
        _ready = false;
        SetRunState(false);
        _pendingApproval = null;
        ApprovalPanel.IsVisible = false;
        var key = connection.DurableService ? "RemoteServiceDisconnected" : "RemoteServiceForegroundDisconnected";
        LocalizedControl.Text(StatusLabel, key);
        AddEntry("CONNECTION", "", "#F0B5A7").LocalizeText(() => L.Text(key) + "\n" + message);
    }
}
