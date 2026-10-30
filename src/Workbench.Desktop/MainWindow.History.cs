using Avalonia.Controls;
using Avalonia.Interactivity;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop;

public partial class MainWindow
{
    private int _historyFirstIndex;
    private int _historyTotal;
    private bool _viewingEarlierHistory;
    private bool _liveWindowTrimmed;
    internal const int MaximumTimelineEntries = 1000;

    private void BoundTimeline()
    {
        while (_entries.Count > MaximumTimelineEntries)
        {
            var removed = _entries[0];
            _entries.RemoveAt(0);
            foreach (var key in _toolEntries.Where(pair => ReferenceEquals(pair.Value, removed)).Select(pair => pair.Key).ToArray())
                _toolEntries.Remove(key);
            _liveWindowTrimmed = true;
        }
        if (_liveWindowTrimmed)
        {
            EarlierHistoryButton.IsVisible = true;
            HistoryRangeLabel.IsVisible = true;
            HistoryRangeLabel.Text = L.Format("LiveHistoryWindow", MaximumTimelineEntries);
        }
    }

    private async Task LoadSessionPageAsync(string id, int? beforeIndex = null)
    {
        var generation = ++_loadGeneration;
        var host = _host;
        var viewTask = host.RequestAsync<SessionView>("session.view", new { sessionId = id, limit = 100 });
        var eventsTask = host.RequestAsync<SessionEventPage>("session.events.page", new { sessionId = id, limit = 100 });
        await Task.WhenAll(viewTask, eventsTask);
        var view = await viewTask;
        var page = beforeIndex is null
            ? new SessionMessagePage(view.Messages, view.FirstMessageIndex, view.TotalMessages, view.HasEarlierMessages,
                view.Available, view.UnavailableReason, view.OmittedPayloads)
            : await host.RequestAsync<SessionMessagePage>("session.messages.page", new { sessionId = id, beforeIndex, limit = 100 });
        if (generation != _loadGeneration || host != _host) return;
        if (!page.Available) throw new InvalidDataException(page.UnavailableReason ?? L.Text("HistoryUnavailable"));
        var events = await eventsTask;
        RestoreSession(new(view.Info, page.Messages, view.Compaction, view.RunCheckpoint), events.Available ? events.Items : [], includeRecovery: false);
        _historyFirstIndex = page.FirstMessageIndex;
        _historyTotal = page.TotalMessages;
        _viewingEarlierHistory = beforeIndex is not null;
        EarlierHistoryButton.IsVisible = page.HasEarlierMessages;
        LatestHistoryButton.IsVisible = _viewingEarlierHistory;
        HistoryRangeLabel.Text = L.Format("HistoryRange", page.FirstMessageIndex + 1, page.FirstMessageIndex + page.Messages.Count, _historyTotal);
        HistoryRangeLabel.IsVisible = _historyTotal > 0;
        if (page.OmittedPayloads) AddEntry("HISTORY", L.Text("HistoryPayloadOmitted"));
        if (view.PendingInputs is { } pending)
        {
            if (!pending.Available || pending.HasMore)
                AddEntry("RECOVERY", L.Format("PendingInputCount", pending.TotalPending), "#E4B499");
            foreach (var input in pending.Items)
                AddEntry("RecoveredInput", "", "#E4B499").LocalizeText(() => L.Format("RecoveredInputText", input.Text));
        }
        RefreshTaskControls();
        await RefreshWorkspaceAsync();
    }

    private async void EarlierHistoryClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_session is null || _running) return;
        if (_liveWindowTrimmed) await LoadSessionPageAsync(_session.Info.Id);
        if (_historyFirstIndex <= 0) return;
        await LoadSessionPageAsync(_session.Info.Id, _historyFirstIndex);
        if (_entries.Count > 0) Timeline.ScrollIntoView(_entries[0]);
    });

    private async void LatestHistoryClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_session is not null && !_running) await LoadSessionPageAsync(_session.Info.Id);
    });

    private RunOptions CurrentRunOptions()
    {
        var options = _settings.Run with
        {
            RunId = null, SecretEnvironmentVariables = null,
            ExecutionMode = _settings.ExecutionMode, Research = _settings.Research,
            AutoApproveWrites = false
        };
        RunLimits.Validate(options);
        return options;
    }

    private void RefreshTaskControls()
    {
        if (RunBudgetLabel is null) return;
        RunBudgetLabel.Text = L.Format("RunBudgetSummary", _settings.Run.MaxIterations,
            (_settings.Run.TimeoutSeconds / 3600d).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture));
        ContinueRunButton.IsVisible = _session?.Info.Status == "paused" ||
            (_session?.RunCheckpoint is not null && _session.Info.Status is "interrupted" or "failed" or "cancelled");
        if (HostLocationLabel is not null) HostLocationLabel.Text = _settings.ActiveConnection.DisplayLabel;
    }

    private async void RunBudgetClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var options = await new RunBudgetWindow(_settings.Run).ShowDialog<RunOptions?>(this);
        if (options is null) return;
        RunLimits.Validate(options);
        _settings = _settings with { Run = options with { RunId = null, SecretEnvironmentVariables = null } };
        SaveSettings();
        RefreshTaskControls();
    });

    private async void ContinueRunClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_running || _submitting || _session is null) return;
        RequireWorkspace();
        if (_viewingEarlierHistory) await LoadSessionPageAsync(_session.Info.Id);
        var provider = _settings.Provider;
        if (_session.Info.Backend == "chatgpt")
        {
            ApplyAccountState(await _host.RequestAsync<AccountState>("account.status"));
            provider = provider with
            {
                Endpoint = "https://api.openai.com/v1", Provider = ProviderKind.OpenAiResponses,
                Model = _settings.CodexModel, ReasoningEffort = _settings.CodexReasoningEffort,
                ThinkingBudgetTokens = null,
                SupportsImages = _accountModels.FirstOrDefault(model => model.Id == _settings.CodexModel)?.SupportsImages == true
            };
        }
        _assistantEntry = null;
        SetRunState(true);
        try
        {
            await _host.RequestAsync<System.Text.Json.JsonElement>("run.continue", new
            {
                sessionId = _session.Info.Id, provider, options = CurrentRunOptions()
            });
            ContinueRunButton.IsVisible = false;
            LocalizedControl.Text(StatusLabel, "RunSubmitted");
        }
        catch { SetRunState(false); throw; }
    });
}
