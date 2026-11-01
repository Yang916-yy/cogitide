using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class ReviewUiTests
{
    [Fact]
    public async Task ReviewActionsAreExplicitAndHunkFeedbackOnlyChangesTheDraft()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var hunk = new GitReviewHunk("hunk", "@@ -10,1 +10,1 @@", "@@ -10,1 +10,1 @@\n-before\n+after\n", 10, 1, 10, 1);
            var diff = new GitReviewDiff(hunk.Patch, [hunk], true, true);
            var review = new GitReviewSnapshot("snapshot", "/repo", "/repo", "feature", new string('a', 40),
                [new("source.cs", " ", "M", new("", [], false, false), diff)], true);
            var changes = new List<(string Action, string? Hunk, bool Confirmed)>();
            var commits = 0; var publishes = 0; var feedback = new List<string>();
            var window = new ReviewWindow(() => Task.FromResult(review), (snapshot, file, area, action, selected, confirmed) =>
            {
                Assert.Equal("snapshot", snapshot.Snapshot); Assert.Equal("source.cs", file.Path); Assert.Equal("worktree", area);
                changes.Add((action, selected, confirmed)); return Task.FromResult(review);
            }, (_, _) => { commits++; return Task.FromResult(new GitReviewCommitResult("commit", review)); },
                (_, _, _, _, _) => { publishes++; return Task.FromResult(new GitReviewPublishResult("push", null, "done", review)); }, feedback.Add);
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Empty(changes); Assert.Equal(0, commits); Assert.Equal(0, publishes);
                var hunks = window.FindControl<ComboBox>("ReviewHunks")!;
                hunks.SelectedIndex = 1;
                window.FindControl<Button>("ReviewAction")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(("stage", "hunk", false), Assert.Single(changes));
                hunks.SelectedIndex = 1;
                window.FindControl<TextBox>("FeedbackText")!.Text = "Keep the original validation.";
                window.FindControl<Button>("AddFeedbackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var text = Assert.Single(feedback);
                Assert.Contains("source.cs", text); Assert.Contains("snapshot", text); Assert.Contains("@@ -10,1 +10,1 @@", text);
                Assert.Contains("line 10", text); Assert.Contains("Keep the original validation.", text);
                Assert.Single(changes); Assert.Equal(0, commits); Assert.Equal(0, publishes);
                window.FindControl<TextBox>("FeedbackText")!.Text = "Unsent feedback stays unchanged.";
                window.FindControl<NumericUpDown>("FeedbackLine")!.Value = 12;
                Localization.SetLanguage("zh-CN");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, hunks.SelectedIndex);
                Assert.Equal("暂存此区块", window.FindControl<Button>("ReviewAction")!.Content);
                Assert.Equal("Unsent feedback stays unchanged.", window.FindControl<TextBox>("FeedbackText")!.Text);
                Assert.Equal(12, window.FindControl<NumericUpDown>("FeedbackLine")!.Value);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SubdirectoryAndUnavailableReviewNeverOfferAnImplicitCommit()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            var review = new GitReviewSnapshot("snapshot", "/repo", "/repo/scope", "feature", new string('b', 40), [], false, "Select the root to commit.");
            var window = new ReviewWindow();
            try
            {
                window.Apply(review);
                Assert.False(window.FindControl<Button>("ReviewAction")!.IsEnabled);
                Assert.False(window.FindControl<Button>("CommitChangesButton")!.IsEnabled);
                Assert.False(window.FindControl<StackPanel>("PublishPanel")!.IsEnabled);
                Assert.Equal(Localization.Text("ReviewScopeWarning"), window.FindControl<TextBlock>("ReviewStatus")!.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
