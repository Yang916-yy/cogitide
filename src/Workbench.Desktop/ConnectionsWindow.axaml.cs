using L = Workbench.Desktop.Localization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using Workbench.Protocol;

namespace Workbench.Desktop;

internal partial class ConnectionsWindow : Window
{
    private readonly HostClient _host;
    private readonly DesktopSettings _settings;
    private string? _authUrl;
    private string? _loginId;
    private bool _loginPending;
    private bool _addingAccount;
    private string? _loginStartBinding;
    private bool _closed;
    private readonly Action<DesktopSettings> _preferencesChanged;
    private bool _initializing = true;
    private bool _updatingProvider;
    private bool _updatingAccounts;
    private bool _switchingAccount;
    private bool _accountChanged;
    private IReadOnlyList<AccountProfile> _profiles = [];
    private AccountState? _account;
    private string? _credentialProfileId;
    private string _credentialEndpoint = "";
    private ProviderKind _credentialFamily;
    private int _keyStatusGeneration;
    private IReadOnlyList<ProviderPreset> _providerPresets = [];
    private readonly bool _requireInitialCredential;

    public ConnectionsWindow(HostClient host, DesktopSettings settings, Action<DesktopSettings> preferencesChanged)
    {
        _host = host;
        _settings = settings;
        _preferencesChanged = preferencesChanged;
        InitializeComponent();
        // TextChanged is dispatcher-posted. Clear an old endpoint's pending secret synchronously
        // when the property changes, before any save/fetch action can read the new address.
        EndpointBox.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) ApiKeyBox.Text = "";
        };
        _requireInitialCredential = settings.Backend == "unconfigured" && string.IsNullOrWhiteSpace(settings.Provider.Endpoint);
        BackendChoice.SelectedIndex = settings.Backend == "chatgpt" ? 0 : settings.Backend == "api" ||
            !string.IsNullOrWhiteSpace(settings.Provider.Endpoint) && !string.IsNullOrWhiteSpace(settings.Provider.Model) ? 1 : -1;
        _credentialProfileId = settings.Provider.CredentialProfileId;
        _credentialEndpoint = settings.Provider.Endpoint;
        _credentialFamily = ComposerChoices.ResolveProvider(settings.Provider);
        LanguagePicker.SelectedIndex = settings.Language switch { "zh-CN" => 1, "en-US" => 2, _ => 0 };
        ReducedMotionToggle.IsChecked = settings.ReducedMotion;
        CompressionGuidanceToggle.IsChecked = settings.Research.CompressionGuidance;
        RepositoryMemoryToggle.IsChecked = settings.Research.RepositoryMemory;
        ReadOnlyPrefetchToggle.IsChecked = settings.Research.ReadOnlyPrefetch;
        ProviderPicker.SelectedIndex = settings.Provider.Provider switch
        { ProviderKind.DeepSeek => 1, ProviderKind.Anthropic => 2, ProviderKind.OpenAiCompatible => 3, ProviderKind.OpenAiResponses => 4, _ => 0 };
        EndpointBox.Text = settings.Provider.Endpoint;
        ApiModelBox.Text = settings.Provider.Model;
        KeyVariableBox.Text = settings.Provider.ApiKeyEnvironmentVariable;
        MaxTokensBox.Value = settings.Provider.MaxOutputTokens;
        ImageCapabilityToggle.IsChecked = settings.Provider.SupportsImages;
        LegacyThinkingToggle.IsChecked = settings.Provider.ThinkingBudgetTokens is not null;
        ThinkingBudgetBox.Value = settings.Provider.ThinkingBudgetTokens ?? 2048;
        UpdateProviderOptions(settings.Provider.ReasoningEffort);
        CodexModelBox.Text = settings.CodexModel;
        _initializing = false;
        UpdateKeyStatus();
        _host.Notification += OnNotification;
        L.Changed += RefreshLocalization;
        UiMotion.Changed += RefreshMotion;
        Closed += (_, _) =>
        {
            _closed = true;
            ApiKeyBox.Text = "";
            _host.Notification -= OnNotification;
            L.Changed -= RefreshLocalization;
            UiMotion.Changed -= RefreshMotion;
            _ = CancelPendingLoginAsync(showNotice: false);
        };
        Opened += async (_, _) => { RefreshMotion(null, EventArgs.Empty); await LoadProviderPresetsAsync(); await RefreshAccountAsync(); };
    }

    private void OnNotification(string method, JsonElement parameters)
    {
        if (method != "account.changed") return;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (parameters.Deserialize<AccountState>(Wire.Json) is { } state)
                {
                    ShowAccount(state);
                    _ = LoadAccountsAsync();
                    if (state.LoggedIn) _ = LoadModelsAsync();
                }
            }
            catch (JsonException) { LocalizedControl.Text(NoticeLabel, "AccountUpdateUnavailable"); }
        });
    }

    private async Task RefreshAccountAsync()
    {
        RefreshAccountButton.IsEnabled = false;
        try
        {
            var state = await _host.RequestAsync<AccountState>("account.status");
            ShowAccount(state);
            await LoadAccountsAsync();
            if (state.LoggedIn) await LoadModelsAsync();
        }
        catch (Exception ex) { LocalizedControl.Text(AccountStatus, "AccountUnavailable", ex.Message); }
        finally { RefreshAccountButton.IsEnabled = true; }
    }

    private void ShowAccount(AccountState state)
    {
        if (_account is { } previous && previous.AccountBinding != state.AccountBinding)
        {
            _accountChanged = true;
            CodexModelBox.Text = "";
            CodexModels.ItemsSource = null;
        }
        _account = state;
        var loginCompleted = state.LoggedIn && (!_addingAccount || state.AccountBinding != _loginStartBinding);
        if (!string.IsNullOrEmpty(state.Error) || loginCompleted)
        {
            _loginId = null;
            _loginPending = false;
            _authUrl = null;
            LoginPanel.IsVisible = false;
        }
        AccountStatus.Text = !state.Available ? state.Error ?? L.Text("OfficialAuthUnavailable")
            : state.LoggedIn ? L.Format("SignedIn", state.Email ?? L.Text("ChatGptAccount"), state.Plan is null ? "" : " · " + state.Plan)
            : L.Text("NotSignedIn");
        LoginButton.IsEnabled = state.Available && !state.LoggedIn && !_loginPending && !_switchingAccount;
        AddAccountButton.IsEnabled = state.Available && !_loginPending && !_switchingAccount;
        AccountPicker.IsEnabled = state.Available && !_loginPending && !_switchingAccount;
        LogoutButton.IsEnabled = state.LoggedIn && !_loginPending && !_switchingAccount;
        if (!string.IsNullOrEmpty(state.Error)) NoticeLabel.Text = state.Error;
    }

    private async Task LoadAccountsAsync()
    {
        var accountBinding = _account?.AccountBinding;
        try
        {
            var profiles = await _host.RequestAsync<AccountProfile[]>("account.list");
            if (_account?.AccountBinding == accountBinding) ShowAccounts(profiles);
        }
        catch (Exception ex) { if (!_closed) LocalizedControl.Text(NoticeLabel, "AccountsUnavailable", ex.Message); }
    }

    internal void ShowAccounts(IReadOnlyList<AccountProfile> profiles)
    {
        if (_closed) return;
        _profiles = profiles;
        _updatingAccounts = true;
        try
        {
            var items = profiles.Select(profile => new ComboBoxItem
            {
                Tag = profile,
                Content = string.Join(" · ", new[] { profile.Email ?? L.Text("ChatGptAccount"), profile.Label,
                    profile.LoggedIn ? null : L.Text("SignInRequired") }.Where(value => !string.IsNullOrWhiteSpace(value)))
            }).ToArray();
            AccountPicker.ItemsSource = items;
            AccountPicker.SelectedItem = items.FirstOrDefault(item => ((AccountProfile)item.Tag!).IsSelected);
            AccountPicker.IsVisible = items.Length > 0;
        }
        finally { _updatingAccounts = false; }
    }

    private async void AccountSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingAccounts || _initializing || _loginPending || _switchingAccount || AccountPicker.SelectedItem is not ComboBoxItem { Tag: AccountProfile profile }) return;
        if (profile.AccountBinding == _account?.AccountBinding) return;
        _switchingAccount = true;
        AccountPicker.IsEnabled = false;
        try
        {
            ShowAccount(await _host.RequestAsync<AccountState>("account.select", new { accountBinding = profile.AccountBinding }));
            await LoadAccountsAsync();
            if (_account?.LoggedIn == true) await LoadModelsAsync();
        }
        catch (Exception ex) { NoticeLabel.Text = ex.Message; ShowAccounts(_profiles); }
        finally
        {
            _switchingAccount = false;
            if (!_closed && _account is { } account) ShowAccount(account);
        }
    }

    private async Task LoadModelsAsync()
    {
        var accountBinding = _account?.AccountBinding;
        try
        {
            var models = await _host.RequestAsync<ModelInfo[]>("model.list");
            if (_closed || accountBinding != _account?.AccountBinding) return;
            CodexModels.ItemsSource = models;
            CodexModels.SelectedItem = models.FirstOrDefault(x => x.Id == CodexModelBox.Text);
        }
        catch (Exception ex) { LocalizedControl.Text(NoticeLabel, "ModelsUnavailable", ex.Message); }
    }

    private async void LoginClick(object? sender, RoutedEventArgs e) => await BeginLoginAsync(newAccount: false);
    private async void AddAccountClick(object? sender, RoutedEventArgs e) => await BeginLoginAsync(newAccount: true);

    private async Task BeginLoginAsync(bool newAccount)
    {
        if (_loginPending || _switchingAccount) return;
        LoginButton.IsEnabled = false;
        AddAccountButton.IsEnabled = false;
        AccountPicker.IsEnabled = false;
        _loginPending = true;
        _addingAccount = newAccount;
        _loginStartBinding = _account?.AccountBinding;
        NoticeLabel.Text = "";
        try
        {
            var login = await _host.RequestAsync<LoginInfo>("account.login", new { newAccount });
            _loginId = login.LoginId;
            _authUrl = login.AuthUrl;
            if (_closed) { await CancelPendingLoginAsync(showNotice: false); return; }
            LoginPanel.IsVisible = true;
            if (login.UserCode is null) LocalizedControl.Text(LoginHint, "CompleteAuthorization");
            else LocalizedControl.Text(LoginHint, "AuthorizationCode", login.UserCode);
            OpenAuthUrl();
        }
        catch (Exception ex)
        {
            await CancelPendingLoginAsync(showNotice: false);
            if (!_closed) { NoticeLabel.Text = ex.Message; LoginButton.IsEnabled = true; }
        }
    }

    private async void CancelLoginClick(object? sender, RoutedEventArgs e) => await CancelPendingLoginAsync(showNotice: true);

    private async Task CancelPendingLoginAsync(bool showNotice)
    {
        var loginId = _loginId;
        _loginId = null;
        _authUrl = null;
        _loginPending = false;
        string? error = null;
        try
        {
            if (loginId is not null) await _host.RequestAsync<JsonElement>("account.login.cancel", new { loginId });
        }
        catch (Exception ex) { error = ex.Message; }
        if (_closed) return;
        LoginPanel.IsVisible = false;
        if (_account is { } account) ShowAccount(account);
        if (!showNotice) return;
        if (error is null) LocalizedControl.Text(NoticeLabel, "LoginCancelled");
        else NoticeLabel.Text = error;
    }

    private void OpenLoginClick(object? sender, RoutedEventArgs e)
    {
        try { OpenAuthUrl(); }
        catch (Exception ex) { NoticeLabel.Text = ex.Message; }
    }

    private void OpenAuthUrl()
    {
        if (!Uri.TryCreate(_authUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException(L.Text("InvalidAuthorizationUrl"));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void ManageUsageClick(object? sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true }); }
        catch (Exception ex) { NoticeLabel.Text = ex.Message; }
    }

    private async void RefreshClick(object? sender, RoutedEventArgs e) => await RefreshAccountAsync();
    private async void LogoutClick(object? sender, RoutedEventArgs e)
    {
        LogoutButton.IsEnabled = false;
        try { await _host.RequestAsync<JsonElement>("account.logout"); await RefreshAccountAsync(); }
        catch (Exception ex) { NoticeLabel.Text = ex.Message; LogoutButton.IsEnabled = true; }
    }

    private void ModelSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CodexModels.SelectedItem is ModelInfo model) CodexModelBox.Text = model.Id;
    }
    private void KeyVariableChanged(object? sender, TextChangedEventArgs e) => UpdateKeyStatus();
    private void UpdateKeyStatus()
    {
        if (KeyStatus is null) return;
        _ = RefreshApiCredentialStatusAsync();
    }
    private ProviderKind SelectedProvider => (ProviderPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
    {
        "DeepSeek" => ProviderKind.DeepSeek,
        "Anthropic" => ProviderKind.Anthropic,
        "OpenAiCompatible" => ProviderKind.OpenAiCompatible,
        "OpenAiResponses" => ProviderKind.OpenAiResponses,
        _ => ProviderKind.Auto
    };

    private ProviderKind EffectiveProvider => ComposerChoices.ResolveProvider(new(Endpoint: EndpointBox.Text ?? "", Provider: SelectedProvider));

    private void ProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _updatingProvider) return;
        ApiKeyBox.Text = "";
        if (EffectiveProvider != ProviderKind.Anthropic) LegacyThinkingToggle.IsChecked = false;
        ImageCapabilityToggle.IsChecked = false;
        UpdateProviderOptions();
        UpdateProviderDetection();
        UpdateKeyStatus();
        UiMotion.Reveal(ProviderOptionsPanel);
    }

    private void EndpointChanged(object? sender, TextChangedEventArgs e)
    {
        if (ApiKeyBox is not null) ApiKeyBox.Text = "";
        if (_initializing || _updatingProvider) return;
        ApiModelsPicker.ItemsSource = null;
        UpdateProviderDetection();
        UpdateKeyStatus();
        if (SelectedProvider != ProviderKind.Auto || !Uri.TryCreate(EndpointBox.Text, UriKind.Absolute, out _)) return;
        if (EffectiveProvider != ProviderKind.Anthropic) LegacyThinkingToggle.IsChecked = false;
        UpdateProviderOptions();
    }

    private void UpdateProviderOptions(string? preferredEffort = null)
    {
        var effort = preferredEffort ?? (ReasoningPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        var family = EffectiveProvider;
        string[] values = family switch
        {
            ProviderKind.DeepSeek => ["none", "low", "high", "max"],
            ProviderKind.Anthropic => ["none", "low", "medium", "high", "xhigh", "max"],
            _ => ["none", "minimal", "low", "medium", "high", "xhigh"]
        };
        List<ComboBoxItem> items = [];
        foreach (var value in values)
        {
            var item = new ComboBoxItem { Tag = value };
            var key = value == "none" && family is ProviderKind.OpenAiCompatible or ProviderKind.OpenAiResponses ? "ReasoningProviderDefault" : value switch
            {
                "none" => "ReasoningOff", "minimal" => "ReasoningMinimal", "low" => "ReasoningLow",
                "medium" => "ReasoningMedium", "high" => "ReasoningHigh", "xhigh" => "ReasoningExtraHigh", _ => "ReasoningMaximum"
            };
            LocalizedControl.Content(item, key);
            items.Add(item);
        }
        ReasoningPicker.ItemsSource = items;
        ReasoningPicker.SelectedItem = items.FirstOrDefault(item => item.Tag?.ToString() == effort) ?? items[0];
        LegacyThinkingPanel.IsVisible = family == ProviderKind.Anthropic;
        ImageCapabilityPanel.IsVisible = family is ProviderKind.OpenAiCompatible or ProviderKind.OpenAiResponses;
        MaxTokensBox.Maximum = family switch { ProviderKind.DeepSeek => 393216, ProviderKind.Anthropic => 1000000, _ => 131072 };
        LocalizedControl.Text(ReasoningHint, family switch
        {
            ProviderKind.DeepSeek => "DeepSeekReasoningHint",
            ProviderKind.Anthropic => "ClaudeReasoningHint",
            ProviderKind.OpenAiResponses => "ResponsesReasoningHint",
            _ => "CompatibleReasoningHint"
        });
        UpdateLegacyThinking();
    }

    private void LegacyThinkingChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing || _updatingProvider) return;
        UpdateLegacyThinking();
        if (LegacyBudgetPanel.IsVisible) UiMotion.Reveal(LegacyBudgetPanel);
    }

    private void UpdateLegacyThinking()
    {
        LegacyBudgetPanel.IsVisible = EffectiveProvider == ProviderKind.Anthropic && LegacyThinkingToggle.IsChecked == true;
        ReasoningPicker.IsEnabled = !LegacyBudgetPanel.IsVisible;
    }

    private void ProviderDefaultsClick(object? sender, RoutedEventArgs e)
    {
        ApiKeyBox.Text = "";
        var family = EffectiveProvider;
        _updatingProvider = true;
        try
        {
            ProviderPicker.SelectedIndex = family switch { ProviderKind.DeepSeek => 1, ProviderKind.Anthropic => 2, ProviderKind.OpenAiResponses => 4, _ => 3 };
            EndpointBox.Text = family switch { ProviderKind.DeepSeek => "https://api.deepseek.com", ProviderKind.Anthropic => "https://api.anthropic.com", _ => "https://api.openai.com/v1" };
            ApiModelBox.Text = family switch { ProviderKind.DeepSeek => "deepseek-flash", ProviderKind.Anthropic => "claude-sonnet-4-6", _ => "" };
            KeyVariableBox.Text = family switch { ProviderKind.DeepSeek => "DEEPSEEK_API_KEY", ProviderKind.Anthropic => "ANTHROPIC_API_KEY", _ => "OPENAI_API_KEY" };
            MaxTokensBox.Value = family == ProviderKind.Anthropic ? 8192 : 4096;
            ImageCapabilityToggle.IsChecked = false;
            LegacyThinkingToggle.IsChecked = false;
            ThinkingBudgetBox.Value = 2048;
        }
        finally { _updatingProvider = false; }
        UpdateProviderOptions(family == ProviderKind.Anthropic ? "high" : "none");
        UpdateKeyStatus();
        UiMotion.Reveal(ProviderOptionsPanel);
    }

    private void BackendSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        SettingsTabs.SelectedIndex = BackendChoice.SelectedIndex == 0 ? 1 : 2;
    }
    private async Task LoadProviderPresetsAsync()
    {
        try
        {
            _providerPresets = await _host.RequestAsync<ProviderPreset[]>("provider.presets");
            if (_closed) return;
            ApiPresetPicker.ItemsSource = _providerPresets;
            UpdateProviderDetection();
        }
        catch (Exception ex) { if (!_closed) NoticeLabel.Text = ex.Message; }
    }
    private void ApiPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ApiPresetPicker.SelectedItem is not ProviderPreset preset) return;
        _updatingProvider = true;
        try
        {
            ApiKeyBox.Text = "";
            ApiModelsPicker.ItemsSource = null;
            EndpointBox.Text = preset.Endpoint;
            ApiModelBox.Text = "";
            KeyVariableBox.Text = preset.ApiKeyEnvironmentVariable;
            ProviderPicker.SelectedIndex = preset.Provider switch
            { ProviderKind.DeepSeek => 1, ProviderKind.Anthropic => 2, ProviderKind.OpenAiResponses => 4, _ => 3 };
            ImageCapabilityToggle.IsChecked = false;
            LegacyThinkingToggle.IsChecked = false;
            BackendChoice.SelectedIndex = 1;
        }
        finally { _updatingProvider = false; }
        UpdateProviderOptions();
        UpdateProviderDetection();
        UpdateKeyStatus();
    }
    private void UpdateProviderDetection()
    {
        if (ProviderDetectionLabel is null) return;
        var detected = Uri.TryCreate(EndpointBox.Text, UriKind.Absolute, out var endpoint)
            ? _providerPresets.FirstOrDefault(p => new Uri(p.Endpoint).Host.Equals(endpoint.Host, StringComparison.OrdinalIgnoreCase)) : null;
        if (detected is null) LocalizedControl.Text(ProviderDetectionLabel, "ProviderDetectionManual");
        else LocalizedControl.Text(ProviderDetectionLabel, "ProviderDetectionKnown", detected.Name);
    }
    private string? CurrentCredentialProfile()
    {
        if (_credentialProfileId is null || !Uri.TryCreate(EndpointBox.Text, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http")) return null;
        try
        {
            return ProviderCredentialBinding.Endpoint(EndpointBox.Text ?? "") == ProviderCredentialBinding.Endpoint(_credentialEndpoint) &&
                EffectiveProvider == _credentialFamily ? _credentialProfileId : null;
        }
        catch (ArgumentException) { return null; }
    }
    private ProviderSettings CredentialProvider() => _settings.Provider with
    {
        Endpoint = EndpointBox.Text?.Trim().TrimEnd('/') ?? "", Model = ApiModelBox.Text?.Trim() ?? "",
        Provider = SelectedProvider, ApiKeyEnvironmentVariable = KeyVariableBox.Text?.Trim() ?? "",
        CredentialProfileId = CurrentCredentialProfile()
    };
    private async Task RefreshApiCredentialStatusAsync()
    {
        var generation = ++_keyStatusGeneration;
        if (_initializing || _closed) return;
        RemoveApiKeyButton.IsEnabled = CurrentCredentialProfile() is not null;
        var provider = CredentialProvider();
        try
        {
            _ = ProviderCredentialBinding.Endpoint(provider.Endpoint);
            var status = await _host.RequestAsync<ProviderCredentialStatus>("provider.credential.status", new { provider });
            if (_closed || generation != _keyStatusGeneration) return;
            LocalizedControl.Text(KeyStatus, status.Configured ? provider.CredentialProfileId is not null ? "ApiKeyStored" : "KeyDetected" : "ApiKeyNotConfigured");
        }
        catch (Exception) { if (!_closed && generation == _keyStatusGeneration) LocalizedControl.Text(KeyStatus, "ApiKeyNotConfigured"); }
    }
    private async Task SaveApiCredentialAsync(ProviderSettings provider)
    {
        var rawKey = ApiKeyBox.Text ?? "";
        try
        {
            var saved = await _host.RequestAsync<ProviderCredentialStatus>("provider.credential.save", new { provider, apiKey = rawKey });
            if (_closed) return;
            _credentialProfileId = saved.CredentialProfileId;
            _credentialEndpoint = ProviderCredentialBinding.Endpoint(provider.Endpoint);
            _credentialFamily = ProviderCredentialBinding.Family(provider);
            LocalizedControl.Text(KeyStatus, "ApiKeyStored");
            RemoveApiKeyButton.IsEnabled = true;
        }
        finally { ApiKeyBox.Text = ""; }
    }
    private async void SaveApiKeyClick(object? sender, RoutedEventArgs e)
    {
        SaveApiKeyButton.IsEnabled = false;
        try { await SaveApiCredentialAsync(CredentialProvider()); }
        catch (Exception ex) { if (!_closed) NoticeLabel.Text = ex.Message; }
        finally { if (!_closed) SaveApiKeyButton.IsEnabled = true; }
    }
    private async void RemoveApiKeyClick(object? sender, RoutedEventArgs e)
    {
        RemoveApiKeyButton.IsEnabled = false;
        try
        {
            await _host.RequestAsync<ProviderCredentialStatus>("provider.credential.delete", new { provider = CredentialProvider() });
            _credentialProfileId = null;
            ApiKeyBox.Text = "";
            UpdateKeyStatus();
        }
        catch (Exception ex) { if (!_closed) NoticeLabel.Text = ex.Message; }
    }
    private async void FetchApiModelsClick(object? sender, RoutedEventArgs e)
    {
        FetchApiModelsButton.IsEnabled = false;
        var provider = CredentialProvider();
        try
        {
            var catalog = await _host.RequestAsync<ProviderModelCatalog>("provider.models", new { provider, apiKey = ApiKeyBox.Text });
            if (_closed || provider.Endpoint != CredentialProvider().Endpoint) return;
            ApiModelsPicker.ItemsSource = catalog.Models;
            LocalizedControl.Text(NoticeLabel, catalog.Truncated ? "ApiModelsTruncated" : "ApiModelsLoaded", catalog.Models.Count);
        }
        catch (Exception ex) { if (!_closed) NoticeLabel.Text = ex.Message; }
        finally { if (!_closed) FetchApiModelsButton.IsEnabled = true; }
    }
    private void ApiModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ApiModelsPicker.SelectedItem is not ModelInfo model) return;
        ApiModelBox.Text = model.Id;
        ImageCapabilityToggle.IsChecked = model.SupportsImages == true;
    }
    private async void SaveClick(object? sender, RoutedEventArgs e)
    {
        var backend = (BackendChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (backend is not ("api" or "chatgpt")) { LocalizedControl.Text(NoticeLabel, "ChooseConnection"); return; }
        if (backend == "chatgpt")
        {
            if (_account?.LoggedIn != true) { LocalizedControl.Text(NoticeLabel, "AccountSignInRequired"); SettingsTabs.SelectedIndex = 1; return; }
            Close(_settings with { Backend = "chatgpt", OnboardingCompleted = true, CodexModel = CodexModelBox.Text?.Trim() ?? "",
                Language = L.Language, ReducedMotion = UiMotion.ReducedMotion, Research = ResearchSettings() });
            return;
        }
        var endpoint = EndpointBox.Text?.Trim().TrimEnd('/') ?? "";
        var model = ApiModelBox.Text?.Trim() ?? "";
        var variable = KeyVariableBox.Text?.Trim() ?? "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            LocalizedControl.Text(NoticeLabel, "InvalidEndpoint");
            return;
        }
        if (string.IsNullOrWhiteSpace(model)) { LocalizedControl.Text(NoticeLabel, "ModelRequired"); return; }
        if (variable.Length > 0 && !Regex.IsMatch(variable, "^[A-Za-z_][A-Za-z0-9_]*$")) { LocalizedControl.Text(NoticeLabel, "VariableRequired"); return; }
        var maximum = (int)(MaxTokensBox.Value ?? 4096);
        int? budget = EffectiveProvider == ProviderKind.Anthropic && LegacyThinkingToggle.IsChecked == true
            ? (int)(ThinkingBudgetBox.Value ?? 2048) : null;
        if (budget is { } manualBudget && (manualBudget < 1024 || manualBudget >= maximum))
        { LocalizedControl.Text(NoticeLabel, "InvalidThinkingBudget"); return; }
        var provider = _settings.Provider with
        {
            Endpoint = endpoint, Model = model, ApiKeyEnvironmentVariable = variable, MaxOutputTokens = maximum,
            Provider = SelectedProvider, ReasoningEffort = (ReasoningPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none",
            ThinkingBudgetTokens = budget, SupportsImages = ImageCapabilityToggle.IsChecked == true,
            CredentialProfileId = CurrentCredentialProfile()
        };
        SaveButton.IsEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Text))
            {
                await SaveApiCredentialAsync(provider);
                provider = provider with { CredentialProfileId = CurrentCredentialProfile() };
            }
            if (_requireInitialCredential)
            {
                var status = await _host.RequestAsync<ProviderCredentialStatus>("provider.credential.status", new { provider });
                if (!status.Configured) { LocalizedControl.Text(NoticeLabel, "ApiCredentialRequired"); return; }
            }
        var accountModel = CodexModelBox.Text?.Trim() ?? "";
        Close(_settings with
        {
            Backend = "api", OnboardingCompleted = true,
            Provider = provider, CodexModel = accountModel,
            CodexReasoningEffort = _accountChanged || accountModel != _settings.CodexModel ? "none" :
                ComposerChoices.AccountEffort(CodexModels.Items.OfType<ModelInfo>().FirstOrDefault(model => model.Id == accountModel), _settings.CodexReasoningEffort),
            Language = L.Language, ReducedMotion = UiMotion.ReducedMotion, Research = ResearchSettings()
        });
        }
        catch (Exception ex) { NoticeLabel.Text = ex.Message; }
        finally { if (!_closed) SaveButton.IsEnabled = true; }
    }
    private void CancelClick(object? sender, RoutedEventArgs e) => Close((DesktopSettings?)null);

    private void LanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        L.SetLanguage(LanguagePicker.SelectedIndex switch { 1 => "zh-CN", 2 => "en-US", _ => null });
        SavePreferences();
    }
    private void ReducedMotionChanged(object? sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        UiMotion.ReducedMotion = ReducedMotionToggle.IsChecked == true;
        SavePreferences();
    }
    private ResearchOptions ResearchSettings() => _settings.Research with
    {
        CompressionGuidance = CompressionGuidanceToggle.IsChecked == true,
        RepositoryMemory = RepositoryMemoryToggle.IsChecked == true,
        ReadOnlyPrefetch = ReadOnlyPrefetchToggle.IsChecked == true
    };
    private void ResearchChanged(object? sender, RoutedEventArgs e) { if (!_initializing) SavePreferences(); }
    private void SavePreferences() => _preferencesChanged(_settings with { Language = L.Language, ReducedMotion = UiMotion.ReducedMotion, Research = ResearchSettings() });
    private void RefreshLocalization()
    {
        Title = ProductIdentity.WindowTitle;
        if (_account is { } account) ShowAccount(account);
        ShowAccounts(_profiles);
        UpdateProviderDetection();
        UpdateKeyStatus();
        LocalizedControl.RemeasureTabHeaders(SettingsTabs);
    }
    private void RefreshMotion(object? sender, EventArgs e)
    {
        foreach (var control in this.GetVisualDescendants().OfType<TemplatedControl>().Where(control => control is Button or TextBox or ComboBox))
            control.Transitions = UiMotion.CreateFeedbackTransitions();
    }
}
