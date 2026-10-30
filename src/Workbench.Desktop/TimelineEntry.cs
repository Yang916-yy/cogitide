using L = Workbench.Desktop.Localization;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Workbench.Desktop;

internal sealed class TimelineEntry(string label, string text, string accent = "#8C93AA") : INotifyPropertyChanged
{
    private string _text = text;
    private string _label = label;
    private Func<string>? _localizedText;
    public string Label { get => _label; set { _label = value; Changed(); PresentationChanged(); } }
    public string Text { get => _localizedText?.Invoke() ?? _text; set { _localizedText = null; _text = value; Changed(); Changed(nameof(ToolSummary)); } }
    public string Accent { get; } = accent;
    public bool IsUser => Label.StartsWith("YOU", StringComparison.Ordinal) || Label == "USER";
    public bool IsAssistant => Label == "ASSISTANT";
    public bool IsTool => Label.StartsWith("TOOL", StringComparison.Ordinal) || Label.StartsWith("DONE", StringComparison.Ordinal) || Label.StartsWith("FAILED", StringComparison.Ordinal);
    public bool IsFailure => Label.StartsWith("FAILED", StringComparison.Ordinal) || Label.Contains("ERROR", StringComparison.Ordinal);
    public bool IsUsage => Label == "TOKENS";
    public bool IsNotice => !IsUser && !IsAssistant && !IsTool && !IsUsage;
    public bool HasUserLabel => IsUser && Label.Contains('·');
    public string StatusForeground => IsFailure || IsNotice ? "#C5A184" : "#9CA3AF";
    public string DisplayLabel => IsUser ? (HasUserLabel ? L.Text(Label[(Label.IndexOf('·') + 1)..].Trim()) : "")
        : IsTool ? (IsFailure ? L.Text("FailedPrefix") : Label.StartsWith("DONE", StringComparison.Ordinal) ? L.Text("CompletedPrefix") : "") + ToolTitle(Label.Split('·').Last().Trim())
        : Label switch { "NOTICE" => L.Text("Notice"), "ERROR" => L.Text("ExecutionFailed"), "STOPPED" => L.Text("Stopped"), "CONNECTION" => L.Text("ConnectionStatus"), "CONTEXT" => L.Text("Context"), _ => Label.Contains('.') ? L.Text("Progress") : L.Text(Label) };
    public string ToolSummary
    {
        get
        {
            if (!IsTool) return "";
            try
            {
                using var doc = JsonDocument.Parse(Text.Split("\n\n", 2)[0]);
                foreach (var property in new[] { "path", "command", "pattern", "url" })
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                        return value.GetString()?.Replace('\n', ' ') ?? "";
            }
            catch (JsonException) { }
            return L.Text("ViewDetails");
        }
    }
    internal static string ToolTitle(string name) => name switch
    {
        "read_file" => L.Text("ToolReadFile"), "list_files" or "glob_files" => L.Text("ToolListFiles"), "search_files" or "search_text" => L.Text("ToolSearchFiles"), "replace_text" or "write_file" or "apply_patch" => L.Text("ToolEditFile"),
        "run_command" or "start_command" or "read_command" or "stop_command" => L.Text("ToolCommand"), "git_diff" => L.Text("ToolDiff"), "web_fetch" => L.Text("ToolWeb"), "TOOL ERROR" => L.Text("ToolExecution"), "TOOL RESULT" or "TOOL" => L.Text("ToolResult"), _ => name
    };
    public void LocalizeText(Func<string> formatter) { _localizedText = formatter; Changed(nameof(Text)); }
    public void RefreshLocalization() { PresentationChanged(); Changed(nameof(Text)); }
    private void PresentationChanged()
    {
        foreach (var property in new[] { nameof(IsUser), nameof(IsAssistant), nameof(IsTool), nameof(IsFailure), nameof(IsUsage), nameof(IsNotice), nameof(HasUserLabel), nameof(StatusForeground), nameof(DisplayLabel), nameof(ToolSummary) }) Changed(property);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
