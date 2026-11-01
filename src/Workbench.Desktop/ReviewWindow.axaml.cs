using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop;

/// <summary>Presentation only. All repository observations and mutations run on the selected Host.</summary>
public partial class ReviewWindow : Window
{
    private readonly Func<Task<GitReviewSnapshot>> _refresh;
    private readonly Func<GitReviewSnapshot, GitReviewFile, string, string, string?, bool, Task<GitReviewSnapshot>> _change;
    private readonly Func<GitReviewSnapshot, string, Task<GitReviewCommitResult>> _commit;
    private readonly Func<GitReviewSnapshot, string, string?, string?, string?, Task<GitReviewPublishResult>> _publish;
    private readonly Action<string> _feedback;
    private GitReviewSnapshot? _review;
    private string? _prUrl;
    private bool _updating;
    private bool _busy;
    private readonly CancellationTokenSource _lifetime = new();
    private sealed record HunkChoice(string Label, GitReviewHunk? Hunk) { public override string ToString() => Label; }
    private GitReviewFile? SelectedFile => ReviewFiles.SelectedItem as GitReviewFile;
    private string Area => (ReviewArea.SelectedItem as ComboBoxItem)?.Tag as string ?? "worktree";
    private GitReviewHunk? SelectedHunk => (ReviewHunks.SelectedItem as HunkChoice)?.Hunk;

    public ReviewWindow() : this(() => Task.FromException<GitReviewSnapshot>(new InvalidOperationException(L.Text("ReviewConnectHost"))),
        (_, _, _, _, _, _) => Task.FromException<GitReviewSnapshot>(new InvalidOperationException(L.Text("ReviewConnectHost"))),
        (_, _) => Task.FromException<GitReviewCommitResult>(new InvalidOperationException(L.Text("ReviewConnectHost"))),
        (_, _, _, _, _) => Task.FromException<GitReviewPublishResult>(new InvalidOperationException(L.Text("ReviewConnectHost"))), _ => { }) { }

    internal ReviewWindow(Func<Task<GitReviewSnapshot>> refresh,
        Func<GitReviewSnapshot, GitReviewFile, string, string, string?, bool, Task<GitReviewSnapshot>> change,
        Func<GitReviewSnapshot, string, Task<GitReviewCommitResult>> commit,
        Func<GitReviewSnapshot, string, string?, string?, string?, Task<GitReviewPublishResult>> publish, Action<string> feedback)
    {
        _refresh = refresh; _change = change; _commit = commit; _publish = publish; _feedback = feedback;
        InitializeComponent();
        L.Changed += RefreshLocalization;
        ReviewPatch.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("Diff");
        ReviewArea.SelectedIndex = 0;
        Opened += async (_, _) => await RunAsync(async () => Apply(await _refresh()));
        Closed += (_, _) => { L.Changed -= RefreshLocalization; _lifetime.Cancel(); _lifetime.Dispose(); };
        UpdateActions();
    }

    internal void Apply(GitReviewSnapshot review)
    {
        var selectedPath = SelectedFile?.Path;
        _updating = true;
        try
        {
            _review = review;
            RepositoryLabel.Text = $"{review.Branch}   {review.WorkspacePath}";
            LocalizedControl.Text(ChangeCount, "ReviewChangedCount", review.Files.Count);
            ReviewFiles.ItemsSource = review.Files;
            ReviewFiles.SelectedItem = review.Files.FirstOrDefault(file => file.Path == selectedPath) ?? review.Files.FirstOrDefault();
            if (review.RepositoryRoot != review.WorkspacePath) LocalizedControl.Text(ReviewStatus, "ReviewScopeWarning");
            else if (review.Files.Count == 0) LocalizedControl.Text(ReviewStatus, "ReviewCleanWarning");
            else if (review.Warning is not null) ReviewStatus.Text = review.Warning;
            else LocalizedControl.Text(ReviewStatus, "ReviewBeforeCommit");
        }
        finally { _updating = false; }
        ShowFile();
    }

