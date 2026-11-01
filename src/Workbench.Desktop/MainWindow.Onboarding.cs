using Avalonia.Controls;
using Workbench.Protocol;

namespace Workbench.Desktop;

public partial class MainWindow
{
    private bool _showingProviderSetup;

    /// <summary>Called after Host initialization and before session creation/submission by the main window.</summary>
    private async Task<bool> EnsureProviderConfiguredAsync(bool force = false)
    {
        var backend = ComposerChoices.NormalizeBackend(_settings.Backend);
        if (!force && backend is "api" or "chatgpt")
        {
            if (!_ready || _running || Program.SmokeTest || Program.ScreenshotPath is not null) return true;
            if (backend == "chatgpt" && (await _host.RequestAsync<AccountState>("account.status")).LoggedIn) return true;
            if (backend == "api" && !string.IsNullOrWhiteSpace(_settings.Provider.Endpoint) && !string.IsNullOrWhiteSpace(_settings.Provider.Model))
            {
                var status = await _host.RequestAsync<ProviderCredentialStatus>("provider.credential.status", new { provider = _settings.Provider });
                if (status.Configured) return true;
            }
        }
        if (!_ready || _showingProviderSetup || Program.SmokeTest || Program.ScreenshotPath is not null) return false;
        _showingProviderSetup = true;
        try
        {
            var connections = new ConnectionsWindow(_host, _settings, preference =>
            {
                _settings = _settings with { Language = preference.Language, ReducedMotion = preference.ReducedMotion };
                SaveSettings();
            });
            var selected = await connections.ShowDialog<DesktopSettings?>(this);
            if (selected is null) return false;
            _modelsLoaded = false;
            await ApplyComposerSettingsAsync(selected.RememberProvider(selected.Provider));
            return ComposerChoices.NormalizeBackend(_settings.Backend) is "api" or "chatgpt";
        }
        finally { _showingProviderSetup = false; }
    }
}
