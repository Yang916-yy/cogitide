namespace Workbench.Desktop;

/// <summary>Public product identity. Storage and executable identities remain compatible.</summary>
public static class ProductIdentity
{
    public const string DisplayName = "Cogitide";
    public static string PreviewLabel => Localization.Text("PreviewLabel");
    public static string WindowTitle => DisplayName + " · " + PreviewLabel;
    public const string Tagline = "Cogito, ergo sum.";
}
