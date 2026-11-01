using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;
using DesktopLocalization = Workbench.Desktop.Localization;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class ProviderSetupUiTests
{
    [Fact]
    public async Task FreshConnectionRequiresAnExplicitModeAndUsesMaskedTransientKeyEntry()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            var host = new HostClient();
            var window = new ConnectionsWindow(host, new DesktopSettings(), _ => { });
            var owner = new Window();
            owner.Show();
            var result = window.ShowDialog<DesktopSettings?>(owner);
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(-1, window.FindControl<ComboBox>("BackendChoice")!.SelectedIndex);
                Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("EndpointBox")!.Text));
                Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("ApiModelBox")!.Text));
                var key = window.FindControl<TextBox>("ApiKeyBox")!;
                Assert.Equal('●', key.PasswordChar);
                Assert.True(string.IsNullOrEmpty(key.Text));
                window.FindControl<Button>("SaveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(result.IsCompleted);
                Assert.Contains("Choose ChatGPT", window.FindControl<TextBlock>("NoticeLabel")!.Text);
                key.Text = "temporary-fixture-key";
                window.FindControl<TextBox>("EndpointBox")!.Text = "https://selected.invalid/v1";
                Assert.True(string.IsNullOrEmpty(key.Text));
                key.Text = "temporary-fixture-key";
                window.Close();
                Assert.True(string.IsNullOrEmpty(key.Text));
            }
            finally
            {
                window.Close(); owner.Close();
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ApiPresetsConfigureAddressAndProtocolWithoutChoosingAModelOrRetainingAKey()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            DesktopLocalization.SetLanguage("en-US");
            var host = new HostClient();
            var window = new ConnectionsWindow(host, new DesktopSettings(), _ => { });
            var owner = new Window(); owner.Show();
            _ = window.ShowDialog<DesktopSettings?>(owner);
            try
            {
                var presets = window.FindControl<ComboBox>("ApiPresetPicker")!;
                window.FindControl<TextBox>("ApiKeyBox")!.Text = "temporary-fixture-key";
                presets.ItemsSource = new[] { new ProviderPreset("fixture", "Fixture", "https://fixture.invalid/v1", ProviderKind.OpenAiResponses, "FIXTURE_KEY") };
                presets.SelectedIndex = 0;
                Assert.Equal("https://fixture.invalid/v1", window.FindControl<TextBox>("EndpointBox")!.Text);
                Assert.Equal(4, window.FindControl<ComboBox>("ProviderPicker")!.SelectedIndex);
                Assert.Equal(1, window.FindControl<ComboBox>("BackendChoice")!.SelectedIndex);
                Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("ApiModelBox")!.Text));
                Assert.True(string.IsNullOrEmpty(window.FindControl<TextBox>("ApiKeyBox")!.Text));
                Assert.Equal("Fetch models", window.FindControl<Button>("FetchApiModelsButton")!.Content);
                DesktopLocalization.SetLanguage("zh-CN");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("获取模型", window.FindControl<Button>("FetchApiModelsButton")!.Content);
                Assert.Equal("安全保存密钥", window.FindControl<Button>("SaveApiKeyButton")!.Content);
                DesktopLocalization.SetLanguage("en-US");
            }
            finally
            {
                window.Close(); owner.Close();
                host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }, CancellationToken.None);
    }
}