    private void FileSelectionChanged(object? sender, SelectionChangedEventArgs e) { if (!_updating) ShowFile(); }
    private void AreaSelectionChanged(object? sender, SelectionChangedEventArgs e) { if (!_updating && ReviewHunks is not null) ShowFile(); }
    private void HunkSelectionChanged(object? sender, SelectionChangedEventArgs e) { if (!_updating) ShowPatch(); }

    private void RefreshLocalization()
    {
        var hunk = SelectedHunk?.Id; var line = FeedbackLine.Value;
        ShowFile(hunk);
        FeedbackLine.Value = line;
    }

    private void ShowFile(string? selectedHunkId = null)
    {
        if (ReviewHunks is null) return;
        var file = SelectedFile;
        var diff = Area == "index" ? file?.Index : file?.Worktree;
        _updating = true;
        try
        {
            SelectedPath.Text = file?.Path ?? L.Text("ReviewNoSelection");
            var choices = new[] { new HunkChoice(L.Text("ReviewWholeFile"), null) }
                .Concat(diff?.Hunks.Select(hunk => new HunkChoice(hunk.Header, hunk)) ?? []).ToArray();
            ReviewHunks.ItemsSource = choices;
            ReviewHunks.SelectedItem = choices.FirstOrDefault(choice => choice.Hunk?.Id == selectedHunkId) ?? choices[0];
        }
        finally { _updating = false; }
        ShowPatch();
    }

    private void ShowPatch()
    {
        var file = SelectedFile;
        var diff = Area == "index" ? file?.Index : file?.Worktree;
        var hunk = SelectedHunk;
        ReviewPatch.Text = hunk?.Patch ?? diff?.Patch ?? "";
        if (string.IsNullOrEmpty(ReviewPatch.Text)) ReviewPatch.Text = file?.UnavailableReason ?? L.Text("ReviewNoAreaChanges");
        if (file?.UnavailableReason is not null) DiffHint.Text = file.UnavailableReason;
        else LocalizedControl.Text(DiffHint, diff?.IsBinary == true ? "ReviewBinaryHint" : diff?.CanSelectHunks == true ? "ReviewHunkHint" : "ReviewWholeFileHint");
        FeedbackLine.Value = Math.Max(1, hunk is null ? 1 : hunk.NewLines == 0 ? hunk.OldStart : hunk.NewStart);
        UpdateActions();
    }

