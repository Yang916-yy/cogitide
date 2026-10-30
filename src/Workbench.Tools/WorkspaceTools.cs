using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Tools;

public static class WorkspaceTools
{
    public static IReadOnlyList<IAgentTool> Create(WorkspaceService service, CommandSessionManager? commands = null)
    {
        List<IAgentTool> tools =
    [
        new FunctionTool(new("list_files", "List a page of workspace files. Git repositories use tracked and untracked files with standard gitignore rules; other directories use a bounded filesystem fallback. Inspect source/truncated/nextOffset. Paths are relative.", Schema(new { pathPrefix = StringProperty("Optional relative prefix, e.g. src/"), offset = OffsetProperty(), limit = LimitProperty(200) })), (arg, ctx, ct) => service.ListFilePageAsync(ctx.WorkspacePath, Optional(arg, "pathPrefix") ?? "", Integer(arg, "offset", 0), Integer(arg, "limit", 200), ct)),
        new FunctionTool(new("read_file", "Read a page of UTF-8 text with a SHA256 of the complete file. offset is a zero-based line. Continue using BOTH nextOffset and nextCharacterOffset with expectedSha256; a very long line may span pages. Inspect truncated before treating the file as fully read.", Schema(new { path = StringProperty("Relative file path"), offset = PageOffset(), limit = LimitProperty(200), characterOffset = PageOffset(), expectedSha256 = StringProperty("Optional full-file hash from the preceding page; rejects changed files.") }, "path")),
            (arg, ctx, ct) => service.ReadFilePageAsync(ctx.WorkspacePath, Required(arg, "path"), Integer(arg, "offset", 0), Integer(arg, "limit", 200), Integer(arg, "characterOffset", 0), Optional(arg, "expectedSha256"), ct)),
        new FunctionTool(new("search_files", "Search workspace text with match pagination, regex, glob filters and context. Default is case-insensitive literal search. Use expectedSnapshot for later pages. Respect scan/inventory/output truncation; narrow pathPrefix when scan limits are hit.", Schema(new { query = StringProperty("Literal text or .NET regular expression"), pathPrefix = StringProperty("Relative prefix; src/ narrows a directory before inventory limits"), offset = PageOffset(), limit = LimitProperty(100), regex = BooleanProperty("Interpret query as a regular expression, with bounded matching time."), glob = StringProperty("Optional relative file pattern supporting * and **, e.g. **/*.cs"), caseSensitive = BooleanProperty("Default false."), contextLines = new { type = "integer", minimum = 0, maximum = 10 }, expectedSnapshot = StringProperty("Optional content snapshot returned by the previous page.") }, "query")),
            (arg, ctx, ct) => service.SearchFilePageAsync(ctx.WorkspacePath, Required(arg, "query"), Optional(arg, "pathPrefix") ?? "", Integer(arg, "offset", 0), Integer(arg, "limit", 100), Boolean(arg, "regex"), Optional(arg, "glob"), Boolean(arg, "caseSensitive"), Integer(arg, "contextLines", 0), Optional(arg, "expectedSnapshot"), ct)),
        new FunctionTool(new("glob_files", "Find workspace files by a relative * / ** glob. Returns stable, paged paths respecting repository ignores and workspace boundaries. Use expectedSnapshot for later pages and inspect truncation.", Schema(new { pattern = StringProperty("Relative pattern, e.g. src/**/*.cs"), pathPrefix = StringProperty("Optional scope, e.g. src/"), offset = PageOffset(), limit = LimitProperty(200), expectedSnapshot = StringProperty("Optional preceding inventory snapshot.") }, "pattern")),
            (arg, ctx, ct) => service.GlobFilesAsync(ctx.WorkspacePath, Required(arg, "pattern"), Optional(arg, "pathPrefix") ?? "", Integer(arg, "offset", 0), Integer(arg, "limit", 200), Optional(arg, "expectedSnapshot"), ct)),
        new FunctionTool(new("inspect_symbols", "Inspect a C# file's namespace, type and member declaration outline using Microsoft's Roslyn parser. Returns signatures, exact source line ranges and syntax errors. This is syntax-only, not compilation, semantic references or proof of correctness. Does not run repository projects or analyzers.", Schema(new { path = StringProperty("Relative C# .cs file path"), offset = OffsetProperty(), limit = LimitProperty(100) }, "path")), (arg, ctx, ct) => service.InspectSymbolsAsync(ctx.WorkspacePath, Required(arg, "path"), Integer(arg, "offset", 0), Integer(arg, "limit", 100), ct)),
        new FunctionTool(new("write_file", "Create a new UTF-8 file or replace an existing one. Existing files REQUIRE expectedSha256 from read_file; use empty hash for new files. Preserve unrelated content.", Schema(new { path = StringProperty("Relative file path"), content = StringProperty("Entire file content"), expectedSha256 = StringProperty("Current SHA256 or empty for a new file") }, "path", "content", "expectedSha256"), true), (arg, ctx, ct) => service.WriteFileAsync(ctx.WorkspacePath, Required(arg, "path"), Required(arg, "content"), Required(arg, "expectedSha256"), ct)),
        new FunctionTool(new("replace_text", "Replace one exact occurrence in a UTF-8 file. Requires current SHA256; stale or ambiguous edits fail without writing.", Schema(new { path = StringProperty("Relative file path"), oldText = StringProperty("Unique exact text to replace"), newText = StringProperty("Replacement text"), expectedSha256 = StringProperty("SHA256 from read_file") }, "path", "oldText", "newText", "expectedSha256"), true), (arg, ctx, ct) => service.ReplaceTextAsync(ctx.WorkspacePath, Required(arg, "path"), Required(arg, "oldText"), Required(arg, "newText"), Required(arg, "expectedSha256"), ct)),
        new FunctionTool(new("apply_patch", "Apply a bounded multi-file *** Begin Patch / *** End Patch edit using Add File, Update File, Delete File and optional Move to. Use @@ context hunks with space/-/+ lines. expectedHashes maps EVERY existing source path to a current read_file SHA256. New destinations must not exist. All edits are preflighted; ambiguous or stale patches fail. Not a filesystem transaction across power loss.", Schema(new { patchText = StringProperty("Patch text in Codex/OpenCode marker format, paths relative to workspace."), expectedHashes = new { type = "object", additionalProperties = new { type = "string" }, description = "Current full-file hashes for update/delete/move source paths; empty object for adds only." } }, "patchText", "expectedHashes"), true),
            (arg, ctx, ct) => service.ApplyPatchAsync(ctx.WorkspacePath, Required(arg, "patchText"), Hashes(arg), ct),
            (call, workspace) => { using var args = JsonDocument.Parse(call.ArgumentsJson); return WorkspaceService.DescribePatchPaths(workspace, Required(args.RootElement, "patchText")); }),
        new FunctionTool(new("run_command", "Run a shell command in the workspace (PowerShell on Windows, /bin/sh on Unix). Not a sandbox. Run targeted build/tests; capture exit code and output. Avoid interactive commands. A nonzero exit is a failure to investigate.", Schema(new { command = StringProperty("Shell command"), timeoutSeconds = new { type = "integer", minimum = 1, maximum = 600 } }, "command", "timeoutSeconds"), true), (arg, ctx, ct) => service.RunCommandAsync(ctx.WorkspacePath, Required(arg, "command"), arg.GetProperty("timeoutSeconds").GetInt32(), ctx.OnOutput, ct, ctx.ExcludedEnvironmentVariables)),
        new FunctionTool(new("git_diff", "Read Git status, unstaged diff and staged diff. Untracked files are listed but their contents need read_file.", Schema(new { })), (_, ctx, ct) => service.GitDiffAsync(ctx.WorkspacePath, ct)),
        new FunctionTool(new("create_worktree", "Create a new branch and isolated Git worktree in a repository-owned sibling .cogitide-worktrees directory. Requires approval because it writes outside the selected checkout. Git owns checkout/index/locking. Does not switch this chat's workspace, merge, delete branches, or replay incomplete creation.", Schema(new { branch = StringProperty("Optional new branch; default codex/worktree-<id>. Existing branches are never reset."), startPoint = StringProperty("Optional commit or ref; default HEAD, resolved before checkout.") }), true),
            (arg, ctx, ct) => service.CreateWorktreeAsync(ctx.WorkspacePath, ctx.SessionId ?? "", Optional(arg, "branch"), Optional(arg, "startPoint") ?? "HEAD", ct)),
        new FunctionTool(new("list_worktrees", "List a page of Cogitide-managed worktrees for this Git repository, including registration and physical-path presence. Continue from nextOffset. Read-only; does not create, prune, adopt, or resume old processes.", Schema(new { offset = new { type = "integer", minimum = 0, maximum = 100 }, limit = new { type = "integer", minimum = 1, maximum = 100 } })),
            (arg, ctx, ct) => service.ListWorktreesAsync(ctx.WorkspacePath, ct, Integer(arg, "offset", 0), Integer(arg, "limit", 50))),
        new FunctionTool(new("worktree_status", "Read Git status and HEAD of a managed worktree. Reports dirty/untracked files and incomplete creation. This read-only tool cannot modify the checkout.", Schema(new { worktreeId = StringProperty("ID from create_worktree or list_worktrees") }, "worktreeId")),
            (arg, ctx, ct) => service.WorktreeStatusAsync(ctx.WorkspacePath, Required(arg, "worktreeId"), ct)),
        new FunctionTool(new("remove_worktree", "Remove a clean managed Git worktree created by this chat. Requires approval. Refuses dirty or untracked files, foreign owners, unknown Git registration, and links. Uses Git without force, retains the branch and never recursively deletes an arbitrary path.", Schema(new { worktreeId = StringProperty("Owned managed worktree ID") }, "worktreeId"), true),
            (arg, ctx, ct) => service.RemoveWorktreeAsync(ctx.WorkspacePath, ctx.SessionId ?? "", Required(arg, "worktreeId"), ct))
    ];
        if (commands is not null) tools.AddRange(
        [
            new FunctionTool(new("start_command", "Start an approved owned shell command and return a commandId promptly. Running is NOT success. Poll read_command. interactive enables bounded write_command input; tty additionally uses a real Porta.Pty terminal. lifetime=session explicitly retains a dev server across normal run completion until timeout, stop/chat cleanup or Host loss. Default lifetime=run stops at run end. Cancellation still stops commands. Not an OS sandbox.", Schema(new { command = StringProperty("Shell command in this workspace"), timeoutSeconds = new { type = "integer", minimum = 1, maximum = 604800 }, yieldMilliseconds = new { type = "integer", minimum = 0, maximum = 10000 }, interactive = BooleanProperty("Default false. Enable controlled stdin; the shell never inherits Host stdin."), tty = BooleanProperty("Requires interactive. Real terminal; may contain ANSI sequences. PTY closeInput is unsupported; send terminal controls explicitly or stop."), lifetime = new { type = "string", @enum = new[] { "run", "session" }, description = "Default run. session is explicit chat-owned dev-server retention, bounded by timeoutSeconds." } }, "command"), true),
                (arg, ctx, ct) => commands.StartAsync(ctx, Required(arg, "command"), Integer(arg, "timeoutSeconds", 21600), Integer(arg, "yieldMilliseconds", 1000), ct, Boolean(arg, "interactive"), Boolean(arg, "tty"), Optional(arg, "lifetime") ?? "run")),
            new FunctionTool(new("write_command", "Write up to 16384 UTF-8 bytes to an owned interactive command (1 MiB total per command). Serialized writes acknowledge flushing, not application success. Pipe closeInput sends real EOF and prevents later writes. PTYs cannot half-close; use terminal controls explicitly or stop_command. Rejected or unknown input is never retried automatically. Requires approval because input can execute commands.", Schema(new { commandId = StringProperty("Owned interactive command ID"), input = StringProperty("Text, including explicit newlines/terminal controls where needed. Empty allowed only with closeInput."), closeInput = BooleanProperty("Default false. Close pipe input after this write; unsupported for tty.") }, "commandId", "input"), true),
                (arg, ctx, ct) => commands.WriteAsync(ctx, Required(arg, "commandId"), Required(arg, "input"), Boolean(arg, "closeInput"), ct)),
            new FunctionTool(new("read_command", "Read status and a page of retained output from an owned command. Use the prior nextOffset byte cursor to continue. waitMilliseconds optionally waits before polling. Check running/status/exitCode/outputTruncated; a running or interrupted command is not verification.", Schema(new { commandId = StringProperty("ID returned by start_command in this chat"), offset = new { type = "integer", minimum = 0, maximum = 4194304 }, maxCharacters = new { type = "integer", minimum = 2, maximum = 12000 }, waitMilliseconds = new { type = "integer", minimum = 0, maximum = 10000 } }, "commandId")),
                (arg, ctx, ct) => commands.ReadAsync(ctx, Required(arg, "commandId"), Integer(arg, "offset", 0), Integer(arg, "maxCharacters", 12000), Integer(arg, "waitMilliseconds", 0), ct)),
            new FunctionTool(new("stop_command", "Stop an owned command and its process tree, retaining logs for inspection. This cannot start commands or access another chat's jobs.", Schema(new { commandId = StringProperty("Owned command session ID"), offset = new { type = "integer", minimum = 0, maximum = 4194304 } }, "commandId")),
                (arg, ctx, ct) => commands.StopAsync(ctx, Required(arg, "commandId"), Integer(arg, "offset", 0), ct))
        ]);
        tools.Add(service.CreateCSharpSemanticTool());
        return tools;
    }

