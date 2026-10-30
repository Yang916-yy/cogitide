using System.Text.Json;
using Avalonia.Interactivity;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop;

public partial class MainWindow
{
    private CancellationTokenSource? _fileFilterDelay;
    private int _filesGeneration;
    private int? _nextFileOffset;
    private string? _fileInventorySnapshot;
    private int _previewGeneration;
    private int? _nextPreviewLine;
    private int? _nextPreviewCharacter;
    private string? _previewSha256;

    private async Task DebounceFilesAsync()
    {
        if (!_ready || FileScope is null) return;
        _fileFilterDelay?.Cancel();
        _fileFilterDelay?.Dispose();
        var delay = _fileFilterDelay = new();
        try
        {
            await Task.Delay(350, delay.Token);
            await GuardAsync(() => LoadFilePageAsync(false));
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
    }

    private async Task LoadFilePageAsync(bool next)
    {
        if (!_ready || string.IsNullOrWhiteSpace(_workspace)) return;
        var generation = ++_filesGeneration;
        var workspace = _workspace;
        var host = _host;
        var prefix = FileScope.Text?.Trim() ?? "";
        var filter = FileFilter.Text?.Trim() ?? "";
        if (next && _nextFileOffset is null) return;
        var result = await host.RequestAsync<ToolResult>("workspace.files.page", new
        {
            workspacePath = workspace, pathPrefix = prefix, filter,
            offset = next ? _nextFileOffset : 0, limit = 200,
            expectedSnapshot = next ? _fileInventorySnapshot : null
        });
        if (generation != _filesGeneration || workspace != _workspace || host != _host) return;
        if (result.IsError) throw new InvalidDataException(result.Content);
        using var document = JsonDocument.Parse(result.Content);
        var page = document.RootElement;
        _files = page.GetProperty("files").EnumerateArray().Select(value => value.GetString()!).ToArray();
        FileList.ItemsSource = _files;
        _nextFileOffset = page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextOffset").GetInt32();
        _fileInventorySnapshot = page.GetProperty("snapshot").GetString();
        MoreFilesButton.IsVisible = _nextFileOffset is not null;
        FilesStartButton.IsVisible = page.GetProperty("offset").GetInt32() > 0;
        FilePageStatus.Text = L.Format("FilePageRange", page.GetProperty("offset").GetInt32() + 1,
            page.GetProperty("offset").GetInt32() + _files.Length, page.GetProperty("totalMatchesInInventory").GetInt32());
        if (page.GetProperty("inventoryTruncated").GetBoolean() || page.GetProperty("skippedEntries").GetInt32() > 0)
            FilePageStatus.Text += " · " + L.Text("FileInventoryNarrow");
    }

    private async void MoreFilesClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => LoadFilePageAsync(true));
    private async void FilesStartClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => LoadFilePageAsync(false));

    private async Task LoadPreviewPageAsync(string path, bool next)
    {
        var generation = ++_previewGeneration;
        var workspace = _workspace;
        var host = _host;
        if (next && _nextPreviewLine is null) return;
        var result = await host.RequestAsync<ToolResult>("workspace.read.page", new
        {
            workspacePath = workspace, path,
            offset = next ? _nextPreviewLine : 0,
            characterOffset = next ? _nextPreviewCharacter ?? 0 : 0,
            expectedSha256 = next ? _previewSha256 : null,
            limit = 200
        });
        if (generation != _previewGeneration || workspace != _workspace || host != _host || FileList.SelectedItem as string != path) return;
        if (result.IsError) throw new InvalidDataException(result.Content);
        using var document = JsonDocument.Parse(result.Content);
        var page = document.RootElement;
        _previewSha256 = page.GetProperty("sha256").GetString();
        _nextPreviewLine = page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextOffset").GetInt32();
        _nextPreviewCharacter = page.GetProperty("nextCharacterOffset").ValueKind == JsonValueKind.Null ? null : page.GetProperty("nextCharacterOffset").GetInt32();
        FilePreview.Text = page.GetProperty("content").GetString() ?? "";
        PreviewPath.Text = path + " · " + L.Format("FilePreviewPosition", page.GetProperty("startLine").GetInt32(), page.GetProperty("startColumn").GetInt32());
        MorePreviewButton.IsVisible = _nextPreviewLine is not null;
        PreviewStartButton.IsVisible = next;
    }

    private async void MorePreviewClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (FileList.SelectedItem is string path) await LoadPreviewPageAsync(path, true);
    });

    private async void PreviewStartClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (FileList.SelectedItem is string path) await LoadPreviewPageAsync(path, false);
    });
}
