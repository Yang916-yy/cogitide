using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ComposerHeadlessTestApp : Application
{
    // Render the production App styles and production MainWindow without any desktop window or Host process.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[Collection("Desktop localization")]
public sealed class ComposerTests
{
    [Fact]
    public async Task OneActionSwitchesBetweenSendAndStopWithoutLosingTheDraft()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = Open();
            try
            {
                var prompt = window.FindControl<TextBox>("PromptBox")!;
                prompt.Text = "Keep this follow-up draft";
                Assert.Null(window.FindControl<Button>("StopButton"));
                Assert.Equal("send", window.FindControl<UiGlyph>("SendGlyph")!.Symbol);
                window.SetRunState(true);
                Assert.Equal("stop", window.FindControl<UiGlyph>("SendGlyph")!.Symbol);
                Assert.True(window.FindControl<Button>("SendButton")!.IsEnabled);
                Assert.False(window.FindControl<Button>("ModelButton")!.IsEnabled);
                Assert.False(window.FindControl<Button>("AttachButton")!.IsEnabled);
                Assert.Contains("add instructions", window.FindControl<TextBlock>("ComposerHint")!.Text);
                Assert.Equal("Keep this follow-up draft", prompt.Text);
                window.SetRunState(false);
                Assert.Equal("send", window.FindControl<UiGlyph>("SendGlyph")!.Symbol);
                Assert.True(window.FindControl<Button>("ModelButton")!.IsEnabled);
                Assert.Equal("Keep this follow-up draft", prompt.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RealComposerMenusSelectConfiguredModelsModesAndProviderSpecificEffort()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var first = new ProviderSettings(Model: "configured-first", Provider: ProviderKind.DeepSeek);
            var second = new ProviderSettings("https://custom.example/v1", "configured-second", "CUSTOM_KEY",
                Provider: ProviderKind.OpenAiCompatible, ReasoningEffort: "medium");
            var window = Open(new DesktopSettings { Provider = first, RecentProviders = [second], Language = "en-US" });
            try
            {
                var modelButton = window.FindControl<Button>("ModelButton")!;
                Click(modelButton);
                var models = modelButton.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Tag is ProviderSettings).ToArray();
                Assert.Equal(2, models.Length);
                Assert.Equal(["configured-first", "configured-second"], models.Select(item => ((ProviderSettings)item.Tag!).Model));
                models[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                modelButton.ContextMenu.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("configured-second", window.FindControl<TextBlock>("ModelLabel")!.Text);
                Assert.Equal("Medium", window.FindControl<TextBlock>("ReasoningLabel")!.Text);

                var modeButton = window.FindControl<Button>("ExecutionModeButton")!;
                Click(modeButton);
                Assert.Contains("No operating-system sandbox", ToolTip.GetTip(modeButton)?.ToString());
                var full = modeButton.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Tag is ExecutionMode.FullAccess);
                full.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                modeButton.ContextMenu.Close();
                Assert.Equal("Full access", window.FindControl<TextBlock>("ExecutionModeLabel")!.Text);

                var reasoningButton = window.FindControl<Button>("ReasoningButton")!;
                Click(reasoningButton);
                var levels = reasoningButton.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
                Assert.Contains(levels, item => (string)item.Tag! == "xhigh");
                Assert.DoesNotContain(levels, item => (string)item.Tag! == "max");
                levels.Single(item => (string)item.Tag! == "high").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                reasoningButton.ContextMenu.Close();
                Assert.Equal("High", window.FindControl<TextBlock>("ReasoningLabel")!.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void AccountEffortsRequireAdvertisedCapabilitiesAndResetUnsupportedSavedChoices()
    {
        Assert.Equal(["none"], ComposerChoices.AccountEfforts(null));
        Assert.Equal("none", ComposerChoices.AccountEffort(null, "high"));
        Assert.Equal(["none", "low", "high", "xhigh"], ComposerChoices.AccountEfforts(new("actual", "Actual", ["low", "high", "xhigh"], "high")));
        Assert.Equal("none", ComposerChoices.AccountEffort(new("actual", "Actual", ["low"], "low"), "high"));
        Assert.Equal("high", ComposerChoices.AccountEffort(new("actual", "Actual", ["low", "high"], "high"), "high"));
    }

    [Fact]
    public async Task AccountCatalogResetsStaleEffortWhileApiPreferencesArePreserved()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = Open(new DesktopSettings { Backend = "chatgpt", CodexModel = "saved-model", CodexReasoningEffort = "high",
                Provider = new(Provider: ProviderKind.DeepSeek, ReasoningEffort: "max") });
            try
            {
                Assert.Equal("High", window.FindControl<TextBlock>("ReasoningLabel")!.Text);
                window.ApplyAccountModels([new("saved-model", "Saved model")]);
                Assert.Equal("Provider default", window.FindControl<TextBlock>("ReasoningLabel")!.Text);
                var reasoning = window.FindControl<Button>("ReasoningButton")!;
                Click(reasoning);
                Assert.Equal(["none"], reasoning.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Tag is string).Select(item => (string)item.Tag!));
                reasoning.ContextMenu.Close();
                var model = window.FindControl<Button>("ModelButton")!;
                Click(model);
                model.ContextMenu!.Items.OfType<MenuItem>().First(item => item.Tag is ProviderSettings)
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                model.ContextMenu.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("Maximum", window.FindControl<TextBlock>("ReasoningLabel")!.Text);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public void SavedApiConfigurationsAreDeduplicatedBoundedAndKeepTheirCapabilities()
    {
        var settings = new DesktopSettings();
        for (var i = 0; i < 12; i++) settings = settings.RememberProvider(new(Model: "model-" + i, ReasoningEffort: "high", SupportsImages: true));
        Assert.Equal(8, settings.RecentProviders.Count);
        Assert.Equal("model-11", settings.RecentProviders[0].Model);
        settings = settings.RememberProvider(settings.Provider with { ReasoningEffort = "max" });
        Assert.Equal(8, settings.RecentProviders.Count);
        Assert.Equal("max", settings.RecentProviders[0].ReasoningEffort);
        Assert.True(settings.RecentProviders[0].SupportsImages);
        Assert.False(ComposerChoices.RequiresNewSession(settings, settings with { Provider = settings.Provider with { ReasoningEffort = "low" } }));
        Assert.True(ComposerChoices.RequiresNewSession(settings, settings with { Provider = settings.Provider with { Model = "different-model" } }));
        Assert.True(ComposerChoices.RequiresNewSession(settings, settings with { Provider = settings.Provider with { Endpoint = "https://custom.example/v1" } }));
        Assert.True(ComposerChoices.RequiresNewSession(settings, settings with { Backend = "codex" }));
        Assert.False(ComposerChoices.RequiresNewSession(settings with { Backend = "codex" }, settings with { Backend = "codex", CodexReasoningEffort = "high" }));
        Assert.True(ComposerChoices.RequiresNewSession(settings with { Backend = "chatgpt" }, settings with { Backend = "chatgpt", CodexModel = "other-model" }));
        Assert.True(ComposerChoices.RequiresNewSession(settings, settings with { Provider = settings.Provider with { Provider = ProviderKind.OpenAiResponses } }));
        var responses = settings.Provider with { Provider = ProviderKind.OpenAiResponses };
        settings = settings.RememberProvider(responses);
        Assert.Contains(settings.RecentProviders, item => item.Model == responses.Model && item.Provider != ProviderKind.OpenAiResponses);
        Assert.Contains(settings.RecentProviders, item => item.Model == responses.Model && item.Provider == ProviderKind.OpenAiResponses);
    }

    [Fact]
    public async Task LegacyChatsStayReviewableAndChatGptUsesTheSameExecutionControls()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var settings = new DesktopSettings { Backend = "codex", CodexModel = "saved-account-model" };
            Assert.Equal("chatgpt", settings.NormalizeBackend().Backend);
            var window = Open(settings);
            try
            {
                var info = new SessionInfo("legacy", "Retained history", Path.GetTempPath(), DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, Backend: "codex", ExternalThreadId: "official-thread");
                window.RestoreSession(new(info, [new("user", "Preserve the original task")]), []);
                var entries = window.FindControl<ItemsControl>("Timeline")!.Items.OfType<TimelineEntry>().ToArray();
                Assert.Contains(entries, entry => entry.Text == "Preserve the original task");
                Assert.Contains(entries, entry => entry.Text.Contains("earlier Codex integration", StringComparison.Ordinal));
                Assert.True(window.NeedsFreshSession);
                Assert.Equal("saved-account-model", window.FindControl<TextBlock>("ModelLabel")!.Text);
                var mode = window.FindControl<Button>("ExecutionModeButton")!;
                Click(mode);
                Assert.Equal("Execution mode", mode.ContextMenu!.Items.OfType<MenuItem>().First().Header);
                Assert.Contains("every model", ToolTip.GetTip(mode)?.ToString());
                Assert.Contains("No operating-system sandbox", ToolTip.GetTip(mode)?.ToString());
                mode.ContextMenu.Close();
                SaveFrame(window, "chatgpt-unified.png");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AccountIdentityChangesStartFreshButTokenRefreshDoesNot()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = Open(new DesktopSettings { Backend = "chatgpt", CodexModel = "account-model" });
            try
            {
                var info = new SessionInfo("owned", "Account history", Path.GetTempPath(), DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch, Backend: "chatgpt", AccountBinding: "binding-a");
                window.RestoreSession(new(info, [new("user", "Original requirement")]), []);
                window.ApplyAccountState(new(true, true, AccountBinding: "binding-a"));
                Assert.False(window.NeedsFreshSession);
                window.ApplyAccountState(new(true, true, Plan: "updated-plan", AccountBinding: "binding-a"));
                Assert.False(window.NeedsFreshSession);
                window.ApplyAccountState(new(true, true, AccountBinding: "binding-b"));
                Assert.True(window.NeedsFreshSession);
                Assert.Equal("ChatGPT", window.FindControl<TextBlock>("ModelLabel")!.Text);
                Assert.Contains(window.FindControl<ItemsControl>("Timeline")!.Items.OfType<TimelineEntry>(),
                    entry => entry.Text.Contains("account changed", StringComparison.Ordinal));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ComposerRendersInBothLanguagesAndWrapsAtNarrowWorkspaceWidth()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ComposerHeadlessTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("en-US");
            var window = Open(new DesktopSettings { Provider = new(ReasoningEffort: "high"), Language = "en-US" });
            try
            {
                Assert.Equal("Workspace edits", window.FindControl<TextBlock>("ExecutionModeLabel")!.Text);
                SaveFrame(window, "en-US-idle.png");
                window.FindControl<TextBox>("PromptBox")!.Text = "核对当前修改，并继续下一步。";
                Localization.SetLanguage("zh-CN");
                window.SetRunState(true);
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("工作区编辑", window.FindControl<TextBlock>("ExecutionModeLabel")!.Text);
                Assert.Equal("高", window.FindControl<TextBlock>("ReasoningLabel")!.Text);
                Assert.Contains("追加指令", window.FindControl<TextBlock>("ComposerHint")!.Text);
                SaveFrame(window, "zh-CN-running.png");
                window.SetRunState(false);
                window.Width = 1000;
                Click(window.FindControl<Button>("FilesButton")!);
                Dispatcher.UIThread.RunJobs();
                SaveFrame(window, "zh-CN-narrow.png");
                var options = window.FindControl<WrapPanel>("ComposerOptions")!;
                Assert.True(options.Bounds.Width > 0);
                foreach (var control in options.Children)
                {
                    Assert.True(control.Bounds.Left >= 0);
                    Assert.True(control.Bounds.Right <= options.Bounds.Width + 1);
                }
                Assert.True(window.FindControl<Button>("SendButton")!.Bounds.Width > 0);
            }
            finally { window.Close(); Localization.SetLanguage("en-US"); }
        }, CancellationToken.None);
    }

    private static MainWindow Open(DesktopSettings? settings = null)
    {
        var window = new MainWindow(settings ?? new DesktopSettings { Language = "en-US" }, connectHost: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveFrame(Window window, string filename)
    {
        Dispatcher.UIThread.RunJobs();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Workbench.slnx"))) directory = directory.Parent;
        var output = Path.Combine(directory?.FullName ?? Path.GetTempPath(), "artifacts", "ui-composer");
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(output, filename), PngBitmapEncoderOptions.Default);
    }
}
