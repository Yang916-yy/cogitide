namespace Workbench.Tools;

/// <summary>Normalize standard file URIs before applying the existing workspace boundary policy.</summary>
internal static class LspFileUris
{
    public static string FilePath(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile ||
            uri.Host.Length != 0 && !uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Language-server location is not a local file URI.");
        var path = uri.LocalPath;
        // vscode-uri serializes Windows drive colons as %3A. Uri.LocalPath decodes that form to
        // /c:/path instead of C:\path; remove only the verified drive-prefix slash, not arbitrary roots.
        if (OperatingSystem.IsWindows() && path.Length >= 4 && path[0] == '/' &&
            char.IsAsciiLetter(path[1]) && path[2] == ':' && path[3] is '/' or '\\')
            path = path[1..].Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Language-server file URI is not an absolute local path.");
        return Path.GetFullPath(path);
    }

    public static string Canonical(string value) => new Uri(FilePath(value)).AbsoluteUri;
}
