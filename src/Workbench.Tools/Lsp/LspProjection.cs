using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Workbench.Tools;

public sealed partial class LspToolSet
{
    private List<JsonNode> Project(string action, JsonElement result, string path, Source[] sources, string encoding, out int dropped, out bool projectionTruncated)
    {
        List<JsonNode> projected = [];
        var omitted = 0;
        var truncated = false;
        var byPath = sources.ToDictionary(s => s.Path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var current = byPath[path];
        if (result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { dropped = 0; projectionTruncated = false; return projected; }
        string Bounded(string value, int maximum)
        {
            if (value.Length > maximum) truncated = true;
            return Clip(value, maximum);
        }
        JsonObject? Location(JsonElement record, string? defaultPath = null)
        {
            try
            {
                var uri = record.TryGetProperty("targetUri", out var target) ? target.GetString() :
                    record.TryGetProperty("uri", out var ordinary) ? ordinary.GetString() : null;
                var relative = defaultPath;
                if (uri is not null)
                {
                    relative = Relative(WorkspaceService.Resolve(_workspace, Relative(LspFileUris.FilePath(uri))));
                }
                if (relative is null || !byPath.TryGetValue(relative, out var source)) throw new ArgumentException("Location outside bounded source snapshot.");
                var range = record.TryGetProperty("targetSelectionRange", out var selection) ? selection :
                    record.TryGetProperty("selectionRange", out var nameRange) ? nameRange : record.GetProperty("range");
                var start = range.GetProperty("start");
                var end = range.GetProperty("end");
                var startLine = start.GetProperty("line").GetInt32();
                var endLine = end.GetProperty("line").GetInt32();
                var startColumn = LspPositions.ToUtf16(LineAt(source.Text, startLine), start.GetProperty("character").GetInt32(), encoding);
                var endColumn = LspPositions.ToUtf16(LineAt(source.Text, endLine), end.GetProperty("character").GetInt32(), encoding);
                if (endLine < startLine || endLine == startLine && endColumn < startColumn) throw new ArgumentException("Reversed range.");
                return new JsonObject { ["path"] = relative, ["line"] = startLine + 1, ["column"] = startColumn + 1,
                    ["endLine"] = endLine + 1, ["endColumn"] = endColumn + 1, ["sha256"] = source.Sha256 };
            }
            catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException or IOException)
            { omitted++; return null; }
        }
        void Symbols(JsonElement symbol, int depth)
        {
            if (depth > 32 || projected.Count >= 10000) { truncated = true; return; }
            var location = symbol.TryGetProperty("location", out var nested) ? Location(nested) : Location(symbol, path);
            if (location is not null)
            {
                location["name"] = Bounded(symbol.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "", 512);
                if (symbol.TryGetProperty("kind", out var kind)) location["kind"] = kind.GetInt32();
                if (symbol.TryGetProperty("detail", out var detail)) location["detail"] = Bounded(detail.GetString() ?? "", 2048);
                projected.Add(location);
            }
            if (symbol.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                foreach (var child in children.EnumerateArray()) Symbols(child, depth + 1);
        }
        if (action == "hover")
        {
            var content = result.TryGetProperty("contents", out var contents) ? contents : default;
            var strings = new List<string>();
            void Hover(JsonElement value)
            {
                if (strings.Count >= 32) { truncated = true; return; }
                if (value.ValueKind == JsonValueKind.String) strings.Add(value.GetString() ?? "");
                else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var text)) strings.Add(text.GetString() ?? "");
                else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Hover(item);
            }
            Hover(content);
            var text = string.Join("\n\n", strings);
            projected.Add(new JsonObject { ["path"] = path, ["sha256"] = current.Sha256, ["content"] = Bounded(text, 8192),
                ["contentTrust"] = "untrusted language-server content" });
        }
        else
        {
            var entries = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().ToArray() : [result];
            foreach (var entry in entries)
            {
                if (projected.Count >= 10000) { truncated = true; break; }
                if (action == "symbols") { Symbols(entry, 0); continue; }
                var location = Location(entry, action == "diagnostics" ? path : null);
                if (location is null) continue;
                if (action == "diagnostics")
                {
                    location["message"] = Bounded(entry.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "", 4096);
                    if (entry.TryGetProperty("severity", out var severity)) location["severity"] = severity.GetInt32();
                    if (entry.TryGetProperty("code", out var code)) location["code"] = Bounded(code.ToString(), 128);
                    if (entry.TryGetProperty("source", out var origin)) location["source"] = Bounded(origin.GetString() ?? "", 128);
                }
                projected.Add(location);
            }
        }
        dropped = omitted;
        projectionTruncated = truncated;
        return projected;
    }

    internal static string LineAt(string text, int line)
    {
        if (line < 0) throw new ArgumentOutOfRangeException(nameof(line));
        var current = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            if (current == line) return text[start..index];
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            current++; start = index + 1;
        }
        if (current == line) return text[start..];
        throw new ArgumentOutOfRangeException(nameof(line), "Position line is outside the source.");
    }
    private static string Clip(string value, int limit) => value.Length <= limit ? value : value[..(char.IsHighSurrogate(value[limit - 1]) ? limit - 1 : limit)];
}

/// <summary>LSP's negotiated UTF-8/UTF-16/UTF-32 character units, using BCL Unicode scalar decoding.</summary>
public static class LspPositions
{
    public static int FromUtf16(string line, int character, string encoding)
    {
        ValidateUtf16(line, character);
        return encoding switch
        {
            "utf-16" => character,
            "utf-8" => Encoding.UTF8.GetByteCount(line.AsSpan(0, character)),
            "utf-32" => ScalarCount(line.AsSpan(0, character)),
            _ => throw new ArgumentException("Unsupported LSP position encoding.")
        };
    }
    private static int ScalarCount(ReadOnlySpan<char> text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes()) count++;
        return count;
    }
    public static int ToUtf16(string line, int character, string encoding)
    {
        if (character < 0) throw new ArgumentOutOfRangeException(nameof(character));
        if (encoding == "utf-16") { ValidateUtf16(line, character); return character; }
        if (encoding is not ("utf-8" or "utf-32")) throw new ArgumentException("Unsupported LSP position encoding.");
        var units = 0;
        var utf16 = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (units == character) return utf16;
            units += encoding == "utf-8" ? rune.Utf8SequenceLength : 1;
            utf16 += rune.Utf16SequenceLength;
            if (units > character) throw new ArgumentException("Position splits a Unicode scalar.");
        }
        return units == character ? utf16 : throw new ArgumentOutOfRangeException(nameof(character));
    }
    private static void ValidateUtf16(string line, int character)
    {
        if (character < 0 || character > line.Length || character > 0 && character < line.Length &&
            char.IsHighSurrogate(line[character - 1]) && char.IsLowSurrogate(line[character]))
            throw new ArgumentOutOfRangeException(nameof(character), "Position is outside the line or splits a surrogate pair.");
    }
}
