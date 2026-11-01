using Avalonia.Interactivity;
using Workbench.Protocol;

namespace Workbench.Desktop;

public partial class MainWindow
{
    private async void ReviewChangesClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        RequireWorkspace();
        if (!_ready) throw new InvalidOperationException(Localization.Text("ReviewConnectHost"));
        var host = _host; var workspace = _workspace;
        void RequireCurrent()
        {
            if (host != _host || workspace != _workspace)
                throw new InvalidOperationException(Localization.Text("ReviewWorkspaceChanged"));
        }
        var window = new ReviewWindow(
            () => { RequireCurrent(); return host.RequestAsync<GitReviewSnapshot>("workspace.review", new { workspacePath = workspace }); },
            (review, file, area, action, hunk, confirmed) =>
            {
                RequireCurrent();
                return host.RequestAsync<GitReviewSnapshot>("workspace.review.change", new { workspacePath = workspace,
                    expectedSnapshot = review.Snapshot, path = file.Path, area, action, hunkId = hunk, confirmed });
            },
            (review, message) =>
            {
                RequireCurrent();
                return host.RequestAsync<GitReviewCommitResult>("workspace.review.commit", new { workspacePath = workspace, expectedSnapshot = review.Snapshot, message });
            },
            (review, action, title, body, baseBranch) =>
            {
                RequireCurrent();
                return host.RequestAsync<GitReviewPublishResult>("workspace.review.publish", new { workspacePath = workspace,
                    expectedSnapshot = review.Snapshot, action, title, body, baseBranch });
            },
            feedback =>
            {
                RequireCurrent();
                PromptBox.Text = string.IsNullOrWhiteSpace(PromptBox.Text) ? feedback : PromptBox.Text.TrimEnd() + "\n\n" + feedback;
            });
        await window.ShowDialog(this);
        if (host == _host && workspace == _workspace) await RefreshWorkspaceAsync();
    });
}
