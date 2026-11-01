using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop.Remote;

/// <summary>Explicit, reviewable deployment; opening this window performs no network or installation action.</summary>
internal sealed class RemoteDeploymentWindow : Window
{
    private readonly HostConnectionProfile _profile;
    private readonly IReadOnlyList<string> _secretEnvironmentVariables;
    private readonly TextBox _package = new();
    private readonly TextBox _install = new();
    private readonly TextBox _data = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _deploy = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;

    internal RemoteDeploymentWindow(HostConnectionProfile profile, IReadOnlyList<string>? secretEnvironmentVariables = null)
    {
        _profile = profile;
        _secretEnvironmentVariables = secretEnvironmentVariables ?? [];
        Title = L.Text("RemoteServiceDeployTitle");
        L.Changed += RefreshTitle;
        LocalizedControl.Content(_deploy, "RemoteServiceDeployStart");
        Width = 690; Height = 590; MinWidth = 520; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _install.Text = Path.GetDirectoryName(profile.HostPath)?.Replace('\\', '/') ?? "/home/user/cogitide-host";
        _data.Text = profile.DataDirectory ?? "/home/user/cogitide-data";
        var browse = new Button { HorizontalAlignment = HorizontalAlignment.Left };
        LocalizedControl.Content(browse, "RemoteServiceChoosePackage");
        browse.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.Text("RemoteServiceChoosePackageTitle"), AllowMultiple = false });
            if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) _package.Text = path;
        };
        _deploy.Click += async (_, _) => await DeployAsync();
        var cancel = new Button();
        LocalizedControl.Content(cancel, "Cancel");
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, _deploy } };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 12,
                Children =
                {
                    Label("RemoteServiceDeployTitle", heading: true),
                    Label("RemoteServiceEndpoint", arguments: [(Func<string>)(() => profile.DisplayLabel)]),
                    Label("RemoteServiceDeployNotice"),
                    Label("RemoteServicePackageFolder"), _package, browse,
                    Label("RemoteServiceNewInstall"), _install,
                    Label("RemoteServiceNewData"), _data,
                    Label("RemoteServiceLifetimeNotice"),
                    _status, buttons
                }
            }
        };
        Closed += (_, _) => { L.Changed -= RefreshTitle; _lifetime.Cancel(); };
    }

    private void RefreshTitle() => Title = L.Text("RemoteServiceDeployTitle");

    private static TextBlock Label(string key, bool heading = false, params object?[] arguments)
    {
        var label = new TextBlock { TextWrapping = TextWrapping.Wrap };
        if (heading) { label.FontSize = 22; label.FontWeight = FontWeight.SemiBold; }
        LocalizedControl.Text(label, key, arguments);
        return label;
    }

    private async Task DeployAsync()
    {
        if (_busy) return;
        _busy = true; _deploy.IsEnabled = false;
        _package.IsEnabled = _install.IsEnabled = _data.IsEnabled = false;
        try
        {
            var request = new RemoteDeploymentRequest(_profile, _package.Text ?? "", _install.Text ?? "", _data.Text ?? "", _secretEnvironmentVariables);
            var profile = await RemoteDeployment.DeployAsync(request, new Progress<string>(message => _status.Text = message), _lifetime.Token);
            Close(profile);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { LocalizedControl.Text(_status, "RemoteServicePartialRetained", e.Message); }
        finally { _busy = false; _deploy.IsEnabled = true; _package.IsEnabled = _install.IsEnabled = _data.IsEnabled = true; }
    }
}
