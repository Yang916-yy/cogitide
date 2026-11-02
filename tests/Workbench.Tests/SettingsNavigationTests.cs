using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

[Collection("Desktop localization")]
public sealed class SettingsNavigationTests
{
    [Fact]
    public async Task VerticalCategoriesPreserveProviderInputsAndLivePreferences()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            using var dialog = Open("C:\\fixture-project");
            var navigation = dialog.Window.FindControl<TabControl>("SettingsNavigation")!;
            Assert.Equal(Dock.Left, navigation.TabStripPlacement);
            Assert.Equal(5, navigation.Items.Count);
            Assert.Equal(0, navigation.SelectedIndex);
            var model = dialog.Window.FindControl<TextBox>("ApiModelBox")!;
            model.Text = "preserve-this-model";
            SaveFrame(dialog.Window, "settings-general-en.png");

            navigation.SelectedIndex = 1;
            dialog.Window.FindControl<TabControl>("SettingsTabs")!.SelectedIndex = 1;
            Localization.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, navigation.SelectedIndex);
            Assert.Equal("模型和账号", ((TextBlock)((TabItem)navigation.Items[1]!).Header!).Text);
            Assert.Contains(dialog.Window.FindControl<ComboBox>("BackendChoice")!.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == Localization.Text("UseApiKey"));
            Assert.Equal("preserve-this-model", model.Text);
            Assert.Equal('●', dialog.Window.FindControl<TextBox>("ApiKeyBox")!.PasswordChar);
            dialog.Window.FindControl<TextBlock>("NoticeLabel")!.Text = "";
            SaveFrame(dialog.Window, "settings-models-zh.png");

            navigation.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            dialog.Window.FindControl<ListBox>("InstalledSkillsList")!.ItemsSource = new[]
            {
                new ConnectionsWindow.SkillListItem("project-review", "Review project changes and validation evidence.", ".agents/skills/project-review/SKILL.md")
            };
            dialog.Window.FindControl<ListBox>("InstalledSkillsList")!.IsVisible = true;
            dialog.Window.FindControl<ListBox>("SkillCatalogList")!.ItemsSource = new[]
            {
                new ConnectionsWindow.SkillCatalogItem("anthropic:pptx", "pptx", "Create and edit presentations with an existing presentation library.", "anthropics/skills · pinned snapshot")
            };
            dialog.Window.FindControl<ListBox>("SkillCatalogList")!.IsVisible = true;
            dialog.Window.FindControl<TextBlock>("SkillsNoticeLabel")!.Text = "";
            LocalizedControl.Text(dialog.Window.FindControl<TextBlock>("SkillsInstalledSummary")!, "SkillsInstalledCount", 1);
            Assert.Equal("技能目录", AutomationProperties.GetName(dialog.Window.FindControl<ListBox>("SkillCatalogList")!));
            Assert.False(dialog.Window.FindControl<Button>("InstallSkillButton")!.IsEnabled);
            dialog.Window.FindControl<ListBox>("SkillCatalogList")!.SelectedIndex = 0;
            Assert.True(dialog.Window.FindControl<Button>("InstallSkillButton")!.IsEnabled);
            SaveFrame(dialog.Window, "settings-skills-zh.png");
            var scroll = dialog.Window.FindControl<ScrollViewer>("SkillsScrollViewer")!;
            scroll.Offset = new Avalonia.Vector(0, scroll.Extent.Height);
            SaveFrame(dialog.Window, "settings-skills-catalog-zh.png");

            Localization.SetLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, navigation.SelectedIndex);
            Assert.Equal("Install selected skill", dialog.Window.FindControl<Button>("InstallSkillButton")!.Content);
            Assert.Equal("preserve-this-model", model.Text);
            dialog.Window.Width = dialog.Window.MinWidth;
            SaveFrame(dialog.Window, "settings-skills-compact-en.png");
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SkillsRequireAProjectAndExistingConnectionActionsReturnAnExplicitRoute()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            using var dialog = Open(null);
            Assert.False(dialog.Window.FindControl<StackPanel>("SkillsProjectPanel")!.IsEnabled);
            Assert.True(dialog.Window.FindControl<TextBlock>("SkillsProjectRequiredLabel")!.IsVisible);
            Assert.False(dialog.Window.FindControl<Button>("OpenRunBudgetButton")!.IsEnabled);
            dialog.Window.FindControl<TabControl>("SettingsNavigation")!.SelectedIndex = 3;
            dialog.Window.FindControl<Button>("OpenHostConnectionsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("connections", dialog.Window.RequestedSection);
            Assert.True(dialog.Result.IsCompletedSuccessfully);
            Assert.Null(dialog.Result.Result);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InitialProviderSetupOpensModelsAndKeepsConnectionChoiceExplicit()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            var host = new HostClient();
            var window = new ConnectionsWindow(host, new DesktopSettings(), _ => { }, canConfigureExecution: false);
            try
            {
                Assert.Equal(1, window.FindControl<TabControl>("SettingsNavigation")!.SelectedIndex);
                Assert.Equal(-1, window.FindControl<ComboBox>("BackendChoice")!.SelectedIndex);
                Assert.False(window.FindControl<Button>("OpenHostConnectionsButton")!.IsEnabled);
            }
            finally { window.Close(); host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }, CancellationToken.None);
    }

    private static Dialog Open(string? workspacePath)
    {
        var host = new HostClient();
        var window = new ConnectionsWindow(host, new DesktopSettings
        {
            Backend = "api", Language = "en-US", OnboardingCompleted = true,
            Provider = new("https://fixture.invalid/v1", "fixture-model", "FIXTURE_API_KEY", Provider: ProviderKind.OpenAiCompatible)
        }, _ => { }, workspacePath);
        var owner = new Window { Width = 1100, Height = 850 };
        owner.Show();
        var result = window.ShowDialog<DesktopSettings?>(owner);
        Dispatcher.UIThread.RunJobs();
        window.FindControl<TextBlock>("NoticeLabel")!.Text = "";
        return new(window, owner, host, result);
    }

    private static void SaveFrame(Window window, string filename)
    {
        var directory = Environment.GetEnvironmentVariable("COGITIDE_SETTINGS_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Assert.True(Path.IsPathFullyQualified(directory));
        Directory.CreateDirectory(directory);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, filename), PngBitmapEncoderOptions.Default);
    }

    private sealed record Dialog(ConnectionsWindow Window, Window Owner, HostClient Host, Task<DesktopSettings?> Result) : IDisposable
    {
        public void Dispose()
        {
            Window.Close(); Owner.Close();
            Host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Localization.SetLanguage("en-US");
        }
    }
}
