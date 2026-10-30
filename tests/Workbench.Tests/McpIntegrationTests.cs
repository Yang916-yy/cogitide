using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

/// <summary>Real SDK transports against small local protocol peers; no model or external server calls.</summary>
public sealed class McpIntegrationTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "cogitide-mcp-" + Guid.NewGuid().ToString("N"));
    public McpIntegrationTests() => Directory.CreateDirectory(_workspace);

    [Fact]
    public async Task MissingConfigurationDoesNotStartAnything()
    {
        await using var tools = await McpToolSet.ConnectAsync(Path.Combine(_workspace, "missing.json"), _workspace);
        Assert.Empty(tools.Tools);
    }

    [Fact]
    public async Task StdioPreservesStructuredErrorsRequiresApprovalAndDoesNotInheritCredentials()
    {
        var unrelatedSecret = "COGITIDE_TEST_SECRET_" + Guid.NewGuid().ToString("N");
        var allowedVariable = "COGITIDE_MCP_ALLOWED_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(unrelatedSecret, "must-not-leak");
        Environment.SetEnvironmentVariable(allowedVariable, "explicit-server-value");
        try
        {
            var config = await StdioConfiguration("normal", [allowedVariable]);
            await using var tools = await McpToolSet.ConnectAsync(config, _workspace);
            var tool = Assert.Single(tools.Tools);
            Assert.True(tool.Definition.RequiresApproval);
            Assert.StartsWith("mcp_fixture__echo_", tool.Definition.Name);
            var result = await Invoke(tool, new { fail = true, inspect = new[] { unrelatedSecret, allowedVariable } });
            Assert.True(result.IsError);
            using var json = JsonDocument.Parse(result.Content);
            var structured = json.RootElement.GetProperty("structuredContent");
            Assert.Equal(JsonValueKind.Null, structured.GetProperty("environment").GetProperty(unrelatedSecret).ValueKind);
            Assert.Equal("explicit-server-value", structured.GetProperty("environment").GetProperty(allowedVariable).GetString());
            Assert.Equal(_workspace, structured.GetProperty("cwd").GetString());
            Assert.Equal("fixture error", json.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
            var image = json.RootElement.GetProperty("content")[1];
            Assert.True(image.GetProperty("dataOmitted").GetBoolean());
            Assert.False(image.TryGetProperty("data", out _));
            Assert.True(image.GetProperty("base64Bytes").GetInt32() > 0);
            var pid = await ReadPid();
            await tools.DisposeAsync();
            await AssertStopped(pid);
        }
        finally
        {
            Environment.SetEnvironmentVariable(unrelatedSecret, null);
            Environment.SetEnvironmentVariable(allowedVariable, null);
        }
    }

    [Fact]
    public async Task NamespacesDoNotAliasSimilarToolNamesAcrossServers()
    {
        var script = await WriteStdioFixture();
        var config = await Configuration(new { servers = new[]
        {
            new { id = "one", transport = "stdio", command = "python", arguments = new[] { "-u", script, "names", "one.pid" } },
            new { id = "two", transport = "stdio", command = "python", arguments = new[] { "-u", script, "names", "two.pid" } }
        } });
        await using var tools = await McpToolSet.ConnectAsync(config, _workspace);
        Assert.Equal(4, tools.Tools.Count);
        Assert.Equal(4, tools.Tools.Select(x => x.Definition.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tools.Tools, x => Assert.InRange(x.Definition.Name.Length, 1, 64));
        Assert.All(tools.Tools, x => Assert.True(x.Definition.RequiresApproval));
    }

    [Fact]
    public async Task CancellationKillsOwnedStdioProcessAndDoesNotReplayTheAction()
    {
        var config = await StdioConfiguration("normal");
        await using var tools = await McpToolSet.ConnectAsync(config, _workspace);
        var tool = Assert.Single(tools.Tools);
        var pid = await ReadPid();
        using var cancellation = new CancellationTokenSource();
        var call = Invoke(tool, new { hang = true }, cancellation.Token);
        await WaitUntil(() => File.Exists(Path.Combine(_workspace, "call-started.txt")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        await AssertStopped(pid);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_workspace, "call-started.txt")));
        var closed = await Invoke(tool, new { });
        Assert.True(closed.IsError);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_workspace, "call-started.txt")));
    }

    [Fact]
    public async Task RunCancellationClosesIdleStdioConnection()
    {
        using var cancellation = new CancellationTokenSource();
        var config = await StdioConfiguration("normal");
        await using var tools = await McpToolSet.ConnectAsync(config, _workspace, ct: cancellation.Token);
        var pid = await ReadPid();
        cancellation.Cancel();
        await AssertStopped(pid);
    }

    [Fact]
    public async Task TimedOutCallReportsUnknownOutcomeAndClosesProcess()
    {
        var config = await StdioConfiguration("normal", callTimeoutSeconds: 1);
        await using var tools = await McpToolSet.ConnectAsync(config, _workspace);
        var pid = await ReadPid();
        var result = await Invoke(Assert.Single(tools.Tools), new { hang = true });
        Assert.True(result.IsError);
        Assert.Contains("unknown", result.Content);
        await AssertStopped(pid);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(_workspace, "call-started.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultBoundsKeepValidJsonAndIndicateOmissions(bool unicode)
    {
        await using var tools = await McpToolSet.ConnectAsync(await StdioConfiguration("normal"), _workspace);
        var result = await Invoke(Assert.Single(tools.Tools), new { large = !unicode, unicode });
        Assert.False(result.IsError);
        Assert.InRange(result.Content.Length, 1, 32_768);
        using var json = JsonDocument.Parse(result.Content);
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(json.RootElement.GetProperty("structuredContent").GetProperty("omitted").GetBoolean());
        Assert.DoesNotContain("A" + new string('A', 10000), result.Content);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("cursor")]
    [InlineData("many-pages")]
    [InlineData("schema")]
    [InlineData("many-tools")]
    public async Task UnsafeOrUnboundedDiscoveryFailsAndCleansUp(string mode)
    {
        var config = await StdioConfiguration(mode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => McpToolSet.ConnectAsync(config, _workspace));
        await AssertStopped(await ReadPid());
    }

    [Theory]
    [InlineData("{\"servers\":[{\"id\":\"fixture\",\"transport\":\"http\",\"url\":\"http://example.com/mcp\"}]}")]
    [InlineData("{\"servers\":[{\"id\":\"fixture\",\"transport\":\"http\",\"url\":\"https://example.com/mcp?key=literal-secret\"}]}")]
    [InlineData("{\"servers\":[{\"id\":\"fixture\",\"transport\":\"http\",\"url\":\"https://user:password@example.com/mcp\"}]}")]
    [InlineData("{\"servers\":[{\"id\":\"fixture\",\"transport\":\"stdio\",\"command\":\"not-started\",\"environmentVariables\":[\"OPENAI_API_KEY\"]}]}")]
    [InlineData("{\"servers\":[{\"id\":\"fixture\",\"transport\":\"http\",\"url\":\"https://example.com/mcp\",\"headerEnvironmentVariables\":{\"Authorization\":\"Bearer literal-secret\"}}]}")]
    public async Task UnsafeConfigurationIsRejectedBeforeConnecting(string json)
    {
        var path = Path.Combine(_workspace, "config.json");
        await File.WriteAllTextAsync(path, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => McpToolSet.ConnectAsync(path, _workspace));
        Assert.False(File.Exists(Path.Combine(_workspace, "fixture.pid")));
    }

    [Fact]
    public async Task ProviderSpecificExcludedVariableCannotBeExplicitlyForwarded()
    {
        var config = await StdioConfiguration("normal", ["CUSTOM_MODEL_CREDENTIAL"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => McpToolSet.ConnectAsync(config, _workspace, ["CUSTOM_MODEL_CREDENTIAL"]));
        Assert.False(File.Exists(Path.Combine(_workspace, "fixture.pid")));
    }

    [Fact]
    public async Task StreamableHttpUsesConfiguredHeaderAndOwnsSessionCleanup()
    {
        var variable = "COGITIDE_MCP_HTTP_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "Bearer local-fixture");
        await using var peer = new HttpPeer();
        try
        {
            var config = await Configuration(new { servers = new[] { new
            {
                id = "remote", transport = "http", url = peer.Url,
                headerEnvironmentVariables = new Dictionary<string, string> { ["Authorization"] = variable }
            } } });
            await using var tools = await McpToolSet.ConnectAsync(config, _workspace);
            var tool = Assert.Single(tools.Tools);
            Assert.True(tool.Definition.RequiresApproval);
            var result = await Invoke(tool, new { value = "http round trip", fail = true });
            Assert.True(result.IsError);
            using var json = JsonDocument.Parse(result.Content);
            Assert.Equal("http round trip", json.RootElement.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.All(peer.Authorizations, x => Assert.Equal("Bearer local-fixture", x));
            await tools.DisposeAsync();
            Assert.Equal(1, peer.DeleteCount);
            Assert.Equal(1, peer.CallCount);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task HttpRedirectIsNotFollowedWithAuthenticationHeaders()
    {
        await using var destination = new HttpPeer();
        await using var redirect = new HttpPeer(destination.Url);
        var config = await Configuration(new { servers = new[] { new { id = "remote", transport = "http", url = redirect.Url } } });
        await Assert.ThrowsAsync<InvalidOperationException>(() => McpToolSet.ConnectAsync(config, _workspace));
        Assert.Empty(destination.Authorizations);
    }

    private Task<ToolResult> Invoke(IAgentTool tool, object arguments, CancellationToken ct = default) =>
        tool.ExecuteAsync(new ToolCall("fixture-call", tool.Definition.Name, JsonSerializer.Serialize(arguments)), new(_workspace), ct);

    private async Task<string> Configuration(object configuration)
    {
        var path = Path.Combine(_workspace, "config.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(configuration));
        return path;
    }

    private async Task<string> StdioConfiguration(string mode, string[]? environment = null, int callTimeoutSeconds = 60)
    {
        var script = await WriteStdioFixture();
        return await Configuration(new { servers = new[] { new { id = "fixture", transport = "stdio", command = "python",
            arguments = new[] { "-u", script, mode, "fixture.pid" }, environmentVariables = environment, callTimeoutSeconds } } });
    }

    private async Task<string> WriteStdioFixture()
    {
        var path = Path.Combine(_workspace, "fixture.py");
        await File.WriteAllTextAsync(path, StdioFixture, new UTF8Encoding(false));
        return path;
    }

    private async Task<int> ReadPid()
    {
        var path = Path.Combine(_workspace, "fixture.pid");
        await WaitUntil(() => File.Exists(path) && new FileInfo(path).Length > 0);
        return int.Parse(await File.ReadAllTextAsync(path));
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static Task AssertStopped(int pid) => WaitUntil(() => !IsRunning(pid));
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(25);
        Assert.True(condition(), "Fixture did not reach the expected state within 15 seconds.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, true);
    }

    // A test-only MCP peer, deliberately small and independent of production framing/parsing.
    // The production implementation uses the official SDK for all protocol handling.
    private const string StdioFixture = """
        import json, os, sys, time
        sys.stdin.reconfigure(encoding='utf-8')
        sys.stdout.reconfigure(encoding='utf-8')
        mode = sys.argv[1]
        with open(sys.argv[2], 'w', encoding='utf-8') as marker:
            marker.write(str(os.getpid()))
        def tool(name='echo', schema=None):
            return {'name':name,'description':'Local deterministic fixture',
                    'inputSchema':schema or {'type':'object','properties':{}},
                    'annotations':{'readOnlyHint':True,'destructiveHint':False}}
        for line in sys.stdin:
            request = json.loads(line)
            if 'id' not in request:
                continue
            method, params = request['method'], request.get('params', {})
            if method == 'server/discover':
                response = {'error':{'code':-32601,'message':'Fixture uses initialize'}}
            elif method == 'initialize':
                response = {'result':{'protocolVersion':'2025-11-25','capabilities':{'tools':{}},
                                      'serverInfo':{'name':'local-fixture','version':'1'}}}
            elif method == 'tools/list':
                items = [tool()]
                if mode == 'duplicate': items = [tool(), tool()]
                if mode == 'names': items = [tool('a.b'), tool('a_b')]
                if mode == 'schema': items = [tool(schema={'type':'object','description':'x'*40000})]
                if mode == 'many-tools': items = [tool('echo'+str(i)) for i in range(65)]
                result = {'tools':items}
                if mode == 'cursor': result['nextCursor'] = 'same-cursor'
                if mode == 'many-pages': result = {'tools':[], 'nextCursor':str(int(params.get('cursor','0'))+1)}
                response = {'result':result}
            elif method == 'tools/call':
                args = params.get('arguments', {})
                with open('call-started.txt','a',encoding='utf-8') as marker: marker.write('once\n')
                if args.get('hang'): time.sleep(120)
                structured = {'environment':{k:os.getenv(k) for k in args.get('inspect',[])},'cwd':os.getcwd()}
                text = 'fixture error' if args.get('fail') else 'fixture success'
                if args.get('large'): structured = {'large':'A'*100000}; text = 'A'*100000
                if args.get('unicode'): structured = {'unicode':'中'*7000}
                response = {'result':{'isError':bool(args.get('fail')),'structuredContent':structured,
                    'content':[{'type':'text','text':text},{'type':'image','mimeType':'image/png','data':'eA=='}]}}
            else:
                response = {'result':{}}
            response.update({'jsonrpc':'2.0','id':request['id']})
            print(json.dumps(response, ensure_ascii=False), flush=True)
        """;

    private sealed class HttpPeer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly string? _redirect;
        public string Url { get; }
        public List<string?> Authorizations { get; } = [];
        public int DeleteCount { get; private set; }
        public int CallCount { get; private set; }

        public HttpPeer(string? redirect = null)
        {
            _redirect = redirect;
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            Url = $"http://127.0.0.1:{port}/mcp";
            _loop = Loop();
        }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
                try
                {
                    Authorizations.Add(context.Request.Headers["Authorization"]);
                    if (_redirect is not null)
                    {
                        context.Response.Redirect(_redirect);
                        context.Response.Close();
                        continue;
                    }
                    if (context.Request.HttpMethod == "DELETE")
                    {
                        DeleteCount++;
                        context.Response.StatusCode = 204;
                        context.Response.Close();
                        continue;
                    }
                    if (context.Request.HttpMethod != "POST")
                    {
                        context.Response.StatusCode = 405;
                        context.Response.Close();
                        continue;
                    }
                    using var request = await JsonDocument.ParseAsync(context.Request.InputStream);
                    var root = request.RootElement;
                    if (!root.TryGetProperty("id", out var id))
                    {
                        context.Response.StatusCode = 202;
                        context.Response.Close();
                        continue;
                    }
                    var method = root.GetProperty("method").GetString();
                    object? result;
                    object? error = null;
                    switch (method)
                    {
                        case "server/discover":
                            result = null;
                            error = new { code = -32601, message = "Fixture uses initialize" };
                            break;
                        case "initialize":
                            context.Response.Headers.Add("Mcp-Session-Id", "fixture-session");
                            result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } },
                                serverInfo = new { name = "http-fixture", version = "1" } };
                            break;
                        case "tools/list":
                            result = new { tools = new[] { new { name = "echo", description = "HTTP fixture", inputSchema = new { type = "object", properties = new { } },
                                annotations = new { readOnlyHint = true } } } };
                            break;
                        case "tools/call":
                            CallCount++;
                            var args = root.GetProperty("params").GetProperty("arguments");
                            result = new { isError = args.TryGetProperty("fail", out var fail) && fail.GetBoolean(),
                                structuredContent = args.Clone(), content = new[] { new { type = "text", text = "http response" } } };
                            break;
                        default: result = new { }; break;
                    }
                    var envelope = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id.Clone() };
                    envelope[error is null ? "result" : "error"] = error ?? result;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch
                {
                    context.Response.Abort();
                    throw;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Close();
            await _loop;
        }
    }
}
