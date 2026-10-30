using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class DesktopDurabilityTests
{
    [Fact]
    public async Task LongTimelineKeepsABoundedPresentationWindowAndAnEarlierHistoryRoute()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = new MainWindow(new DesktopSettings(), connectHost: false);
            var now = DateTimeOffset.UtcNow;
            var original = Enumerable.Range(0, 1100).Select(index => new ChatMessage("user", "Retained message " + index)).ToArray();
            try
            {
                window.RestoreSession(new(new("session", "History", Path.GetTempPath(), now, now), original), []);
                var timeline = window.FindControl<ListBox>("Timeline")!;
                Assert.Equal(MainWindow.MaximumTimelineEntries, timeline.Items.Count);
                Assert.Equal("Retained message 1099", ((TimelineEntry)timeline.Items[^1]!).Text);
                Assert.True(window.FindControl<Button>("EarlierHistoryButton")!.IsVisible);
                Assert.Contains("complete history is saved", window.FindControl<TextBlock>("HistoryRangeLabel")!.Text);
                Assert.Equal(1100, original.Length);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PausedTaskOffersContinuationAndGenerousBudgetWithoutStartingAHost()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var options = new RunOptions();
            var window = new MainWindow(new DesktopSettings { Run = options }, connectHost: false);
            var now = DateTimeOffset.UtcNow;
            try
            {
                window.Show();
                window.RestoreSession(new(new("session", "Long task", Path.GetTempPath(), now, now, "paused"),
                    [new("user", "Keep the original task."), new("assistant", "Observed progress.")],
                    RunCheckpoint: new(1, "run", 0, options, 256, now, "iteration_limit")), []);
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.FindControl<Button>("ContinueRunButton")!.IsVisible);
                Assert.Contains("256", window.FindControl<TextBlock>("RunBudgetLabel")!.Text);
                Assert.Contains("6h", window.FindControl<TextBlock>("RunBudgetLabel")!.Text);
                Assert.Equal("Paused at budget", window.FindControl<TextBlock>("RunStateLabel")!.Text);
                Assert.NotNull(window.FindControl<TextBox>("FileScope"));
                Assert.NotNull(window.FindControl<Button>("HostConnectionsButton"));
                window.RestoreSession(new(new("session", "Long task", Path.GetTempPath(), now, now, "completed"), []), []);
                Assert.False(window.FindControl<Button>("ContinueRunButton")!.IsVisible);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task BudgetFormUsesSharedUpperLimitsAndDoesNotAlterPermissions()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("zh-CN");
            var options = new RunOptions(ExecutionMode: ExecutionMode.ReadOnly, MaxReportedTokens: 500_000);
            var window = new RunBudgetWindow(options);
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("任务预算", window.Title);
                RunLimits.Validate(options with { MaxIterations = 10_000, TimeoutSeconds = 604_800 });
                Assert.Equal(ExecutionMode.ReadOnly, options.ExecutionMode);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
