using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop;

public partial class MainWindow
{
    private void BindHost(HostClient host)
    {
        host.SecretEnvironmentVariables = new[] { _settings.Provider.ApiKeyEnvironmentVariable }
            .Concat(_settings.RecentProviders.Select(provider => provider.ApiKeyEnvironmentVariable))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        host.Notification += (method, value) => Dispatcher.UIThread.Post(() =>
        {
            if (host == _host) OnNotification(method, value);
        });
        host.Disconnected += message => Dispatcher.UIThread.Post(() =>
        {
            if (host != _host) return;
            _ready = false;
            SetRunState(false);
            StatusLabel.Text = message;
            AddEntry("CONNECTION", message, "#F0B5A7");
        });
    }

    private async void HostConnectionsClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (_running || _submitting) return;
        var profile = await new HostConnectionWindow(_settings).ShowDialog<HostConnectionProfile?>(this);
        if (profile is null) return;
        profile.Validate();
        _fileFilterDelay?.Cancel();
        ++_loadGeneration; ++_filesGeneration; ++_previewGeneration;
        _ready = false;
        var old = _host;
        _host = new();
        BindHost(_host);
        foreach (var terminal in _terminals.ToArray()) terminal.Close();
        await old.DisposeAsync();
        _settings = _settings.RememberConnection(profile) with { LastWorkspace = null, LastSessionId = null };
        _workspace = ""; _session = null;
        _accountModels = []; _accountBinding = null; _accountKnown = false; _modelsLoaded = false;
        _entries.Clear(); _sessions.Clear(); _toolEntries.Clear(); _steeringEntries.Clear(); _files = [];
        FileList.ItemsSource = _files; FilePreview.Text = ""; DiffPreview.Text = ""; CommandOutput.Text = "";
        EarlierHistoryButton.IsVisible = LatestHistoryButton.IsVisible = HistoryRangeLabel.IsVisible = false;
        MoreFilesButton.IsVisible = FilesStartButton.IsVisible = MorePreviewButton.IsVisible = PreviewStartButton.IsVisible = false;
        ApprovalPanel.IsVisible = false; _pendingApproval = null; _assistantEntry = null;
        SaveSettings();
        UpdateWorkspaceLabels(); RefreshTaskControls();
        SetRunState(false);
        await InitializeAsync();
    });

    private async Task OpenRemoteWorkspaceAsync()
    {
        var window = new Window
        {
            Title = L.Text("RemoteOpenProject"), Width = 570, Height = 225, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var path = new TextBox { Text = string.IsNullOrWhiteSpace(_workspace) ? "/home/" : _workspace, PlaceholderText = "/home/user/project" };
        var accept = new Button { Content = L.Text("OpenProject"), HorizontalAlignment = HorizontalAlignment.Right };
        accept.Click += (_, _) => window.Close(path.Text);
        window.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 14,
            Children = { new TextBlock { Text = L.Text("RemoteWorkspaceHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, path, accept }
        };
        var selected = await window.ShowDialog<string?>(this);
        if (string.IsNullOrWhiteSpace(selected)) return;
        RemotePaths.RequireAbsolute(selected);
        var host = _host;
        var response = await host.RequestAsync<JsonElement>("workspace.validate", new { workspacePath = selected });
        if (host != _host) return;
        _workspace = response.GetProperty("workspacePath").GetString()!;
        _session = null; _loadingSelection = true; SessionList.SelectedItem = null; _loadingSelection = false;
        _entries.Clear(); _toolEntries.Clear(); _steeringEntries.Clear(); _assistantEntry = null;
        EarlierHistoryButton.IsVisible = LatestHistoryButton.IsVisible = HistoryRangeLabel.IsVisible = false;
        EmptyState.IsVisible = true;
        LocalizedControl.Text(SessionTitle, "NewSession");
        LocalizedControl.Text(RunStateLabel, "Ready");
        UpdateWorkspaceLabels(); RefreshTaskControls(); SaveSettings();
        await RefreshWorkspaceAsync();
        PromptBox.Focus();
    }
}
