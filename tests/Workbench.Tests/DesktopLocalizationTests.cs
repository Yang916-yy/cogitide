using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using Workbench.Desktop;
using Xunit;

namespace Workbench.Tests;

[CollectionDefinition("Desktop localization", DisableParallelization = true)]
public sealed class DesktopLocalizationCollection;

[Collection("Desktop localization")]
public sealed class DesktopLocalizationTests
{
    [Theory]
    [InlineData(null, "zh-TW", "zh-CN")]
    [InlineData(null, "de-DE", "en-US")]
    [InlineData("en-US", "zh-CN", "en-US")]
    [InlineData("zh-CN", "en-US", "zh-CN")]
    [InlineData("invalid", "en-GB", "en-US")]
    public void LanguagePreferenceAndSystemFallbackAreDeterministic(string? preference, string system, string expected) =>
        Assert.Equal(expected, Localization.ResolveCulture(preference, CultureInfo.GetCultureInfo(system)).Name);

    [Fact]
    public void SatelliteResourcesCoverEveryKeyAndKeepFormatPlaceholdersCompatible()
    {
        var resources = new ResourceManager("Workbench.Desktop.Resources.Strings", typeof(Localization).Assembly);
        using var english = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        using var chinese = resources.GetResourceSet(CultureInfo.GetCultureInfo("zh-CN"), true, false)!;
        Assert.NotNull(chinese);
        Assert.True(english.Cast<DictionaryEntry>().Count() > 150);
        foreach (DictionaryEntry resource in english)
        {
            var translated = chinese.GetString((string)resource.Key);
            Assert.False(string.IsNullOrWhiteSpace(translated), $"Missing zh-CN resource: {resource.Key}");
            Assert.Equal(CompositeFormat.Parse((string)resource.Value!).MinimumArgumentCount,
                CompositeFormat.Parse(translated).MinimumArgumentCount);
        }
    }

    [Fact]
    public void SwitchingLanguageRefreshesPresentationWithoutChangingUserOrToolContent()
    {
        const string raw = "中文 user text / unchanged model and tool evidence";
        var entry = new TimelineEntry("DONE · read_file", raw);
        var changes = new List<string?>();
        entry.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Localization.SetLanguage("zh-CN");
        Assert.Equal("完成 · 读取文件", entry.DisplayLabel);
        Localization.SetLanguage("en-US");
        entry.RefreshLocalization();
        Assert.Equal("Done · Read file", entry.DisplayLabel);
        Assert.Equal(raw, entry.Text);
        Assert.Contains(nameof(TimelineEntry.DisplayLabel), changes);
        Assert.Equal("Stopped", DesktopEventPresentation.SessionStatus("cancelled"));
        Assert.Equal("Model call · 1,234 input tokens · 5 output tokens", DesktopEventPresentation.Usage("{\"inputTokens\":1234,\"outputTokens\":5}"));
        Assert.Equal("Conversation summary · 1,234 input tokens · 5 output tokens", DesktopEventPresentation.Usage("{\"inputTokens\":1234,\"outputTokens\":5,\"purpose\":\"compaction\"}"));
    }

    [Fact]
    public void RuntimeResourceBindingsReceiveChangeNotification()
    {
        var changes = new List<string?>();
        System.ComponentModel.PropertyChangedEventHandler handler = (_, args) => changes.Add(args.PropertyName);
        Localization.Instance.PropertyChanged += handler;
        try
        {
            Localization.SetLanguage("en-US");
            Assert.Equal("Settings", Localization.Instance["Connections"]);
            Localization.SetLanguage("zh-CN");
            Assert.Equal("设置", Localization.Instance["Connections"]);
            Assert.Contains("Item[]", changes);
            Assert.Contains(nameof(Localization.Revision), changes);
        }
        finally { Localization.Instance.PropertyChanged -= handler; }
    }
}
