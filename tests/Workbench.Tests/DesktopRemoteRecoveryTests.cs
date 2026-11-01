using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Desktop;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class DesktopRemoteRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoherentMessageCursorRebuildsInFlightDeltasAndLateCompletionUpdatesOneAssistantCard(bool snapshotAfterMessageSave)
    {
        var directory = Path.Combine(Path.GetTempPath(), "cogitide-recovery-presentation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new SqliteSessionStore(Path.Combine(directory, "sessions.db"));
            var original = SessionRecoveryCursorTests.Session();
            await store.SaveAsync(original);
            await store.AppendEventAsync(new("session", "run", "message.delta", "Hello"));
            await store.SaveAsync(original with { RunCheckpoint = original.RunCheckpoint! with { Usage = new(4, 1, 1) } });
            var viewBeforeConcurrentSave = (await store.ReadViewAsync("session"))!;
            await store.AppendEventAsync(new("session", "run", "message.delta", " world"));
            await store.SaveAsync(original with { Messages = [.. original.Messages, new("assistant", "Hello world")] });
            var view = snapshotAfterMessageSave ? (await store.ReadViewAsync("session"))! : viewBeforeConcurrentSave;
            await store.AppendEventAsync(new("session", "run", "message.completed", "Hello world"));
            var pages = new List<SessionEventReplayPage>();
            var cursor = view.MessageEventSequence!.Value;
            long? upper = null;
            do
            {
                var page = await store.EventsSincePageAsync("session", cursor, upper, limit: 1);
                pages.Add(page); upper ??= page.ThroughSequence; cursor = page.NextAfterSequence;
            } while (pages[^1].HasMore);
            using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
            await session.Dispatch(() =>
            {
                Localization.SetLanguage("en-US");
                var window = new MainWindow(new DesktopSettings(), connectHost: false);
                try
                {
                    window.RestoreSession(new(view.Info, view.Messages, view.Compaction, view.RunCheckpoint), []);
                    window.PrepareMessageRecovery(view);
                    foreach (var page in pages) window.ApplyRecoveredJournalPage(page);
                    var entries = window.FindControl<ListBox>("Timeline")!.Items.Cast<TimelineEntry>().ToArray();
                    var assistant = Assert.Single(entries, entry => entry.Label == "ASSISTANT");
                    Assert.Equal("Hello world", assistant.Text);
                    foreach (var evt in pages.SelectMany(page => page.Items))
                        Assert.False(window.AcceptPresentationNotification("agent.event", Wire.Element(evt)));
                    // A late lower ID above the proven prefix remains eligible; a live maximum is never a filter.
                    Assert.True(window.AcceptPresentationNotification("agent.event", Wire.Element(new AgentEvent("session", "run", "fixture", Sequence: 9))));
                    Assert.True(window.AcceptPresentationNotification("agent.event", Wire.Element(new AgentEvent("session", "run", "fixture", Sequence: 7))));
                }
                finally { window.Close(); }
            }, CancellationToken.None);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void BoundedEventIdentityWindowPreservesLegitimateOutOfOrderEventsAndSuppressesExactDuplicates()
    {
        var window = new PresentedEventWindow(capacity: 3);
        AgentEvent E(long sequence, string session = "selected") => new(session, "run", "message.delta", "text", Sequence: sequence);
        window.Reset("selected", [E(10)]);
        Assert.True(window.Accept(E(8)));
        Assert.True(window.Accept(E(9)));
        Assert.False(window.Accept(E(8)));
        Assert.False(window.Accept(E(100, "another-session")));
        Assert.True(window.Accept(E(11)));
        Assert.Equal(3, window.Count);
        Assert.True(window.Accept(E(10))); // Explicitly bounded retention, not unbounded historic deduplication.
        Assert.Equal(3, window.Count);
        window.SelectSession("new");
        Assert.True(window.Accept(E(8, "new")));
        Assert.Equal(1, window.Count);
    }

    [Fact]
    public void ReconnectKeepsSelectionOnlyForTheSamePinnedServiceEndpoint()
    {
        var profile = new HostConnectionProfile
        {
            Kind = HostConnectionKind.Ssh, Destination = "fixture-host", HostPath = "/opt/cogitide/Workbench.Host",
            DurableService = true, ServiceId = Guid.NewGuid().ToString("N"), DataDirectory = "/home/user/state"
        };
        Assert.True(MainWindow.CanRecoverRemoteConnection(profile, profile with { Name = "Renamed" }));
        Assert.False(MainWindow.CanRecoverRemoteConnection(profile, profile with { ServiceId = Guid.NewGuid().ToString("N") }));
        Assert.False(MainWindow.CanRecoverRemoteConnection(profile, profile with { Destination = "other-host" }));
        Assert.False(MainWindow.CanRecoverRemoteConnection(profile, profile with { DurableService = false }));
    }

    [Fact]
    public async Task ActiveRestorationAndServiceDisconnectKeepOutcomeUnknownWithoutSendingActions()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = new MainWindow(new DesktopSettings(), connectHost: false);
            var now = DateTimeOffset.UtcNow;
            var loaded = new AgentSession(new("selected", "Waiting task", Path.GetTempPath(), now, now, "waiting_approval"),
                [new("user", "Keep the registered request pending.")], RunCheckpoint: new(1, "run", 0, new(), 1, now));
            try
            {
                window.RestoreSession(loaded, [new("selected", "run", "approval.requested", Sequence: 5)]);
                Assert.Equal(Localization.Text("AwaitingApproval"), window.FindControl<TextBlock>("RunStateLabel")!.Text);
                Assert.False(window.FindControl<Button>("HostConnectionsButton")!.IsEnabled);
                Assert.False(window.AcceptPresentationNotification("agent.event", Wire.Element(new AgentEvent("selected", "run", "approval.requested", Sequence: 5))));
                Assert.True(window.AcceptPresentationNotification("agent.event", Wire.Element(new AgentEvent("selected", "run", "message.delta", "late", Sequence: 4))));
                var profile = new HostConnectionProfile { Kind = HostConnectionKind.Ssh, Destination = "fixture", DurableService = true, ServiceId = Guid.NewGuid().ToString("N"), DataDirectory = "/tmp/state" };
                window.PresentHostDisconnect(profile, "Synthetic transport loss.");
                Assert.Contains("may still be running", window.FindControl<TextBlock>("StatusLabel")!.Text);
                Assert.True(window.FindControl<Button>("HostConnectionsButton")!.IsEnabled);
                Assert.False(window.FindControl<Border>("ApprovalPanel")!.IsVisible);
                Assert.Equal("waiting_approval", loaded.Info.Status);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DetachedConnectionControlsAndDeploymentTitleUseLiveLanguageResources()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("zh-CN");
            var window = new HostConnectionWindow(new DesktopSettings());
            var deployment = new RemoteDeploymentWindow(new HostConnectionProfile { Kind = HostConnectionKind.Ssh, Destination = "fixture" });
            try
            {
                Assert.Equal("停止服务及其任务", window.FindControl<Button>("StopServiceButton")!.Content);
                Assert.Equal("部署预构建的远端 Host", deployment.Title);
                Localization.SetLanguage("en-US");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Stop service and its runs", window.FindControl<Button>("StopServiceButton")!.Content);
                Assert.Equal("Deploy a prebuilt remote Host", deployment.Title);
            }
            finally { deployment.Close(); window.Close(); }
        }, CancellationToken.None);
    }
}
