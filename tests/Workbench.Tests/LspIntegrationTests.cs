using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

/// <summary>Real stdio processes against a deterministic LSP peer; never a model or paid service.</summary>
public sealed class LspIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-lsp-" + Guid.NewGuid().ToString("N"));
    private const string Source = "let face = \"😀\"; function add() { return 1; }\nadd();\n";
    public LspIntegrationTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task MissingConfigurationAndUntrustedConfigurationCannotLaunch()
    {
        await using var missing = await LspToolSet.LoadAsync(Path.Combine(_root, "absent.json"), _root);
        Assert.Empty(missing.Tools);
        var config = await Configuration();
        Assert.Throws<InvalidDataException>(() => new LspToolSet(_root, new([config with { TrustedWorkspace = false }])));
        Assert.False(File.Exists(Path.Combine(_root, "server.pid")));
        await using var loaded = new LspToolSet(_root, new([config]));
        var tool = Assert.Single(WorkspaceTools.Create(new(), lsp: loaded), t => t.Definition.Name == "lsp_query");
        Assert.True(tool.Definition.RequiresApproval);
        Assert.False(File.Exists(Path.Combine(_root, "server.pid")));
        var denied = await tool.ExecuteAsync(new("1", "lsp_query", """{"action":"definition","path":"../outside.ts"}"""), new(_root));
        Assert.True(denied.IsError);
        Assert.False(File.Exists(Path.Combine(_root, "server.pid")));
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-8")]
    [InlineData("utf-32")]
    public async Task NegotiatedPositionsDefinitionsReferencesSymbolsAndDiagnostics(string encoding)
    {
        var configuration = await Configuration(encoding);
        await using var tools = new LspToolSet(_root, new([configuration]));
        var definition = await tools.QueryAsync("definition", "source.ts", 2, 2);
        Assert.False(definition.IsError, definition.Content);
        using var json = JsonDocument.Parse(definition.Content);
        var location = Assert.Single(json.RootElement.GetProperty("results").EnumerateArray());
        Assert.Equal("source.ts", location.GetProperty("path").GetString());
        Assert.Equal(Source.IndexOf("add", StringComparison.Ordinal) + 1, location.GetProperty("column").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("omittedExternalOrInvalidLocations").GetInt32());
        var hover = await tools.QueryAsync("hover", "source.ts", 1, Source.IndexOf("add", StringComparison.Ordinal) + 1);
        using var hoverJson = JsonDocument.Parse(hover.Content);
        var expected = LspPositions.FromUtf16(Source.Split('\n')[0], Source.IndexOf("add", StringComparison.Ordinal), encoding);
        Assert.Contains($"position={expected}", hoverJson.RootElement.GetProperty("results")[0].GetProperty("content").GetString());
        var references = await tools.QueryAsync("references", "source.ts", 1, 1, limit: 1);
        using var refs = JsonDocument.Parse(references.Content);
        Assert.Equal(1, refs.RootElement.GetProperty("nextOffset").GetInt32());
        var next = await tools.QueryAsync("references", "source.ts", 1, 1, offset: 1, limit: 1,
            expectedSnapshot: refs.RootElement.GetProperty("snapshot").GetString());
        Assert.False(next.IsError, next.Content);
        var symbols = await tools.QueryAsync("symbols", "source.ts");
        using var symbolJson = JsonDocument.Parse(symbols.Content);
        Assert.Equal("add", symbolJson.RootElement.GetProperty("results")[0].GetProperty("name").GetString());
        var diagnostics = await tools.QueryAsync("diagnostics", "source.ts");
        using var diagnosticJson = JsonDocument.Parse(diagnostics.Content);
        Assert.True(diagnosticJson.RootElement.GetProperty("diagnosticsObserved").GetBoolean());
        Assert.True(diagnosticJson.RootElement.GetProperty("diagnosticVersionVerified").GetBoolean());
        Assert.Equal("fixture diagnostic", diagnosticJson.RootElement.GetProperty("results")[0].GetProperty("message").GetString());
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "server.pid")));
        await tools.DisposeAsync();
        await AssertStopped(pid);
    }

    [Fact]
    public async Task WindowsEncodedDriveUrisResolveLocationsAndVersionedDiagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;
        await using var tools = new LspToolSet(_root, new([await Configuration(mode: "encoded-uri")]));
        var definition = await tools.QueryAsync("definition", "source.ts", 2, 2);
        Assert.False(definition.IsError, definition.Content);
        using var json = JsonDocument.Parse(definition.Content);
        Assert.Equal("source.ts", Assert.Single(json.RootElement.GetProperty("results").EnumerateArray()).GetProperty("path").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("omittedExternalOrInvalidLocations").GetInt32());
        var diagnostics = await tools.QueryAsync("diagnostics", "source.ts");
        using var diagnosticJson = JsonDocument.Parse(diagnostics.Content);
        Assert.True(diagnosticJson.RootElement.GetProperty("diagnosticsObserved").GetBoolean(), diagnostics.Content);
        Assert.True(diagnosticJson.RootElement.GetProperty("diagnosticVersionVerified").GetBoolean(), diagnostics.Content);
        Assert.Equal("fixture diagnostic", Assert.Single(diagnosticJson.RootElement.GetProperty("results").EnumerateArray()).GetProperty("message").GetString());
    }

    [Fact]
    public async Task SameLengthSourceEditsAndInventoryChangesInvalidatePages()
    {
        await using var tools = new LspToolSet(_root, new([await Configuration()]));
        var first = await tools.QueryAsync("references", "source.ts", limit: 1);
        using var page = JsonDocument.Parse(first.Content);
        var snapshot = page.RootElement.GetProperty("snapshot").GetString();
        await File.WriteAllTextAsync(Path.Combine(_root, "source.ts"), Source.Replace("face", "same", StringComparison.Ordinal));
        var stale = await tools.QueryAsync("references", "source.ts", offset: 1, expectedSnapshot: snapshot);
        Assert.True(stale.IsError);
        Assert.Contains("snapshot changed", stale.Content);
        var fresh = await tools.QueryAsync("references", "source.ts", limit: 1);
        using var freshPage = JsonDocument.Parse(fresh.Content);
        await File.WriteAllTextAsync(Path.Combine(_root, "added.ts"), "export const added = 1;\n");
        var inventoryStale = await tools.QueryAsync("references", "source.ts", offset: 1,
            expectedSnapshot: freshPage.RootElement.GetProperty("snapshot").GetString());
        Assert.True(inventoryStale.IsError);
    }

    [Fact]
    public async Task ExplicitEnvironmentAndRunCancellationPreserveOwnership()
    {
        var secret = "LSP_SECRET_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(secret, "do-not-inherit");
        Environment.SetEnvironmentVariable("COGITIDE_LSP_FIXTURE_ALLOWED", "configured");
        try
        {
            using var lifetime = new CancellationTokenSource();
            var configuration = (await Configuration()) with { EnvironmentVariables = ["COGITIDE_LSP_FIXTURE_ALLOWED"] };
            await using var tools = new LspToolSet(_root, new([configuration]), lifetime: lifetime.Token);
            var hover = await tools.QueryAsync("hover", "source.ts");
            using var json = JsonDocument.Parse(hover.Content);
            Assert.Contains("allowed=configured", json.RootElement.GetProperty("results")[0].GetProperty("content").GetString());
            Assert.DoesNotContain("do-not-inherit", hover.Content);
            var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "server.pid")));
            lifetime.Cancel();
            await AssertStopped(pid);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secret, null);
            Environment.SetEnvironmentVariable("COGITIDE_LSP_FIXTURE_ALLOWED", null);
        }
    }

    [Fact]
    public async Task RequestCancellationStopsProcessAndNeverReplays()
    {
        await using var tools = new LspToolSet(_root, new([await Configuration(mode: "hang")]));
        using var cancellation = new CancellationTokenSource();
        var request = tools.QueryAsync("hover", "source.ts", ct: cancellation.Token);
        await WaitFor(() => File.Exists(Path.Combine(_root, "request.log")));
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "server.pid")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
        await AssertStopped(pid);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_root, "request.log")));
    }

    [Fact]
    public async Task PullDiagnosticsAndUnsupportedCapabilitiesRemainExplicit()
    {
        await using var tools = new LspToolSet(_root, new([await Configuration(mode: "pull")]));
        var diagnostics = await tools.QueryAsync("diagnostics", "source.ts");
        using var json = JsonDocument.Parse(diagnostics.Content);
        Assert.True(json.RootElement.GetProperty("diagnosticsObserved").GetBoolean());
        Assert.Equal("pull diagnostic", json.RootElement.GetProperty("results")[0].GetProperty("message").GetString());
        await using var unsupported = new LspToolSet(_root, new([await Configuration(mode: "unsupported")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unsupported.QueryAsync("definition", "source.ts"));
    }

    [Fact]
    public async Task StaleDiagnosticsDoNotBecomeAnObservedEmptySuccess()
    {
        await using var tools = new LspToolSet(_root, new([await Configuration(mode: "stale-only")]));
        var diagnostics = await tools.QueryAsync("diagnostics", "source.ts");
        Assert.False(diagnostics.IsError, diagnostics.Content);
        using var json = JsonDocument.Parse(diagnostics.Content);
        Assert.False(json.RootElement.GetProperty("diagnosticsObserved").GetBoolean());
        Assert.False(json.RootElement.GetProperty("diagnosticVersionVerified").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public async Task RequestTimeoutStopsTheOwnedServerWithoutReplay()
    {
        var configuration = (await Configuration(mode: "hang")) with { RequestTimeoutSeconds = 1 };
        await using var tools = new LspToolSet(_root, new([configuration]));
        var result = await tools.QueryAsync("hover", "source.ts").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsError);
        Assert.Contains("no request replayed", result.Content);
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "server.pid")));
        await AssertStopped(pid);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_root, "request.log")));
    }

    [Fact]
    public async Task UncooperativeShutdownCannotPreventOwnedProcessCleanup()
    {
        await using var tools = new LspToolSet(_root, new([await Configuration(mode: "ignore-shutdown")]));
        var result = await tools.QueryAsync("hover", "source.ts");
        Assert.False(result.IsError, result.Content);
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "server.pid")));
        await tools.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await AssertStopped(pid);
    }

    [Fact]
    public async Task ServerRequestedEditsCannotBypassWorkspaceEditingTools()
    {
        await using var tools = new LspToolSet(_root, new([await Configuration(mode: "edit")]));
        var result = await tools.QueryAsync("hover", "source.ts");
        Assert.False(result.IsError, result.Content);
        await WaitFor(() => File.Exists(Path.Combine(_root, "apply-edit-result.log")));
        using var response = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "apply-edit-result.log")));
        Assert.False(response.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(Source, await File.ReadAllTextAsync(Path.Combine(_root, "source.ts")));
    }

    [Theory]
    [InlineData("utf-8", 3, 5)]
    [InlineData("utf-16", 3, 3)]
    [InlineData("utf-32", 3, 2)]
    public void PositionsRoundTripAndRejectSplitUnicode(string encoding, int utf16, int encoded)
    {
        Assert.Equal(encoded, LspPositions.FromUtf16("a😀z", utf16, encoding));
        Assert.Equal(utf16, LspPositions.ToUtf16("a😀z", encoded, encoding));
        Assert.Throws<ArgumentOutOfRangeException>(() => LspPositions.FromUtf16("a😀z", 2, encoding));
        if (encoding == "utf-8") Assert.Throws<ArgumentException>(() => LspPositions.ToUtf16("a😀z", 2, encoding));
    }

    private async Task<LspServerConfiguration> Configuration(string encoding = "utf-16", string mode = "normal")
    {
        var script = Path.Combine(_root, "fixture-server.py");
        await File.WriteAllTextAsync(script, Fixture, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(_root, "source.ts"), Source, new UTF8Encoding(false));
        var start = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("import sys; print(sys.executable)");
        using var probe = Process.Start(start)!;
        var python = (await probe.StandardOutput.ReadToEndAsync()).Trim();
        await probe.WaitForExitAsync();
        Assert.Equal(0, probe.ExitCode);
        return new("fixture", python, ["-u", script, encoding, mode], ["typescript"], TrustedWorkspace: true);
    }
    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private static async Task AssertStopped(int pid)
    {
        await WaitFor(() => { try { using var p = Process.GetProcessById(pid); return p.HasExited; } catch (ArgumentException) { return true; } });
    }

    // Protocol framing here belongs to an intentionally minimal TEST PEER. Production uses StreamJsonRpc.
    private const string Fixture = """
import sys, os, json, pathlib
encoding, mode = sys.argv[1:3]
pathlib.Path('server.pid').write_text(str(os.getpid()))
documents = {}
def send(message):
    body = json.dumps(message, ensure_ascii=False).encode('utf-8')
    sys.stdout.buffer.write(('Content-Length: %d\r\n\r\n' % len(body)).encode('ascii') + body)
    sys.stdout.buffer.flush()
def reply(ident, result):
    send({'jsonrpc':'2.0','id':ident,'result':result})
def units(text):
    if encoding == 'utf-8': return len(text.encode('utf-8'))
    if encoding == 'utf-32': return len(text)
    return len(text.encode('utf-16-le')) // 2
def external_uri(uri):
    if mode == 'encoded-uri' and uri.startswith('file:///') and len(uri) > 10 and uri[9] == ':':
        return uri[:8] + uri[8].lower() + '%3A' + uri[10:]
    return uri
while True:
    headers = {}
    while True:
        line = sys.stdin.buffer.readline()
        if not line: sys.exit(0)
        if line in (b'\r\n', b'\n'): break
        key, value = line.decode('ascii').split(':', 1)
        headers[key.lower()] = value.strip()
    msg = json.loads(sys.stdin.buffer.read(int(headers['content-length'])))
    method, p = msg.get('method'), msg.get('params', {})
    if method == 'initialize':
        capabilities = {'positionEncoding':encoding, 'definitionProvider':True, 'referencesProvider':True,
                        'hoverProvider':True, 'documentSymbolProvider':True, 'textDocumentSync':1}
        if mode == 'unsupported': capabilities['definitionProvider'] = False
        if mode == 'pull': capabilities['diagnosticProvider'] = {'interFileDependencies':False, 'workspaceDiagnostics':False}
        reply(msg['id'], {'capabilities':capabilities})
    elif method in ('textDocument/didOpen', 'textDocument/didChange'):
        doc = p['textDocument']; uri = doc['uri']
        text = doc.get('text', p.get('contentChanges',[{}])[0].get('text',''))
        documents[uri] = text
        send({'jsonrpc':'2.0','method':'textDocument/publishDiagnostics','params':
              {'uri':external_uri(uri),'version':doc['version']-1,'diagnostics':[{'range':{'start':{'line':0,'character':0},'end':{'line':0,'character':1}},'message':'stale diagnostic'}]}})
        if mode != 'stale-only':
            send({'jsonrpc':'2.0','method':'textDocument/publishDiagnostics','params':
                  {'uri':external_uri(uri),'version':doc['version'],'diagnostics':[{'range':{'start':{'line':0,'character':0},'end':{'line':0,'character':1}},'severity':1,'message':'fixture diagnostic'}]}})
    elif method == 'textDocument/definition':
        uri = p['textDocument']['uri']; first = documents[uri].splitlines()[0]
        column = units(first[:first.index('add')])
        span = {'start':{'line':0,'character':column},'end':{'line':0,'character':column+3}}
        reply(msg['id'], [{'targetUri':external_uri(uri),'targetRange':span,'targetSelectionRange':span},
                          {'uri':'file:///outside-secret.ts','range':span}])
    elif method == 'textDocument/references':
        uri = p['textDocument']['uri']; span = {'start':{'line':1,'character':0},'end':{'line':1,'character':3}}
        reply(msg['id'], [{'uri':uri,'range':span}, {'uri':uri,'range':span}])
    elif method == 'textDocument/hover':
        if mode == 'hang':
            with open('request.log','a') as log: log.write('hover\n')
            continue
        if mode == 'edit':
            send({'jsonrpc':'2.0','id':7000,'method':'workspace/applyEdit','params':{'edit':{'changes':{p['textDocument']['uri']:[{'range':{'start':{'line':0,'character':0},'end':{'line':0,'character':0}},'newText':'MUST NOT WRITE'}]}}}})
        reply(msg['id'], {'contents':{'kind':'plaintext','value':'position=%d allowed=%s' % (p['position']['character'],os.getenv('COGITIDE_LSP_FIXTURE_ALLOWED',''))}})
    elif method == 'textDocument/documentSymbol':
        span = {'start':{'line':1,'character':0},'end':{'line':1,'character':3}}
        reply(msg['id'], [{'name':'add','kind':12,'range':span,'selectionRange':span}])
    elif method == 'textDocument/diagnostic':
        span = {'start':{'line':0,'character':0},'end':{'line':0,'character':1}}
        reply(msg['id'], {'kind':'full','items':[{'range':span,'message':'pull diagnostic'}]})
    elif method == 'shutdown':
        if mode != 'ignore-shutdown': reply(msg['id'], None)
    elif method == 'exit': sys.exit(0)
    elif method is None and msg.get('id') == 7000:
        pathlib.Path('apply-edit-result.log').write_text(json.dumps(msg.get('result', {})))
""";
}
