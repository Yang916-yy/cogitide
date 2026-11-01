using Avalonia.Controls;
using Avalonia.Interactivity;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop.Remote;

internal sealed partial class HostConnectionWindow : Window
{
    private HostConnectionProfile _editing = HostConnectionProfile.Local;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initializing = true;
    private readonly IReadOnlyList<string> _secretEnvironmentVariables;

    public HostConnectionWindow(DesktopSettings settings)
    {
        _secretEnvironmentVariables = new[] { settings.Provider.ApiKeyEnvironmentVariable }
            .Concat(settings.RecentProviders.Select(provider => provider.ApiKeyEnvironmentVariable)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
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
        DurableServiceBox.IsChecked = profile.DurableService;
        ServiceIdBox.Text = profile.ServiceId;
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

    internal HostConnectionProfile ReadProfile(bool attachService = true)
    {
        if (!_editing.IsRemote) return HostConnectionProfile.Local;
        var profile = _editing with
        {
            Name = NameBox.Text?.Trim() ?? "", Destination = DestinationBox.Text?.Trim() ?? "",
            HostPath = HostPathBox.Text ?? "", DotnetPath = DotnetPathBox.Text ?? "dotnet",
            DataDirectory = string.IsNullOrWhiteSpace(DataDirectoryBox.Text) ? null : DataDirectoryBox.Text,
            DurableService = attachService && DurableServiceBox.IsChecked == true,
            ServiceId = string.IsNullOrWhiteSpace(ServiceIdBox.Text) ? null : ServiceIdBox.Text.Trim()
        };
        profile.Validate();
        return profile;
    }

    private async void DeployClick(object? sender, RoutedEventArgs arguments)
    {
        try
        {
            var profile = await new RemoteDeploymentWindow(ReadProfile(attachService: false), _secretEnvironmentVariables).ShowDialog<HostConnectionProfile?>(this);
            if (profile is not null) ShowProfile(profile);
        }
        catch (Exception e) { NoticeLabel.Text = e.Message; }
    }

    private async void StartServiceClick(object? sender, RoutedEventArgs arguments)
    {
        SetServiceBusy(true);
        try
        {
            var profile = await RemoteDeployment.StartAsync(ReadProfile(attachService: false), _lifetime.Token, _secretEnvironmentVariables);
            ShowProfile(profile);
            LocalizedControl.Text(NoticeLabel, "RemoteServiceStarted");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { NoticeLabel.Text = e.Message; }
        finally { SetServiceBusy(false); }
    }

    private async void StopServiceClick(object? sender, RoutedEventArgs arguments)
    {
        SetServiceBusy(true);
        try
        {
            await RemoteDeployment.StopAsync(ReadProfile() with { DurableService = true }, _lifetime.Token, _secretEnvironmentVariables);
            LocalizedControl.Text(NoticeLabel, "RemoteServiceStopped");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) { NoticeLabel.Text = e.Message; }
        finally { SetServiceBusy(false); }
    }

    private void SetServiceBusy(bool busy)
    {
        DeployButton.IsEnabled = StartServiceButton.IsEnabled = StopServiceButton.IsEnabled = ConnectButton.IsEnabled = !busy;
        if (busy) LocalizedControl.Text(NoticeLabel, "RemoteServiceBusy");
    }

    private void ConnectClick(object? sender, RoutedEventArgs arguments)
    {
        try { Close(ReadProfile()); }
        catch (ArgumentException exception) { NoticeLabel.Text = exception.Message; }
    }

    private void CancelClick(object? sender, RoutedEventArgs arguments) => Close(null);
}
