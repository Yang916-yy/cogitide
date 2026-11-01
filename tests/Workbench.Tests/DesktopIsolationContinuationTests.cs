using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class DesktopIsolationContinuationTests
{
    [Fact]
    public async Task RestoredTaskKeepsIsolationUntilTheUserExplicitlySavesADifferentBudgetPolicy()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = new MainWindow(new DesktopSettings { Run = new(RequireOsIsolation: false), ExecutionMode = ExecutionMode.ReadOnly }, connectHost: false);
            var now = DateTimeOffset.UtcNow;
            var restored = new AgentSession(new("isolated", "Remote task", "/remote/workspace", now, now, "paused"), [new("user", "Continue the original task.")],
                RunCheckpoint: new(1, "run", 0, new(RequireOsIsolation: true), 1, now, "iteration_limit"));
            try
            {
                window.Show();
                window.RestoreSession(restored, []);
                Assert.True(CurrentOptions(window).RequireOsIsolation);
                Assert.Equal(ExecutionMode.ReadOnly, CurrentOptions(window).ExecutionMode);
                var mode = window.FindControl<Button>("ExecutionModeButton")!;
                Assert.Contains("Unsupported native policies stop execution", ToolTip.GetTip(mode)?.ToString());
                Assert.DoesNotContain("No operating-system sandbox", ToolTip.GetTip(mode)?.ToString());

                window.FindControl<Button>("RunBudgetButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                var budget = Assert.IsType<RunBudgetWindow>(Assert.Single(window.OwnedWindows));
                var isolation = Assert.Single(budget.GetVisualDescendants().OfType<CheckBox>(), check => Equals(check.Content, Localization.Text("OsIsolationRequired")));
                Assert.True(isolation.IsChecked);
                isolation.IsChecked = false;
                Assert.Single(budget.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, Localization.Text("Save")))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.False(CurrentOptions(window).RequireOsIsolation);
                Assert.Equal(ExecutionMode.ReadOnly, CurrentOptions(window).ExecutionMode);
                Assert.Contains("No operating-system sandbox", ToolTip.GetTip(mode)?.ToString());

                // An explicit override belongs to the restored task, not another loaded session.
                window.RestoreSession(restored with { Info = restored.Info with { Id = "another-isolated-task" } }, []);
                Assert.True(CurrentOptions(window).RequireOsIsolation);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RestoredCheckpointPolicyIsShownEvenWhenLocalDefaultRequiresIsolation()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = new MainWindow(new DesktopSettings { Run = new(RequireOsIsolation: true) }, connectHost: false);
            var now = DateTimeOffset.UtcNow;
            try
            {
                window.RestoreSession(new(new("ordinary", "Task", Path.GetTempPath(), now, now, "paused"), [new("user", "Original task")],
                    RunCheckpoint: new(1, "run", 0, new(RequireOsIsolation: false), 1, now, "iteration_limit")), []);
                Assert.False(CurrentOptions(window).RequireOsIsolation);
                Assert.Contains("No operating-system sandbox", ToolTip.GetTip(window.FindControl<Button>("ExecutionModeButton")!)?.ToString());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static RunOptions CurrentOptions(MainWindow window) =>
        (RunOptions)typeof(MainWindow).GetMethod("CurrentRunOptions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
}