    private static object StringProperty(string description) => new { type = "string", description };
    private static object BooleanProperty(string description) => new { type = "boolean", description };
    private static object PageOffset() => new { type = "integer", minimum = 0, maximum = 16777216 };
    private static object OffsetProperty() => new { type = "integer", minimum = 0, maximum = 100000, description = "Optional zero-based page offset; default 0." };
    private static object LimitProperty(int defaultLimit) => new { type = "integer", minimum = 1, maximum = 1000, description = $"Optional page size; default {defaultLimit}." };
    private static string Schema(object properties, params string[] required) => JsonSerializer.Serialize(new { type = "object", properties, required, additionalProperties = false });
    private static string Required(JsonElement arg, string name) => arg.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException($"Missing string argument: {name}");
    private static string? Optional(JsonElement arg, string name) => arg.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new ArgumentException($"Expected string argument: {name}") : null;
    private static int Integer(JsonElement arg, string name, int fallback) => arg.TryGetProperty(name, out var value) ? value.TryGetInt32(out var number) ? number : throw new ArgumentException($"Expected integer argument: {name}") : fallback;
    private static bool Boolean(JsonElement arg, string name) => arg.TryGetProperty(name, out var value)
        ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new ArgumentException($"Expected boolean argument: {name}") : false;
    private static IReadOnlyDictionary<string, string> Hashes(JsonElement arg)
    {
        if (!arg.TryGetProperty("expectedHashes", out var hashes) || hashes.ValueKind != JsonValueKind.Object) throw new ArgumentException("expectedHashes must be an object.");
        return hashes.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw new ArgumentException("Expected hash string."), StringComparer.Ordinal);
    }
    private sealed class FunctionTool(ToolDefinition definition, Func<JsonElement, ToolExecutionContext, CancellationToken, Task<ToolResult>> execute,
        Func<ToolCall, string, IReadOnlyList<string>>? paths = null) : IAgentTool, IWorkspacePathTool
    {
        public ToolDefinition Definition => definition;
        public IReadOnlyList<string> GetPaths(ToolCall call, string workspace) => paths?.Invoke(call, workspace) ?? [];
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                using var args = JsonDocument.Parse(call.ArgumentsJson);
                return await execute(args.RootElement, context, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or System.ComponentModel.Win32Exception)
            {
                return new ToolResult(e.Message, true);
            }
        }
    }
}
