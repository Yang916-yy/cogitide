using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;
using DesktopLocalization = Workbench.Desktop.Localization;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class ConnectionsProviderTests
{
    [Fact]
    public async Task ResearchOptionsRoundTripThroughTheRealSettingsDialog()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            using var dialog = OpenDialog(new());
            foreach (var name in new[] { "CompressionGuidanceToggle", "RepositoryMemoryToggle", "ReadOnlyPrefetchToggle" })
            {
                var toggle = dialog.Window.FindControl<CheckBox>(name)!;
                Assert.False(toggle.IsChecked);
                toggle.IsChecked = true;
            }
            DesktopLocalization.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Save(dialog.Window);
            Assert.True(dialog.Result.IsCompletedSuccessfully);
            var research = dialog.Result.Result!.Research;
            Assert.True(research.CompressionGuidance && research.RepositoryMemory && research.ReadOnlyPrefetch);
            Assert.Equal(4, research.MaxPrefetches);
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<DesktopSettings>(
                System.Text.Json.JsonSerializer.Serialize(dialog.Result.Result, Wire.Json), Wire.Json)!;
            Assert.Equal(research, roundTrip.Research);
            Assert.Equal(new ResearchOptions(), (new DesktopSettings { Research = null! }).NormalizeBackend().Research);
            DesktopLocalization.SetLanguage("en-US");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExistingAndAdvancedProviderSettingsRoundTripThroughRealDialog()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            ProviderSettings[] settings =
            [
                new(),
                new(ReasoningEffort: "max", MaxOutputTokens: 16384),
                new("https://api.anthropic.com", "claude-sonnet-4-6", "ANTHROPIC_API_KEY", 8192,
                    ProviderKind.Anthropic, "high", ThinkingBudgetTokens: 2048),
                new("http://localhost:9000/v1", "custom-vision", "LOCAL_MODEL_KEY", 128,
                    ProviderKind.OpenAiCompatible, "medium", SupportsImages: true),
                new("https://api.openai.com/v1", "configured-responses-model", "OPENAI_API_KEY", 4096,
                    ProviderKind.OpenAiResponses, "high", SupportsImages: true)
            ];
            foreach (var expected in settings)
            {
                using var dialog = OpenDialog(expected);
                Save(dialog.Window);
                Assert.True(dialog.Result.IsCompletedSuccessfully);
                Assert.Equal(expected, dialog.Result.GetAwaiter().GetResult()!.Provider);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SwitchingProvidersResetsIncompatibleEffortBudgetAndImageFlag()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            using var dialog = OpenDialog(new("https://api.anthropic.com", "claude-sonnet-4-6", "ANTHROPIC_API_KEY", 8192,
                ProviderKind.Anthropic, "medium", ThinkingBudgetTokens: 2048));
            var provider = dialog.Window.FindControl<ComboBox>("ProviderPicker")!;
            var reasoning = dialog.Window.FindControl<ComboBox>("ReasoningPicker")!;
            var legacy = dialog.Window.FindControl<CheckBox>("LegacyThinkingToggle")!;
            Assert.False(reasoning.IsEnabled);
            provider.SelectedIndex = 1;
            Assert.Equal("none", (reasoning.SelectedItem as ComboBoxItem)?.Tag);
            Assert.False(legacy.IsChecked);
            Assert.True(reasoning.IsEnabled);
            Assert.False(dialog.Window.FindControl<StackPanel>("LegacyThinkingPanel")!.IsVisible);
            provider.SelectedIndex = 3;
            var images = dialog.Window.FindControl<CheckBox>("ImageCapabilityToggle")!;
            images.IsChecked = true;
            reasoning.SelectedItem = reasoning.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "xhigh");
            provider.SelectedIndex = 1;
            Assert.Equal("none", (reasoning.SelectedItem as ComboBoxItem)?.Tag);
            Assert.False(images.IsChecked);
            Assert.False(dialog.Window.FindControl<StackPanel>("ImageCapabilityPanel")!.IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OpenDialogLocalizesProviderChoicesWithoutChangingSelection()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            using var dialog = OpenDialog(new(Provider: ProviderKind.DeepSeek, ReasoningEffort: "max"));
            dialog.Window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var combo = dialog.Window.FindControl<ComboBox>("ReasoningPicker")!;
            var selected = combo.SelectedItem;
            Assert.Equal("Maximum", (selected as ComboBoxItem)?.Content);
            DesktopLocalization.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.Same(selected, combo.SelectedItem);
            Assert.Equal("最高", (selected as ComboBoxItem)?.Content);
            Assert.Contains(combo.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "最高");
            Assert.Equal(2, dialog.Window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex);
            DesktopLocalization.SetLanguage("en-US");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InvalidManualBudgetKeepsDialogOpenWithUsefulValidation()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            using var dialog = OpenDialog(new("https://api.anthropic.com", "claude-sonnet-4-6", "ANTHROPIC_API_KEY", 4096,
                ProviderKind.Anthropic, "high", ThinkingBudgetTokens: 4096));
            Save(dialog.Window);
            Assert.False(dialog.Result.IsCompleted);
            Assert.Contains("below", dialog.Window.FindControl<TextBlock>("NoticeLabel")!.Text);
            dialog.Window.FindControl<NumericUpDown>("ThinkingBudgetBox")!.Value = 2048;
            Save(dialog.Window);
            Assert.True(dialog.Result.IsCompletedSuccessfully);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChatGptSettingsExplainAuthenticationOnlyAndExposeLoginCancellation()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            using var dialog = OpenDialog(new());
            dialog.Window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var text = dialog.Window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text ?? "").ToArray();
            Assert.Contains(text, value => value.Contains("Cogitide runs the task, tools, summaries and project memory", StringComparison.Ordinal));
            Assert.DoesNotContain(text, value => value.Contains("Codex app-server", StringComparison.Ordinal));
            Assert.Equal("Cancel sign-in", dialog.Window.FindControl<Button>("CancelLoginButton")!.Content);
            Assert.Equal("Continue with ChatGPT", dialog.Window.FindControl<Button>("LoginButton")!.Content);
            Assert.Equal("Add account", dialog.Window.FindControl<Button>("AddAccountButton")!.Content);
            Assert.Equal("Manage ChatGPT usage ↗", dialog.Window.FindControl<Button>("ManageUsageButton")!.Content);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SavedAccountsRemainSeparateAndSelectedDuringLanguageChanges()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            using var dialog = OpenDialog(new());
            var connection = (ConnectionsWindow)dialog.Window;
            connection.ShowAccounts([
                new("binding-work", "same@example.com", "Work", IsSelected: true, LoggedIn: true),
                new("binding-personal", "same@example.com", "Personal")
            ]);
            var picker = connection.FindControl<ComboBox>("AccountPicker")!;
            Assert.True(picker.IsVisible);
            Assert.Equal(2, picker.Items.Count);
            Assert.Equal("binding-work", ((AccountProfile)((ComboBoxItem)picker.SelectedItem!).Tag!).AccountBinding);
            Assert.Contains("Sign-in required", ((ComboBoxItem)picker.Items[1]!).Content?.ToString());
            DesktopLocalization.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("binding-work", ((AccountProfile)((ComboBoxItem)picker.SelectedItem!).Tag!).AccountBinding);
            Assert.Contains("需要登录", ((ComboBoxItem)picker.Items[1]!).Content?.ToString());
            DesktopLocalization.SetLanguage("en-US");
        }, CancellationToken.None);
    }

    private static void Save(Window window) =>
        window.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    // Exercise the real compiled XAML and settings rather than copies of production source.
    private static Dialog OpenDialog(ProviderSettings provider)
    {
        var settings = new DesktopSettings { Provider = provider, Language = "en-US" };
        var host = new HostClient();
        var window = new ConnectionsWindow(host, settings, _ => { });
        var owner = new Window { Width = 800, Height = 800 };
        owner.Show();
        var result = window.ShowDialog<DesktopSettings?>(owner);
        Dispatcher.UIThread.RunJobs();
        return new(window, owner, host, result);
    }

    private sealed record Dialog(Window Window, Window Owner, IAsyncDisposable Host, Task<DesktopSettings?> Result) : IDisposable
    {
        public void Dispose()
        {
            Window.Close();
            Owner.Close();
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
