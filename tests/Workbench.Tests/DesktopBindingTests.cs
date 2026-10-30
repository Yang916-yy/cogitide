using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Workbench.Desktop;
using Xunit;

namespace Workbench.Tests;

public sealed class DesktopBindingTestApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new Style(selector => selector.OfType<Window>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontFamilyProperty, new FontFamily("Inter, Segoe UI, PingFang SC, Microsoft YaHei UI, Noto Sans CJK SC, sans-serif")),
                new Setter(TemplatedControl.FontSizeProperty, 13d)
            }
        });
        Styles.Add(new Style(selector => selector.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.FontSizeProperty, 12d),
                new Setter(TemplatedControl.FontWeightProperty, FontWeight.Medium)
            }
        });
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DesktopBindingTestApp>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public partial class DesktopLocalizationView : UserControl
{
    public DesktopLocalizationView() => AvaloniaXamlLoader.Load(this);
}

[Collection("Desktop localization")]
public sealed class DesktopBindingTests
{
    [Fact]
    public async Task TranslatedTabHeaderRemeasuresToTheSameWidthAsFreshEnglishHeader()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("zh-CN");
            var view = new DesktopLocalizationView();
            var window = new Window { Width = 680, Height = 760, Content = view };
            window.Show();
            var reference = new TabItem { Header = new TextBlock { Text = "General" } };
            var referenceWindow = new Window { Width = 680, Height = 760, Content = new TabControl { Items = { reference } } };
            try
            {
                Dispatcher.UIThread.RunJobs();
                using var chineseFrame = window.CaptureRenderedFrame();
                var tabs = view.GetVisualDescendants().OfType<TabControl>().Single();
                var selected = tabs.SelectedItem;
                Localization.SetLanguage("en-US");
                LocalizedControl.RemeasureTabHeaders(tabs);
                referenceWindow.Show();
                Dispatcher.UIThread.RunJobs();
                using var englishFrame = window.CaptureRenderedFrame();
                Assert.Same(selected, tabs.SelectedItem);
                var translated = view.FindControl<TabItem>("LocalizedTab")!;
                Assert.True(reference.Bounds.Width > 0);
                Assert.Equal(reference.Bounds.Width, translated.Bounds.Width, 1);
                var translatedText = translated.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "General");
                var referenceText = reference.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "General");
                Assert.Equal(referenceText.Bounds.Width, translatedText.Bounds.Width, 1);
                Assert.Equal(referenceText.DesiredSize.Width, translatedText.DesiredSize.Width, 1);
            }
            finally { referenceWindow.Close(); window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SelectedComboLabelChangesLanguageWithoutChangingSelection()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
            Localization.SetLanguage("zh-CN");
            var view = new DesktopLocalizationView();
            var window = new Window { Width = 680, Height = 760, Content = view };
            window.Show();
            try
            {
                var combo = view.FindControl<ComboBox>("LocalizedCombo")!;
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(combo.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "API 接口");
                var selected = combo.SelectedItem;
                Localization.SetLanguage("en-US");
                Dispatcher.UIThread.RunJobs();
                Assert.Same(selected, combo.SelectedItem);
                Assert.Contains(combo.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "API");
                Assert.DoesNotContain(combo.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "API 接口");
                Localization.SetLanguage("zh-CN");
                Dispatcher.UIThread.RunJobs();
                Assert.Same(selected, combo.SelectedItem);
                Assert.Contains(combo.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "API 接口");
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ExistingXamlControlsChangeLanguageWithoutRecreatingViewOrChangingContent()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
        Localization.SetLanguage("zh-CN");
        var view = new DesktopLocalizationView();
        var window = new Window { Content = view };
        window.Show();
        try
        {
            var text = view.FindControl<TextBlock>("LocalizedText")!;
            var button = view.FindControl<Button>("LocalizedButton")!;
            var tab = view.FindControl<TabItem>("LocalizedTab")!;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("设置", text.Text);
            Assert.Equal("保存设置", button.Content);
            Assert.Equal("通用", Assert.IsType<TextBlock>(tab.Header).Text);

            Localization.SetLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Settings", text.Text);
            Assert.Equal("Save settings", button.Content);
            Assert.Equal("General", Assert.IsType<TextBlock>(tab.Header).Text);
            Assert.Equal("用户 draft / do not translate", view.FindControl<TextBox>("UserDraft")!.Text);
            Assert.Equal("model 原始回复 / keep verbatim", view.FindControl<SelectableTextBlock>("ModelReply")!.Text);

            Localization.SetLanguage("zh-CN");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("设置", text.Text);
            Assert.Equal("保存设置", button.Content);
            Assert.Equal("通用", Assert.IsType<TextBlock>(tab.Header).Text);
        }
        finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task FormattedStatusBindingReevaluatesBothResourceAndDeferredArguments()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopBindingTestApp));
        await session.Dispatch(() =>
        {
        Localization.SetLanguage("zh-CN");
        var status = new TextBlock();
        LocalizedControl.Text(status, "SessionRestored", (Func<string>)(() => DesktopEventPresentation.SessionStatus("cancelled")));
        var window = new Window { Content = status };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("已恢复会话 · 已停止", status.Text);
            Localization.SetLanguage("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Chat restored · Stopped", status.Text);
        }
        finally { window.Close(); }
        }, CancellationToken.None);
    }
}
