using System.Diagnostics;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class RepositoryNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-repository-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    public RepositoryNavigationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task GitOwnsIgnoreRulesAndKeepsTrackedIgnoredFiles()
    {
        await Git("init", "-q");
        await Write("tracked.secret", "tracked");
        await Git("add", "tracked.secret");
        await Write(".gitignore", "*.secret\n!keep.secret\nignored/\n");
        await Write("private.secret", "secret");
        await Write("keep.secret", "visible");
        await Write("ignored/generated.cs", "generated");
        await Write("visible.cs", "class Visible { }");
        var inventory = await _service.InventoryAsync(_root);
        Assert.Equal("git", inventory.Source);
        Assert.Contains("tracked.secret", inventory.Files);
        Assert.Contains("keep.secret", inventory.Files);
        Assert.Contains("visible.cs", inventory.Files);
        Assert.DoesNotContain("private.secret", inventory.Files);
        Assert.DoesNotContain("ignored/generated.cs", inventory.Files);
        Assert.DoesNotContain(inventory.Files, path => path.StartsWith(".git/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NestedWorkspaceNeverListsSiblingPathsAndUsesParentIgnoreRules()
    {
        await Git("init", "-q");
        await Write(".gitignore", "*.generated.cs\n");
        await Write("outside.cs", "class Outside { }");
        await Write("src/中文.cs", "class Inside { }");
        await Write("src/hidden.generated.cs", "class Hidden { }");
        await Write("src/deep/Other.cs", "class Other { }");
        var inventory = await _service.InventoryAsync(Path.Combine(_root, "src"));
        Assert.Equal("git", inventory.Source);
        Assert.Equal(new[] { "deep/Other.cs", "中文.cs" }, inventory.Files);
    }

    [Fact]
    public async Task NonGitFallbackIsExplicitAndStillSkipsBuildDirectories()
    {
        await Write("visible.cs", "class Visible { }");
        await Write("node_modules/hidden.js", "hidden");
        var inventory = await _service.InventoryAsync(_root);
        Assert.Equal("filesystem", inventory.Source);
        Assert.Contains("gitignore", inventory.Warning!);
        Assert.Equal(new[] { "visible.cs" }, inventory.Files);
    }

    [Fact]
    public async Task FilePaginationAndSearchReportRemainingResults()
    {
        for (var i = 0; i < 5; i++) await Write($"src/{i}.cs", $"// target {i}\nclass C{i} {{ }}");
        await Write("other.txt", "target outside prefix");
        using var page = JsonDocument.Parse((await _service.ListFilePageAsync(_root, "src/", 1, 2)).Content);
        Assert.Equal(new[] { "src/1.cs", "src/2.cs" }, page.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(3, page.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.True(page.RootElement.GetProperty("truncated").GetBoolean());
        using var search = JsonDocument.Parse((await _service.SearchAsync(_root, "target", "src/", 3, 2)).Content);
        var matches = search.RootElement.GetProperty("matches").EnumerateArray().ToArray();
        Assert.Equal(2, matches.Length);
        Assert.Equal("src/3.cs", matches[0].GetProperty("path").GetString());
        Assert.Equal(1, matches[0].GetProperty("line").GetInt32());
        Assert.False(search.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, search.RootElement.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task SearchHonorsGitIgnoreAndRejectsEscapingPrefix()
    {
        await Git("init", "-q");
        await Write(".gitignore", "private.txt\n");
        await Write("private.txt", "UNIQUE_SECRET_NEEDLE");
        await Write("public.txt", "a public note");
        using var search = JsonDocument.Parse((await _service.SearchAsync(_root, "UNIQUE_SECRET_NEEDLE")).Content);
        Assert.Empty(search.RootElement.GetProperty("matches").EnumerateArray());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ListFilePageAsync(_root, "../"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SearchAsync(_root, "note", ".git/", 0, 10));
    }

    [Fact]
    public async Task ToolCallsWithOldArgumentsRemainValid()
    {
        await Write("sample.txt", "find me");
        var tools = WorkspaceTools.Create(_service).ToDictionary(tool => tool.Definition.Name);
        var list = await tools["list_files"].ExecuteAsync(new ToolCall("a", "list_files", "{}"), new ToolExecutionContext(_root));
        var search = await tools["search_files"].ExecuteAsync(new ToolCall("b", "search_files", "{\"query\":\"find\"}"), new ToolExecutionContext(_root));
        Assert.False(list.IsError);
        Assert.False(search.IsError);
        Assert.Contains("sample.txt", list.Content);
        Assert.Contains("sample.txt", search.Content);
    }

    [Fact]
    public async Task InventoryCancellationDoesNotBecomeFallbackSuccess()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.InventoryAsync(_root, cancelled.Token));
    }

    [Fact]
    public async Task BrokenGitMetadataNeverFallsBackToScanningIgnoredFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        await Write(".gitignore", "private.txt\n");
        await Write("private.txt", "not for automatic discovery");
        await Assert.ThrowsAsync<IOException>(() => _service.InventoryAsync(_root));
    }

    [Fact]
    public async Task LargeUnicodeSearchAndSymbolPagesStayValidAndContinueWithoutGaps()
    {
        await Write("中文.cs", "class 示例 {\n" + string.Join('\n', Enumerable.Range(0, 120).Select(i => $"public void 方法{i}({string.Join(", ", Enumerable.Range(0, 25).Select(p => $"string 参数{p}"))}) {{ }} // 命中{new string('中', 250)}")) + "\n}");
        List<int> lines = [];
        var offset = 0;
        var usedSmallerPage = false;
        while (true)
        {
            var response = await _service.SearchAsync(_root, "命中", "", offset, 1000);
            Assert.False(response.IsError);
            Assert.True(response.Content.Length <= 80_000);
            using var page = JsonDocument.Parse(response.Content);
            lines.AddRange(page.RootElement.GetProperty("matches").EnumerateArray().Select(item => item.GetProperty("line").GetInt32()));
            usedSmallerPage |= page.RootElement.GetProperty("responseTruncated").GetBoolean();
            if (page.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = page.RootElement.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset); offset = next;
        }
        Assert.True(usedSmallerPage);
        Assert.Equal(Enumerable.Range(2, 120), lines);
        List<string> methods = [];
        offset = 0;
        while (true)
        {
            var response = await _service.InspectSymbolsAsync(_root, "中文.cs", offset, 1000);
            Assert.False(response.IsError);
            Assert.True(response.Content.Length <= 80_000);
            using var page = JsonDocument.Parse(response.Content);
            methods.AddRange(page.RootElement.GetProperty("symbols").EnumerateArray().Select(item => item.GetProperty("name").GetString()!));
            if (page.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = page.RootElement.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset); offset = next;
        }
        Assert.Equal(new[] { "示例" }.Concat(Enumerable.Range(0, 120).Select(i => $"方法{i}")), methods);
    }

    [Fact]
    public async Task LongUnicodeFilePagesStayValidAndContinueWithoutDuplicates()
    {
        var names = Enumerable.Range(0, 200).Select(i => $"{i:D3}-{new string('中', 70)}.txt").ToArray();
        foreach (var name in names) await Write(name, "x");
        List<string> observed = [];
        var offset = 0;
        var shortened = false;
        while (true)
        {
            var response = await _service.ListFilePageAsync(_root, "", offset, 1000);
            Assert.False(response.IsError);
            Assert.True(response.Content.Length <= 80_000);
            using var page = JsonDocument.Parse(response.Content);
            observed.AddRange(page.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()!));
            shortened |= page.RootElement.GetProperty("responseTruncated").GetBoolean();
            if (page.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = page.RootElement.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset); offset = next;
        }
        Assert.True(shortened);
        Assert.Equal(names, observed);
    }

    [Fact]
    public async Task SymbolOutlineUsesSourceLinesAndExcludesMethodBodies()
    {
        const string source = "namespace Demo;\npublic class Calculator\n{\n    public int Value { get; set; }\n    public int Add(int left, int right)\n    {\n        return left + right;\n    }\n}\n";
        await Write("Calculator.cs", source);
        using var result = JsonDocument.Parse((await _service.InspectSymbolsAsync(_root, "Calculator.cs")).Content);
        Assert.False(result.RootElement.GetProperty("semanticAnalysis").GetBoolean());
        Assert.Empty(result.RootElement.GetProperty("syntaxErrors").EnumerateArray());
        var symbols = result.RootElement.GetProperty("symbols").EnumerateArray().ToArray();
        var method = Assert.Single(symbols, item => item.GetProperty("name").GetString() == "Add");
        Assert.Equal(5, method.GetProperty("startLine").GetInt32());
        Assert.Equal(8, method.GetProperty("endLine").GetInt32());
        Assert.Contains("int Add(int left, int right)", method.GetProperty("signature").GetString());
        Assert.DoesNotContain("return", method.GetProperty("signature").GetString());
        Assert.Contains(symbols, item => item.GetProperty("kind").GetString() == "property");
    }

    [Fact]
    public async Task BrokenCodeStillProvidesDeclarationsAndSyntaxDiagnostics()
    {
        await Write("Broken.cs", "class Broken { public void Work( { }");
        using var result = JsonDocument.Parse((await _service.InspectSymbolsAsync(_root, "Broken.cs")).Content);
        Assert.Contains(result.RootElement.GetProperty("symbols").EnumerateArray(), item => item.GetProperty("name").GetString() == "Broken");
        Assert.NotEmpty(result.RootElement.GetProperty("syntaxErrors").EnumerateArray());
    }

    [Fact]
    public async Task SymbolPagesAndDepthHaveExplicitLimits()
    {
        await Write("Many.cs", "class Many {\n" + string.Join('\n', Enumerable.Range(0, 12).Select(i => $"public void M{i}() {{ }}")) + "\n}");
        using var page = JsonDocument.Parse((await _service.InspectSymbolsAsync(_root, "Many.cs", 1, 3)).Content);
        Assert.Equal(3, page.RootElement.GetProperty("symbols").GetArrayLength());
        Assert.Equal(4, page.RootElement.GetProperty("nextOffset").GetInt32());
        Assert.True(page.RootElement.GetProperty("truncated").GetBoolean());
        await Write("Deep.cs", string.Concat(Enumerable.Range(0, 80).Select(i => $"class C{i} {{ ")) + new string('}', 80));
        using var deep = JsonDocument.Parse((await _service.InspectSymbolsAsync(_root, "Deep.cs")).Content);
        Assert.True(deep.RootElement.GetProperty("depthLimited").GetBoolean());
        Assert.True(deep.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task SymbolToolRejectsNonCSharpAndWorkspaceEscapes()
    {
        await Write("notes.txt", "ordinary text");
        Assert.True((await _service.InspectSymbolsAsync(_root, "notes.txt")).IsError);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.InspectSymbolsAsync(_root, "../outside.cs"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.InspectSymbolsAsync(_root, ".git/hidden.cs"));
        await Write("Large.cs", new string(' ', WorkspaceService.MaxFileBytes + 1));
        await Assert.ThrowsAsync<IOException>(() => _service.InspectSymbolsAsync(_root, "Large.cs"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.InspectSymbolsAsync(_root, "notes.cs", ct: cancelled.Token));
    }

    private async Task Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await error);
        await output;
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(parent, comparison) || !Path.GetFileName(full).StartsWith("workbench-repository-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (!Directory.Exists(full)) return;
        foreach (var path in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (!Path.GetFullPath(path).StartsWith(full + Path.DirectorySeparatorChar, comparison)) throw new InvalidOperationException("Unexpected fixture child path.");
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(full, true);
    }
}
