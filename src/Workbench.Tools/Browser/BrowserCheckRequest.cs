using System.Text.Json;

namespace Workbench.Tools;

internal sealed record BrowserCheckAssertion(string Type, string? Text = null, int? Status = null);

internal sealed record BrowserCheckRequest(Uri Url, HashSet<string> AllowedOrigins,
    IReadOnlyList<BrowserCheckAssertion> Assertions, int TimeoutSeconds, bool CaptureScreenshot)
{
    internal static BrowserCheckRequest Parse(string arguments)
    {
        if (arguments.Length > 40_000) throw new ArgumentException("Browser arguments exceed 40,000 characters.");
        using var json = JsonDocument.Parse(arguments);
        var root = json.RootElement;
        CheckProperties(root, ["url", "allowedOrigins", "assertions", "timeoutSeconds", "captureScreenshot"]);
        var url = HttpUrl(String(root, "url"));
        var origins = new HashSet<string>(StringComparer.Ordinal) { Origin(url) };
        if (root.TryGetProperty("allowedOrigins", out var allowed))
        {
            if (allowed.ValueKind != JsonValueKind.Array || allowed.GetArrayLength() > 8) throw new ArgumentException("allowedOrigins accepts at most eight exact origins.");
            foreach (var item in allowed.EnumerateArray())
            {
                var origin = HttpUrl(String(item));
                if (origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
                    throw new ArgumentException("allowedOrigins must contain exact HTTP(S) origins without paths, queries or fragments.");
                origins.Add(Origin(origin));
            }
        }
        var timeout = 20;
        if (root.TryGetProperty("timeoutSeconds", out var seconds) && (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetInt32(out timeout) || timeout is < 1 or > 60))
            throw new ArgumentException("timeoutSeconds must be an integer from one to sixty.");
        var screenshot = false;
        if (root.TryGetProperty("captureScreenshot", out var capture))
        {
            if (capture.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("captureScreenshot must be boolean.");
            screenshot = capture.GetBoolean();
        }
        List<BrowserCheckAssertion> assertions = [];
        if (root.TryGetProperty("assertions", out var checks))
        {
            if (checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() > 16) throw new ArgumentException("assertions accepts at most sixteen checks.");
            foreach (var check in checks.EnumerateArray())
            {
                CheckProperties(check, ["type", "value"]);
                var type = String(check, "type");
                if (!check.TryGetProperty("value", out var value)) throw new ArgumentException("An assertion requires value.");
                if (type == "status")
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var status) || status is < 100 or > 599) throw new ArgumentException("status value must be an HTTP status integer.");
                    assertions.Add(new(type, Status: status));
                }
                else
                {
                    if (type is not ("text" or "selector" or "title")) throw new ArgumentException("Unknown assertion type.");
                    var text = String(value);
                    if (type == "selector" && (text.Length > 1024 || text.Contains(">>", StringComparison.Ordinal)))
                        throw new ArgumentException("selector requires one CSS selector of at most 1024 characters.");
                    assertions.Add(new(type, text));
                }
            }
        }
        return new(url, origins, assertions, timeout, screenshot);
    }

    internal static bool IsAllowed(string url, HashSet<string> origins) =>
        Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" &&
        parsed.UserInfo.Length == 0 && origins.Contains(Origin(parsed));

    private static Uri HttpUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 ||
            Uri.CheckHostName(uri.IdnHost) == UriHostNameType.Unknown || uri.Host.Contains('*'))
            throw new ArgumentException("URL must be an absolute HTTP(S) URL without embedded credentials.");
        return uri;
    }
    private static string Origin(Uri uri) => new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri.GetLeftPart(UriPartial.Authority);
    private static string String(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? String(value) : throw new ArgumentException(name + " is required.");
    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 2048 } text
        ? text : throw new ArgumentException("Values must be nonempty strings of at most 2048 characters.");
    private static void CheckProperties(JsonElement root, string[] known)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("Browser arguments and assertions must be objects.");
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!known.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new ArgumentException("Unknown or duplicate browser argument: " + property.Name);
    }
}
