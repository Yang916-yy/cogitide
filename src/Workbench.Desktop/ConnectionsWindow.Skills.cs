using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Workbench.Protocol;

namespace Workbench.Desktop;

internal partial class ConnectionsWindow
{
    private readonly string? _workspacePath;
    private readonly CancellationTokenSource _skillsLifetime = new();
    private bool _skillsLoaded;
    private bool _skillsBusy;

    internal string? RequestedSection { get; private set; }

    private void SettingsNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _closed || !ReferenceEquals(e.Source, SettingsNavigation) || SettingsNavigation.SelectedIndex != 2 || _skillsLoaded || _skillsBusy) return;
        _ = RefreshSkillsAsync();
    }

    private void OpenHostConnectionsClick(object? sender, RoutedEventArgs e)
    {
        RequestedSection = "connections";
        Close((DesktopSettings?)null);
    }

    private void OpenRunBudgetClick(object? sender, RoutedEventArgs e)
    {
        RequestedSection = "budget";
        Close((DesktopSettings?)null);
    }

    private async void BrowseSkillFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_host.IsRemote || _workspacePath is null || _skillsBusy || !StorageProvider.CanPickFolder) return;
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Localization.Text("SkillsImport"), AllowMultiple = false
            });
            if (!_closed && folders.FirstOrDefault()?.TryGetLocalPath() is { } path) SkillImportPathBox.Text = path;
        }
        catch (Exception ex) { if (!_closed) LocalizedControl.Text(SkillsNoticeLabel, "SkillsInstallFailed", ex.Message); }
    }

    private async void RefreshSkillsClick(object? sender, RoutedEventArgs e) => await RefreshSkillsAsync();

    private async Task RefreshSkillsAsync()
    {
        if (_workspacePath is null || _skillsBusy || _closed) return;
        SetSkillsBusy(true);
        LocalizedControl.Text(SkillsInstalledSummary, "SkillsWorking");
        try { await LoadInstalledSkillsAsync(); }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) LocalizedControl.Text(SkillsNoticeLabel, "SkillsLoadFailed", ex.Message); }
        finally { if (!_closed) SetSkillsBusy(false); }
    }

    private async Task LoadInstalledSkillsAsync()
    {
        var catalog = await _host.RequestAsync<JsonElement>("project.skills", new { workspacePath = _workspacePath }, _skillsLifetime.Token);
        if (_closed) return;
        var skills = catalog.GetProperty("skills").EnumerateArray().Select(skill => new SkillListItem(
            skill.GetProperty("name").GetString() ?? "",
            skill.GetProperty("description").GetString() ?? "",
            skill.GetProperty("path").GetString() ?? "")).ToArray();
        InstalledSkillsList.ItemsSource = skills;
        InstalledSkillsList.IsVisible = skills.Length > 0;
        _skillsLoaded = true;
        LocalizedControl.Text(SkillsInstalledSummary, skills.Length == 0 ? "SkillsNoneInstalled" : "SkillsInstalledCount", skills.Length);
        if (catalog.TryGetProperty("diagnostics", out var diagnostics) && diagnostics.GetArrayLength() > 0)
            SkillsNoticeLabel.Text = string.Join(Environment.NewLine, diagnostics.EnumerateArray().Select(value => value.GetString()));
    }

    private async void ImportSkillClick(object? sender, RoutedEventArgs e)
    {
        if (_workspacePath is null || _skillsBusy || _closed) return;
        var sourcePath = SkillImportPathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(sourcePath)) { LocalizedControl.Text(SkillsNoticeLabel, "SkillsImportPathRequired"); return; }
        SetSkillsBusy(true);
        LocalizedControl.Text(SkillsNoticeLabel, "SkillsImporting");
        try
        {
            var result = await _host.RequestAsync<SkillInstallResult>("project.skills.import", new { workspacePath = _workspacePath, sourcePath }, _skillsLifetime.Token);
            if (_closed) return;
            await ShowSkillInstalledAsync(result.Name);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) ShowSkillMutationFailure(ex); }
        finally { if (!_closed) SetSkillsBusy(false); }
    }

    private async void SearchSkillsClick(object? sender, RoutedEventArgs e)
    {
        if (_workspacePath is null || _skillsBusy || _closed) return;
        var query = SkillSearchBox.Text?.Trim() ?? "";
        SetSkillsBusy(true);
        SkillCatalogList.ItemsSource = null;
        SkillCatalogList.IsVisible = false;
        SkillCatalogWarningLabel.Text = "";
        LocalizedControl.Text(SkillsNoticeLabel, "SkillsSearching");
        try
        {
            var catalog = await _host.RequestAsync<SkillCatalogSearchResult>("skills.catalog.search", new { query }, _skillsLifetime.Token);
            if (_closed) return;
            var skills = catalog.Results.Select(skill => new SkillCatalogItem(
                skill.Id, skill.Name, skill.Description, skill.SourceLabel + " · " + skill.Revision)).ToArray();
            SkillCatalogList.ItemsSource = skills;
            SkillCatalogList.IsVisible = skills.Length > 0;
            LocalizedControl.Text(SkillsNoticeLabel, skills.Length == 0 ? "SkillsNoMatches" : catalog.Truncated ? "SkillsSearchResultsTruncated" : "SkillsSearchResults", skills.Length);
            SkillCatalogWarningLabel.Text = catalog.Warning;
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) LocalizedControl.Text(SkillsNoticeLabel, "SkillsSearchFailed", ex.Message); }
        finally { if (!_closed) SetSkillsBusy(false); }
    }

    private void SkillCatalogSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (InstallSkillButton is not null) InstallSkillButton.IsEnabled = !_skillsBusy && SkillCatalogList.SelectedItem is SkillCatalogItem;
    }

    private async void InstallSkillClick(object? sender, RoutedEventArgs e)
    {
        if (_workspacePath is null || _skillsBusy || _closed || SkillCatalogList.SelectedItem is not SkillCatalogItem skill) return;
        SetSkillsBusy(true);
        LocalizedControl.Text(SkillsNoticeLabel, "SkillsInstalling");
        try
        {
            var result = await _host.RequestAsync<SkillInstallResult>("project.skills.install", new { workspacePath = _workspacePath, skillId = skill.Id }, _skillsLifetime.Token);
            if (_closed) return;
            await ShowSkillInstalledAsync(result.Name);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex) { if (!_closed) ShowSkillMutationFailure(ex); }
        finally { if (!_closed) SetSkillsBusy(false); }
    }

    private async Task ShowSkillInstalledAsync(string name)
    {
        try
        {
            await LoadInstalledSkillsAsync();
            if (!_closed) LocalizedControl.Text(SkillsNoticeLabel, "SkillInstalled", name);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception ex)
        {
            if (!_closed) LocalizedControl.Text(SkillsNoticeLabel, "SkillInstalledRefreshFailed", name, ex.Message);
        }
    }

    private void ShowSkillMutationFailure(Exception exception)
    {
        var unknown = exception is IOException or OperationCanceledException or ObjectDisposedException or JsonException;
        LocalizedControl.Text(SkillsNoticeLabel, unknown ? "SkillInstallOutcomeUnknown" : "SkillsInstallFailed", exception.Message);
    }

    private void SetSkillsBusy(bool busy)
    {
        _skillsBusy = busy;
        RefreshSkillsButton.IsEnabled = !busy;
        ImportSkillButton.IsEnabled = !busy;
        BrowseSkillFolderButton.IsEnabled = !busy && !_host.IsRemote;
        SearchSkillsButton.IsEnabled = !busy;
        InstallSkillButton.IsEnabled = !busy && SkillCatalogList.SelectedItem is SkillCatalogItem;
    }

    internal sealed record SkillListItem(string Name, string Description, string RelativePath);
    internal sealed record SkillCatalogItem(string Id, string Name, string Description, string SourceLabel);
}
