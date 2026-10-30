using L = Workbench.Desktop.Localization;
using System.Globalization;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Desktop;

internal static class DesktopEventPresentation
{
    internal static IReadOnlyList<AgentEvent> UnconfirmedInstructions(IEnumerable<AgentEvent> events)
    {
        var pending = new Dictionary<string, List<AgentEvent>>(StringComparer.Ordinal);
        foreach (var evt in events.OrderBy(x => x.Sequence))
        {
            if (evt.Type == "input.queued" && evt.Text is not null)
            {
                if (!pending.TryGetValue(evt.RunId, out var queue)) pending[evt.RunId] = queue = [];
                queue.Add(evt);
            }
            else if (evt.Type == "input.steered" && pending.TryGetValue(evt.RunId, out var queue))
            {
                // Identical messages are distinct inputs; consume only the oldest matching input in this run.
                var index = queue.FindIndex(item => string.Equals(item.Text, evt.Text, StringComparison.Ordinal));
                if (index >= 0) queue.RemoveAt(index);
            }
        }
        return pending.Values.SelectMany(x => x).OrderBy(x => x.Sequence).ToArray();
    }

    internal static string SessionStatus(string status) => status switch
    {
        "completed" => L.Text("Completed"),
        "paused" => L.Text("Paused"),
        "interrupted" => L.Text("Interrupted"),
        "cancelled" => L.Text("Stopped"),
        "failed" => L.Text("ExecutionFailed"),
        "running" => L.Text("RunningStatus"),
        "idle" => L.Text("Ready"),
        _ => status
    };

    internal static string Usage(string? json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json ?? "{}");
            var root = doc.RootElement;
            string Count(string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) ? count.ToString("N0", CultureInfo.InvariantCulture) : L.Text("Unknown");
            var compaction = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("purpose", out var purpose)
                && purpose.ValueKind == JsonValueKind.String && purpose.GetString() == "compaction";
            var text = L.Format(compaction ? "CompactionUsage" : "ModelUsage", Count("inputTokens"), Count("outputTokens"));
            foreach (var (field, key) in new[] { ("cachedInputTokens", "CachedUsage"), ("cacheWriteTokens", "CacheWriteUsage"), ("reasoningTokens", "ReasoningUsage") })
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(field, out var value) && value.TryGetInt64(out var count) && count > 0)
                    text += " · " + L.Format(key, count.ToString("N0", CultureInfo.InvariantCulture));
            return text;
        }
        catch (JsonException) { return L.Text("UsageUnavailable"); }
        catch (InvalidOperationException) { return L.Text("UsageUnavailable"); }
    }
}