    private void UpdateActions()
    {
        if (ReviewAction is null || PublishPanel is null) return;
        var file = SelectedFile;
        var diff = Area == "index" ? file?.Index : file?.Worktree;
        var changeable = !_busy && file is not null && file.UnavailableReason is null && diff?.HasChanges == true &&
            (SelectedHunk is null || diff.CanSelectHunks);
        LocalizedControl.Content(ReviewAction, Area == "index" ? SelectedHunk is null ? "ReviewUnstageFile" : "ReviewUnstageHunk"
            : SelectedHunk is null ? "ReviewStageFile" : "ReviewStageHunk");
        ReviewAction.IsEnabled = changeable;
        RevertChange.IsEnabled = changeable && Area == "worktree";
        RefreshReviewButton.IsEnabled = !_busy;
        ReviewFiles.IsEnabled = !_busy; ReviewArea.IsEnabled = !_busy; ReviewHunks.IsEnabled = !_busy;
        PublishPanel.IsEnabled = !_busy && _review is not null && _review.RepositoryRoot == _review.WorkspacePath;
        CommitChangesButton.IsEnabled = !_busy && _review?.CanCommit == true && _review.Files.Any(item => item.Index.HasChanges);
        PushBranchButton.IsEnabled = CreatePrButton.IsEnabled = !_busy && _review?.HeadCommit is not null && _review.Branch != "Detached HEAD";
        AddFeedbackButton.IsEnabled = !_busy && file is not null;
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; UpdateActions();
        try { await operation(); }
        catch (Exception ex) { if (!_lifetime.IsCancellationRequested) ReviewStatus.Text = ex.Message; }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) UpdateActions(); }
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e) => await RunAsync(async () => Apply(await _refresh()));
    private async void ChangeClick(object? sender, RoutedEventArgs e) => await ApplyChangeAsync(Area == "index" ? "unstage" : "stage", false);
    private async void RevertClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file) return;
        var subject = SelectedHunk is null ? L.Text("ReviewAllWorking") : SelectedHunk.Header;
        if (await ConfirmAsync(L.Text("ReviewDiscardTitle"), L.Format("ReviewDiscardMessage", subject, file.Path), L.Text("ReviewDiscardAction")))
            await ApplyChangeAsync("revert", true);
    }
    private async Task ApplyChangeAsync(string action, bool confirmed)
    {
        if (_review is not { } review || SelectedFile is not { } file) return;
        var area = Area; var hunk = SelectedHunk?.Id;
        await RunAsync(async () => Apply(await _change(review, file, area, action, hunk, confirmed)));
    }

    private void FeedbackClick(object? sender, RoutedEventArgs e)
    {
        if (_review is not { } review || SelectedFile is not { } file || string.IsNullOrWhiteSpace(FeedbackText.Text)) return;
        var feedback = new GitReviewFeedback(review.Snapshot, file.Path, Area, SelectedHunk?.Header,
            decimal.ToInt32(FeedbackLine.Value ?? 1), FeedbackText.Text.Trim());
        try
        {
            _feedback(FormatFeedback(feedback));
            FeedbackText.Text = "";
            LocalizedControl.Text(ReviewStatus, "ReviewFeedbackAdded");
        }
        catch (Exception ex) { ReviewStatus.Text = ex.Message; }
    }

    internal static string FormatFeedback(GitReviewFeedback feedback) =>
        $"Review feedback for {feedback.Path} ({feedback.Area}, line {feedback.Line?.ToString() ?? "file"})\n" +
        $"Review snapshot: {feedback.Snapshot}\n" + (feedback.HunkHeader is null ? "" : feedback.HunkHeader + "\n") + feedback.Text;

    private async void CommitClick(object? sender, RoutedEventArgs e)
    {
        if (_review is not { } review) return;
        await RunAsync(async () =>
        {
            var result = await _commit(review, CommitMessage.Text ?? "");
            Apply(result.Review); CommitMessage.Text = "";
            LocalizedControl.Text(ReviewStatus, "ReviewCommitted", result.Commit[..Math.Min(12, result.Commit.Length)]);
        });
    }

    private async void PushClick(object? sender, RoutedEventArgs e)
    {
        if (_review is not { } review || !await ConfirmAsync(L.Text("ReviewPushTitle"), L.Format("ReviewPushMessage", review.Branch), L.Text("ReviewPushAction"))) return;
        await PublishAsync(review, "push");
    }
    private async void CreatePrClick(object? sender, RoutedEventArgs e)
    {
        if (_review is not { } review || !await ConfirmAsync(L.Text("ReviewPrConfirmTitle"), L.Format("ReviewPrConfirmMessage", review.Branch, PrTitle.Text, PrBody.Text), L.Text("ReviewCreatePrAction"))) return;
        await PublishAsync(review, "create_pr");
    }
    private async Task PublishAsync(GitReviewSnapshot review, string action) => await RunAsync(async () =>
    {
        var result = await _publish(review, action, PrTitle.Text, PrBody.Text, PrBase.Text);
        Apply(result.Review);
        if (result.Action == "push") LocalizedControl.Text(ReviewStatus, "ReviewPushed");
        else LocalizedControl.Text(ReviewStatus, result.Url is null ? "ReviewPrCheckRemote" : "ReviewPrCreated");
        _prUrl = result.Url; PrLinkButton.IsVisible = _prUrl is not null;
    });
    private void OpenPrClick(object? sender, RoutedEventArgs e)
    {
        if (_prUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        var dialog = new Window { Title = title, Width = 470, Height = 300, MinHeight = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = true };
        var cancel = new Button { Content = L.Text("Cancel") };
        var confirm = new Button { Content = action, Classes = { "primary" } };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        dialog.Content = new Grid
        {
            Margin = new Thickness(24), RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22 } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
                    [Grid.RowProperty] = 1, Children = { cancel, confirm } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }
}
