using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Workbench.Desktop;

/// <summary>Native .NET resources; unknown system languages fall back to English.</summary>
public sealed class Localization : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new("Workbench.Desktop.Resources.Strings", typeof(Localization).Assembly);
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    public static Localization Instance { get; } = new();
    public static CultureInfo Culture { get; private set; } = ResolveCulture(null, SystemCulture);
    public static string? Language { get; private set; }
    public int Revision { get; private set; }
    public string this[string key] => Text(key);
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event Action? Changed;

    public static CultureInfo ResolveCulture(string? preference, CultureInfo systemCulture) =>
        CultureInfo.GetCultureInfo(preference is "zh-CN" or "en-US" ? preference
            : systemCulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en-US");

    public static void SetLanguage(string? preference)
    {
        Language = preference is "zh-CN" or "en-US" ? preference : null;
        Culture = ResolveCulture(Language, SystemCulture);
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Instance.Revision++;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(nameof(Revision)));
        Changed?.Invoke();
    }

    public static string Text(string key) => Resources.GetString(key, Culture) ?? key;
    public static string Format(string key, params object?[] arguments) => string.Format(Culture, Text(key), arguments);
}
