using System.Diagnostics;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class SemanticNavigationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-semantic-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    public SemanticNavigationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DefinitionBindsTheActualCrossFileOverloadAndReferencesExcludeSameNames()
    {
        const string api = """
            namespace Example;
            public static class Api
            {
                public static string Pick(int value) => "number";
                public static string Pick(string value) => "text";
            }
            """;
        const string use = """
            namespace Example;
            public sealed class Use
            {
                public string Run()
                {
                    int Pick = 7;
                    // Api.Pick(123) is only a comment.
                    return Api.Pick(Pick) + Api.Pick("hi");
                }
                public string Other() => Elsewhere.Api.Pick(1);
            }
            """;
        await Write("Api.cs", api);
        await Write("Use.cs", use);
        await Write("Elsewhere.cs", "namespace Elsewhere; public static class Api { public static string Pick(int n) => n.ToString(); }");
        var numeric = Position(use, "Pick(Pick)");
        using var definition = await Analyze("definition", "Use.cs", numeric);
        var location = Assert.Single(Results(definition));
        Assert.Equal("Api.cs", location.GetProperty("path").GetString());
        Assert.Equal(Position(api, "Pick(int").Line, location.GetProperty("line").GetInt32());
        Assert.Contains("int", definition.RootElement.GetProperty("symbol").GetProperty("display").GetString());
        using var references = await Analyze("references", "Use.cs", numeric);
        var reference = Assert.Single(Results(references));
        Assert.Equal(numeric.Line, reference.GetProperty("line").GetInt32());
        Assert.Equal(numeric.Column, reference.GetProperty("column").GetInt32());
        using var textDefinition = await Analyze("definition", "Use.cs", Position(use, "Pick(\"hi\")"));
        Assert.Equal(Position(api, "Pick(string").Line, Assert.Single(Results(textDefinition)).GetProperty("line").GetInt32());
        using var local = await Analyze("references", "Use.cs", Position(use, "Pick ="));
        Assert.Equal(numeric.Column + "Pick(".Length, Assert.Single(Results(local)).GetProperty("column").GetInt32());
        var comment = Position(use, "Pick(123)");
        Assert.True((await _service.CSharpSemanticAsync(_root, "definition", "Use.cs", comment.Line, comment.Column)).IsError);
    }

    [Fact]
    public async Task GenericMethodsKeepOneSymbolIdentityAcrossConstructedInvocations()
    {
        const string source = "class C { T Echo<T>(T x) => x; int A() => Echo<int>(1); string B() => Echo<string>(\"x\"); }";
        await Write("C.cs", source);
        using var references = await Analyze("references", "C.cs", Position(source, "Echo<T>"));
        Assert.Equal(2, Results(references).Length);
        Assert.All(Results(references), result => Assert.Equal("reference", result.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task MethodReferencesPreserveNameofEscapedIdentifiersAndLocalFunctions()
    {
        const string source = """
            class C
            {
                void @class() { }
                void \u006d() { }
                int Use()
                {
                    @class(); m();
                    var a = nameof(@class);
                    var b = nameof(m);
                    int @event(int value) => value;
                    return @event(1) + @event(2);
                }
            }
            """;
        await Write("C.cs", source);
        using var keyword = await Analyze("references", "C.cs", Position(source, "@class()"));
        Assert.Equal(2, Results(keyword).Length);
        Assert.Contains(Results(keyword), result => result.GetProperty("line").GetInt32() == Position(source, "@class);").Line);
        using var unicode = await Analyze("references", "C.cs", Position(source, "\\u006d()"));
        Assert.Equal(2, Results(unicode).Length);
        Assert.Contains(Results(unicode), result => result.GetProperty("line").GetInt32() == Position(source, "nameof(m)").Line);
        using var local = await Analyze("references", "C.cs", Position(source, "@event(int"));
        Assert.Equal(2, Results(local).Length);
    }

    [Theory]
    [InlineData("@field;")]
    [InlineData("@property {")]
    [InlineData("@param)")]
    [InlineData("@local =")]
    public async Task MemberParameterAndLocalReferencesKeepEscapedIdentifierUses(string declaration)
    {
        const string source = """
            class C
            {
                int @field;
                int @property { get; set; }
                int M(int @param)
                {
                    int @local = @param;
                    @field = @local;
                    @property = @field;
                    return @local + @property + @param;
                }
            }
            """;
        await Write("C.cs", source);
        using var references = await Analyze("references", "C.cs", Position(source, declaration));
        Assert.Equal(2, Results(references).Length);
        Assert.All(Results(references), result => Assert.Equal("reference", result.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task ConstructorOverloadsAndTypeAliasesRemainSemanticallyBound()
    {
        const string source = """
            using Alias = Widget;
            class Widget
            {
                public Widget(int value) { }
                public Widget(string value) { }
            }
            class Consumer
            {
                Widget first = new Alias(1);
                Widget second = new Widget("text");
            }
            """;
        await Write("C.cs", source);
        using var numeric = await Analyze("references", "C.cs", Position(source, "Widget(int"));
        var location = Assert.Single(Results(numeric));
        var alias = Position(source, "Alias(1)");
        Assert.Equal(alias.Line, location.GetProperty("line").GetInt32());
        Assert.Equal(alias.Column, location.GetProperty("column").GetInt32());
        using var text = await Analyze("references", "C.cs", Position(source, "Widget(string"));
        Assert.Equal(Position(source, "Widget(\"text\")").Line, Assert.Single(Results(text)).GetProperty("line").GetInt32());
        var typeDeclaration = Position(source, "class Widget");
        using var type = await Analyze("references", "C.cs", (typeDeclaration.Line, typeDeclaration.Column + "class ".Length));
        Assert.Contains(Results(type), result => result.GetProperty("line").GetInt32() == alias.Line && result.GetProperty("column").GetInt32() == alias.Column);
    }

    [Fact]
    public async Task UserDefinedOperatorsAndConversionsRemainSemanticallyBound()
    {
        const string source = """
            struct Number
            {
                public static Number operator +(Number a, Number b) => a;
                public static explicit operator int(Number n) => 0;
                int Read(Number other) => (int)(this + other);
            }
            """;
        await Write("C.cs", source);
        using var addition = await Analyze("references", "C.cs", Position(source, "+(Number"));
        Assert.Equal(Position(source, "+ other").Column, Assert.Single(Results(addition)).GetProperty("column").GetInt32());
        using var conversion = await Analyze("references", "C.cs", Position(source, "explicit operator"));
        Assert.Equal(Position(source, "int)(this").Column, Assert.Single(Results(conversion)).GetProperty("column").GetInt32());
    }

    [Fact]
    public async Task OverloadedNameofGroupsNeverChooseAnArbitraryMethod()
    {
        const string source = "class C { void F(int value) { } void F(string value) { } void Use() { var name = nameof(F); F(1); } }";
        await Write("C.cs", source);
        using var references = await Analyze("references", "C.cs", Position(source, "F(int"));
        Assert.Equal(Position(source, "F(1)").Column, Assert.Single(Results(references)).GetProperty("column").GetInt32());
        var group = Position(source, "F);");
        Assert.True((await _service.CSharpSemanticAsync(_root, "definition", "C.cs", group.Line, group.Column)).IsError);
    }

    [Fact]
    public async Task AmbiguousOverloadsNeverChooseAnArbitraryCandidate()
    {
        const string source = "class C { void F(string x) {} void F(System.Uri x) {} void M() { F(null); } }";
        await Write("C.cs", source);
        var point = Position(source, "F(null)");
        var result = await _service.CSharpSemanticAsync(_root, "definition", "C.cs", point.Line, point.Column);
        Assert.True(result.IsError);
        Assert.Contains("uniquely bound", result.Content);
    }

    [Fact]
    public async Task DiagnosticsUseCompilerBindingAndExposeTheSourceOnlyLimit()
    {
        await Write("C.cs", "class C { int M() => \"wrong type\"; }");
        using var diagnostics = await Analyze("diagnostics", "C.cs", (1, 1));
        Assert.Contains(Results(diagnostics), result => result.GetProperty("code").GetString() == "CS0029");
        Assert.All(Results(diagnostics), result => Assert.Equal("C.cs", result.GetProperty("path").GetString()));
        var scope = diagnostics.RootElement.GetProperty("scope");
        Assert.True(scope.GetProperty("sourceOnly").GetBoolean());
        Assert.False(scope.GetProperty("completeProject").GetBoolean());
        Assert.False(scope.GetProperty("projectEvaluation").GetBoolean());
        Assert.True(diagnostics.RootElement.GetProperty("bclReferences").GetInt32() > 0);
    }

    [Fact]
    public async Task NearestProjectBoundaryExcludesNestedAndNeighborSourcesWithoutExecutingProjects()
    {
        await Write("App/App.csproj", "<Project><Target Name=\"Injected\" BeforeTargets=\"Build\"><WriteLinesToFile File=\"executed.txt\" Lines=\"bad\" /></Target></Project>");
        await Write("App/Api.cs", "class Api { public static int Value = 1; }");
        const string use = "class Use { int M() => Api.Value; }";
        await Write("App/Sub/Use.cs", use);
        await Write("App/Nested/Nested.csproj", "<Project />");
        await Write("App/Nested/Duplicate.cs", "class Api { public static int Value = 2; }");
        await Write("Neighbor/Other.csproj", "<Project />");
        await Write("Neighbor/Duplicate.cs", "class Api { public static int Value = 3; }");
        await Write("Duplicate.cs", "class Api { public static int Value = 4; }");
        using var result = await Analyze("definition", "App/Sub/Use.cs", Position(use, "Value"));
        Assert.Equal("App/Api.cs", Assert.Single(Results(result)).GetProperty("path").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("scannedFiles").GetInt32());
        Assert.Equal("App", result.RootElement.GetProperty("scope").GetProperty("directory").GetString());
        using var diagnostics = await Analyze("diagnostics", "App/Sub/Use.cs", (1, 1));
        Assert.DoesNotContain(Results(diagnostics), result => result.GetProperty("code").GetString() == "CS0101");
        Assert.False(File.Exists(Path.Combine(_root, "App", "executed.txt")));
    }

    [Fact]
    public async Task GitIgnoreRulesApplyToCompilationAndExplicitIgnoredTargets()
    {
        await Git("init", "-q");
        await Write(".gitignore", "ignored/\nIgnored.cs\n");
        await Write("Api.cs", "class Api { public static int Value = 1; }");
        await Write("Use.cs", "class Use { int M() => Api.Value; }");
        await Write("ignored/Duplicate.cs", "class Api { public static int Value = 2; }");
        await Write("Ignored.cs", "class Hidden { }");
        using var diagnostics = await Analyze("diagnostics", "Use.cs", (1, 1));
        Assert.Equal("git", diagnostics.RootElement.GetProperty("source").GetString());
        Assert.Equal(2, diagnostics.RootElement.GetProperty("scannedFiles").GetInt32());
        Assert.DoesNotContain(Results(diagnostics), result => result.GetProperty("code").GetString() == "CS0101");
        var ignored = await _service.CSharpSemanticAsync(_root, "diagnostics", "Ignored.cs");
        Assert.True(ignored.IsError);
        Assert.Contains("ignored", ignored.Content);
    }

    [Fact]
    public async Task ReferencePaginationRejectsSameLengthEditsAndAddedSourceFiles()
    {
        const string api = "class C { public static int Value = 1; }";
        await Write("A.cs", api);
        await Write("B.cs", "class D { int M() => C.Value + C.Value; }");
        var point = Position(api, "Value");
        using var first = await Analyze("references", "A.cs", point, limit: 1);
        Assert.Single(Results(first));
        Assert.Equal(1, first.RootElement.GetProperty("nextOffset").GetInt32());
        var snapshot = first.RootElement.GetProperty("snapshot").GetString();
        using var second = await Analyze("references", "A.cs", point, offset: 1, limit: 1, snapshot: snapshot);
        Assert.Single(Results(second));
        Assert.True(second.RootElement.GetProperty("snapshotValidated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextOffset").ValueKind);
        await Write("B.cs", "class D { int M() => C.Value - C.Value; }");
        var stale = await _service.CSharpSemanticAsync(_root, "references", "A.cs", point.Line, point.Column, 1, 1, snapshot);
        Assert.True(stale.IsError);
        Assert.Contains("changed", stale.Content);
        using var fresh = await Analyze("references", "A.cs", point, limit: 1);
        await Write("Extra.cs", "class Extra { }");
        Assert.True((await _service.CSharpSemanticAsync(_root, "references", "A.cs", point.Line, point.Column, 1, 1,
            fresh.RootElement.GetProperty("snapshot").GetString())).IsError);
    }

    [Fact]
    public async Task PositionsAreOneBasedUtf16AndMetadataDefinitionsAreExplicit()
    {
        const string source = "class 示例 { string s = \"😀\"; int 值; int Read() => 值; }";
        await Write("中文.cs", source);
        using var definition = await Analyze("definition", "中文.cs", Position(source, "值;", last: true));
        Assert.Equal(Position(source, "值;").Column, Assert.Single(Results(definition)).GetProperty("column").GetInt32());
        Assert.Equal("one-based UTF-16", definition.RootElement.GetProperty("positionEncoding").GetString());
        using var metadata = await Analyze("definition", "中文.cs", Position(source, "string"));
        Assert.True(metadata.RootElement.GetProperty("symbol").GetProperty("metadataOnly").GetBoolean());
        Assert.Empty(Results(metadata));
        Assert.True((await _service.CSharpSemanticAsync(_root, "definition", "中文.cs", 2, 1)).IsError);
    }

    [Fact]
    public async Task FileByteAndSyntaxBudgetsNeverReturnPartialBinding()
    {
        for (var index = 0; index <= 256; index++) await Write($"Many/{index:D3}.cs", $"class C{index} {{ }}");
        var files = await _service.CSharpSemanticAsync(_root, "diagnostics", "Many/000.cs");
        Assert.True(files.IsError);
        Assert.Contains("source-file budget", files.Content);
        await Write("Large/Large.csproj", "<Project />");
        await Write("Large/C.cs", "//" + new string('x', WorkspaceService.MaxFileBytes));
        var bytes = await _service.CSharpSemanticAsync(_root, "diagnostics", "Large/C.cs");
        Assert.True(bytes.IsError);
        Assert.Contains("byte budget", bytes.Content);
        await Write("Deep/Deep.csproj", "<Project />");
        await Write("Deep/C.cs", "class C { int M() => " + new string('(', 150) + "1" + new string(')', 150) + "; }");
        var depth = await _service.CSharpSemanticAsync(_root, "diagnostics", "Deep/C.cs");
        Assert.True(depth.IsError);
        Assert.Contains("syntax budget", depth.Content);
    }

    [Fact]
    public async Task UnicodeDiagnosticPagesRemainBoundedValidJsonAndContinueWithoutGaps()
    {
        await Write("C.cs", "class C {\n" + string.Join('\n', Enumerable.Range(0, 120).Select(index => $"不存在{new string('中', 90)} 字段{index};")) + "\n}");
        var offset = 0;
        string? snapshot = null;
        HashSet<(string Code, int Line, int Column)> seen = [];
        var responseReduced = false;
        var total = -1;
        while (true)
        {
            using var page = await Analyze("diagnostics", "C.cs", (1, 1), offset, 1000, snapshot);
            var value = page.RootElement;
            snapshot = value.GetProperty("snapshot").GetString();
            total = value.GetProperty("totalResults").GetInt32();
            Assert.True(value.GetRawText().Length <= 80_000);
            responseReduced |= value.GetProperty("responseTruncated").GetBoolean();
            foreach (var result in Results(page)) Assert.True(seen.Add((result.GetProperty("code").GetString()!, result.GetProperty("line").GetInt32(), result.GetProperty("column").GetInt32())));
            if (value.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = value.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset);
            offset = next;
        }
        Assert.True(responseReduced);
        Assert.Equal(total, seen.Count);
        Assert.True(total >= 120);
    }

    [Fact]
    public async Task ToolPreservesPathChecksAndCancellation()
    {
        await Write("C.cs", "class C { }");
        var tool = _service.CreateCSharpSemanticTool();
        Assert.IsAssignableFrom<IWorkspacePathTool>(tool);
        var call = new ToolCall("id", "csharp_semantic", "{\"action\":\"diagnostics\",\"path\":\"C.cs\"}");
        Assert.Equal(["C.cs"], ((IWorkspacePathTool)tool).GetPaths(call, _root));
        Assert.False((await tool.ExecuteAsync(call, new ToolExecutionContext(_root))).IsError);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CSharpSemanticAsync(_root, "diagnostics", "../outside.cs"));
        var escape = call with { ArgumentsJson = "{\"action\":\"diagnostics\",\"path\":\"../outside.cs\"}" };
        Assert.True((await tool.ExecuteAsync(escape, new ToolExecutionContext(_root))).IsError);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.CSharpSemanticAsync(_root, "diagnostics", "C.cs", ct: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(call, new ToolExecutionContext(_root), cancelled.Token));
    }

    private async Task<JsonDocument> Analyze(string action, string path, (int Line, int Column) point, int offset = 0, int limit = 100, string? snapshot = null)
    {
        var result = await _service.CSharpSemanticAsync(_root, action, path, point.Line, point.Column, offset, limit, snapshot);
        Assert.False(result.IsError, result.Content);
        return JsonDocument.Parse(result.Content);
    }

    private static JsonElement[] Results(JsonDocument document) => document.RootElement.GetProperty("results").EnumerateArray().ToArray();
    private static (int Line, int Column) Position(string source, string needle, bool last = false)
    {
        var index = last ? source.LastIndexOf(needle, StringComparison.Ordinal) : source.IndexOf(needle, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var before = source[..index];
        var lastBreak = before.LastIndexOf('\n');
        return (before.Count(character => character == '\n') + 1, index - lastBreak);
    }

    private async Task Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content);
    }

    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
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
        if (!full.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(full).StartsWith("cogitide-semantic-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (!Directory.Exists(full)) return;
        foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, true);
    }
}
