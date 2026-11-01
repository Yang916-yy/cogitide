using System.Text.Json;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

/// <summary>Opt-in acceptance against installed upstream servers, never installation from a test.</summary>
public sealed class LspRealServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-real-lsp-" + Guid.NewGuid().ToString("N"));
    public LspRealServerTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [RealLspFact("COGITIDE_TEST_TS_LSP_COMMAND", "COGITIDE_TEST_TS_LSP_SCRIPT")]
    public async Task TypeScriptResolvesCrossFileDefinitionsReferencesHoverSymbolsAndTypeErrors()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "tsconfig.json"), """{"compilerOptions":{"strict":true,"noEmit":true},"include":["*.ts"]}""");
        await File.WriteAllTextAsync(Path.Combine(_root, "library.ts"), "export function add(a: number, b: number): number { return a + b; }\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "consumer.ts"), "import { add } from './library';\nconst answer = add(1, 2);\nconst broken: number = 'oops';\n");
        // Its documented syntax-server mode intentionally serves partial IntelliSense while projects load.
        // Use the single semantic server so the first query validates cross-file binding, without sleeps/retries.
        var options = JsonSerializer.SerializeToElement(new { disableAutomaticTypingAcquisition = true, tsserver = new { useSyntaxServer = "never" } });
        await using var tools = new LspToolSet(_root, new([new LspServerConfiguration("typescript",
            Environment.GetEnvironmentVariable("COGITIDE_TEST_TS_LSP_COMMAND")!,
            [Environment.GetEnvironmentVariable("COGITIDE_TEST_TS_LSP_SCRIPT")!, "--stdio"],
            ["typescript", "javascript"], TrustedWorkspace: true, InitializationOptions: options, RequestTimeoutSeconds: 60)]));
        await AssertCore(tools, "consumer.ts", "library.ts", 2, 16, 1, 17, "number");
    }

    [RealLspFact("COGITIDE_TEST_PY_LSP_COMMAND", "COGITIDE_TEST_PY_LSP_SCRIPT")]
    public async Task PythonResolvesCrossFileDefinitionsReferencesHoverSymbolsAndTypeErrors()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "pyrightconfig.json"), """{"include":["*.py"],"typeCheckingMode":"basic"}""");
        await File.WriteAllTextAsync(Path.Combine(_root, "library.py"), "def add(a: int, b: int) -> int:\n    return a + b\n");
        await File.WriteAllTextAsync(Path.Combine(_root, "consumer.py"), "from library import add\nanswer = add(1, 2)\nbroken: int = 'oops'\n");
        await using var tools = new LspToolSet(_root, new([new LspServerConfiguration("pyright",
            Environment.GetEnvironmentVariable("COGITIDE_TEST_PY_LSP_COMMAND")!,
            [Environment.GetEnvironmentVariable("COGITIDE_TEST_PY_LSP_SCRIPT")!, "--stdio"],
            ["python"], TrustedWorkspace: true, RequestTimeoutSeconds: 60)]));
        await AssertCore(tools, "consumer.py", "library.py", 2, 10, 1, 6, "int");
    }

    private static async Task AssertCore(LspToolSet tools, string consumer, string library, int useLine, int useColumn, int definitionLine, int definitionColumn, string expectedType)
    {
        var definition = await tools.QueryAsync("definition", consumer, useLine, useColumn);
        Assert.False(definition.IsError, definition.Content);
        using var locations = JsonDocument.Parse(definition.Content);
        Assert.True(locations.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("path").GetString() == library), definition.Content);
        var references = await tools.QueryAsync("references", library, definitionLine, definitionColumn);
        Assert.False(references.IsError, references.Content);
        using var referenceJson = JsonDocument.Parse(references.Content);
        Assert.True(referenceJson.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("path").GetString() == consumer), references.Content);
        var hover = await tools.QueryAsync("hover", consumer, useLine, useColumn);
        Assert.False(hover.IsError, hover.Content);
        using var hoverJson = JsonDocument.Parse(hover.Content);
        Assert.Contains("add", hoverJson.RootElement.GetProperty("results")[0].GetProperty("content").GetString());
        Assert.Contains(expectedType, hoverJson.RootElement.GetProperty("results")[0].GetProperty("content").GetString());
        var symbols = await tools.QueryAsync("symbols", library);
        Assert.False(symbols.IsError, symbols.Content);
        using var symbolJson = JsonDocument.Parse(symbols.Content);
        Assert.True(symbolJson.RootElement.GetProperty("results").EnumerateArray().Any(item => item.GetProperty("name").GetString() == "add"), symbols.Content);
        var diagnostics = await tools.QueryAsync("diagnostics", consumer);
        Assert.False(diagnostics.IsError, diagnostics.Content);
        using var diagnosticJson = JsonDocument.Parse(diagnostics.Content);
        Assert.True(diagnosticJson.RootElement.GetProperty("diagnosticsObserved").GetBoolean(), diagnostics.Content);
        Assert.True(diagnosticJson.RootElement.GetProperty("results").EnumerateArray().Any(item =>
            item.GetProperty("line").GetInt32() == 3 && item.GetProperty("severity").GetInt32() == 1), diagnostics.Content);
    }
}

public sealed class RealLspFactAttribute : FactAttribute
{
    public RealLspFactAttribute(string commandEnvironmentVariable, string scriptEnvironmentVariable)
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(commandEnvironmentVariable)) ||
            !File.Exists(Environment.GetEnvironmentVariable(scriptEnvironmentVariable)))
            Skip = $"Opt-in: provide installed server paths using {commandEnvironmentVariable} and {scriptEnvironmentVariable}.";
    }
}
