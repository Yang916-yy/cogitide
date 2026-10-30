using Avalonia.Controls;
using Avalonia.Interactivity;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop.Remote;

internal sealed partial class HostConnectionWindow : Window
{
    private HostConnectionProfile _editing = HostConnectionProfile.Local;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initializing = true;

    public HostConnectionWindow(DesktopSettings settings)
    {
        InitializeComponent();
        ProfilePicker.ItemsSource = new[] { HostConnectionProfile.Local }.Concat(settings.ConnectionProfiles ?? []).ToArray();
        ProfilePicker.SelectedItem = settings.ActiveConnection;
        if (ProfilePicker.SelectedIndex < 0) ProfilePicker.SelectedIndex = 0;
        NewWslButton.IsEnabled = OperatingSystem.IsWindows();
        _initializing = false;
        ShowProfile(ProfilePicker.SelectedItem as HostConnectionProfile ?? HostConnectionProfile.Local);
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private void ProfileChanged(object? sender, SelectionChangedEventArgs arguments)
    {
        if (!_initializing && ProfilePicker.SelectedItem is HostConnectionProfile profile) ShowProfile(profile);
    }

    private void ShowProfile(HostConnectionProfile profile)
    {
        _editing = profile;
        RemoteFields.IsVisible = profile.IsRemote;
        NameBox.Text = profile.Name;
        DestinationBox.Text = profile.Destination;
        HostPathBox.Text = profile.HostPath;
        DotnetPathBox.Text = profile.DotnetPath;
        DataDirectoryBox.Text = profile.DataDirectory;
        WslDiscoveryPanel.IsVisible = profile.Kind == HostConnectionKind.Wsl;
        DistributionPicker.IsVisible = false;
        LocalizedControl.Text(DestinationLabel, profile.Kind == HostConnectionKind.Wsl ? "RemoteWslDistribution" : "RemoteSshDestination");
        LocalizedControl.Text(TransportNotice, profile.Kind == HostConnectionKind.Wsl ? "RemoteWslNotice" : "RemoteSshNotice");
        NoticeLabel.Text = "";
    }

    private void NewSshClick(object? sender, RoutedEventArgs arguments) => NewProfile(HostConnectionKind.Ssh);
    private void NewWslClick(object? sender, RoutedEventArgs arguments) => NewProfile(HostConnectionKind.Wsl);

    private void NewProfile(HostConnectionKind kind)
    {
        _initializing = true;
        ProfilePicker.SelectedIndex = -1;
        _initializing = false;
        ShowProfile(new() { Kind = kind });
        DestinationBox.Focus();
    }

    private async void DiscoverWslClick(object? sender, RoutedEventArgs arguments)
    {
        DiscoverWslButton.IsEnabled = false;
        try
        {
            var distributions = await WslDistributions.ListAsync(_lifetime.Token);
            DistributionPicker.ItemsSource = distributions;
            DistributionPicker.IsVisible = distributions.Count > 0;
            LocalizedControl.Text(NoticeLabel, distributions.Count > 0 ? "RemoteWslDiscovered" : "RemoteNoWsl");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { if (!_lifetime.IsCancellationRequested) LocalizedControl.Text(NoticeLabel, "RemoteWslDiscoveryFailed"); }
        finally { DiscoverWslButton.IsEnabled = true; }
    }

    private void DistributionChanged(object? sender, SelectionChangedEventArgs arguments)
    {
        if (DistributionPicker.SelectedItem is string distribution) DestinationBox.Text = distribution;
    }

    internal HostConnectionProfile ReadProfile()
    {
        if (!_editing.IsRemote) return HostConnectionProfile.Local;
        var profile = _editing with
        {
            Name = NameBox.Text?.Trim() ?? "", Destination = DestinationBox.Text?.Trim() ?? "",
            HostPath = HostPathBox.Text ?? "", DotnetPath = DotnetPathBox.Text ?? "dotnet",
            DataDirectory = string.IsNullOrWhiteSpace(DataDirectoryBox.Text) ? null : DataDirectoryBox.Text
        };
        profile.Validate();
        return profile;
    }

    private void ConnectClick(object? sender, RoutedEventArgs arguments)
    {
        try { Close(ReadProfile()); }
        catch (ArgumentException exception) { NoticeLabel.Text = exception.Message; }
    }

    private void CancelClick(object? sender, RoutedEventArgs arguments) => Close(null);
}
