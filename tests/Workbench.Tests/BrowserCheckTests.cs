using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class BrowserCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "workbench-browser-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("{\"url\":\"file:///etc/passwd\"}")]
    [InlineData("{\"url\":\"https://user:password@example.com\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"allowedOrigins\":[\"https://other.example/path\"]}")]
    [InlineData("{\"url\":\"https://example.com\",\"allowedOrigins\":[\"https://*.example.com\"]}")]
    [InlineData("{\"url\":\"https://example.com\",\"timeoutSeconds\":61}")]
    [InlineData("{\"url\":\"https://example.com\",\"timeoutSeconds\":true}")]
    [InlineData("{\"url\":\"https://example.com\",\"captureScreenshot\":\"yes\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"screenshotPath\":\"../escape.png\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"url\":\"https://other.example\"}")]
    [InlineData("{\"url\":\"https://example.com\",\"assertions\":[{\"type\":\"status\",\"value\":\"200\"}]}")]
    [InlineData("{\"url\":\"https://example.com\",\"assertions\":[{\"type\":\"script\",\"value\":\"alert(1)\"}]}")]
    [InlineData("{\"url\":\"https://example.com\",\"assertions\":[{\"type\":\"selector\",\"value\":\"body >> text=hello\"}]}")]
    public async Task RequiresApprovalAndRejectsInvalidRequestsWithoutStartingBrowser(string arguments)
    {
        var tool = new BrowserCheckTool(_root);
        Assert.True(tool.Definition.RequiresApproval);
        Assert.Equal("browser_check", tool.Definition.Name);
        var result = await tool.ExecuteAsync(new("invalid", tool.Definition.Name, arguments), new(_root));
        Assert.True(result.IsError);
        using var output = JsonDocument.Parse(result.Content);
        Assert.Equal("invalid_request", output.RootElement.GetProperty("outcome").GetString());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task HeadlessChecksUnicodeAssertionsAndWritesOnlyAnOwnedPngArtifact()
    {
        await using var server = new FixtureServer(_ => new(200, """
            <!doctype html><html><head><title>Acceptance 验收</title></head>
            <body><h1 id="ready">Ready 就绪 مرحبا</h1><script>console.log('fixture log');</script></body></html>
            """));
        var result = await Run(server.Url, new object[]
        {
            new { type = "text", value = "就绪 مرحبا" }, new { type = "selector", value = "#ready" },
            new { type = "title", value = "Acceptance 验收" }, new { type = "status", value = 200 }
        }, screenshot: true);
        Assert.False(result.IsError, result.Content);
        using var output = JsonDocument.Parse(result.Content);
        var evidence = output.RootElement;
        Assert.Equal("passed", evidence.GetProperty("outcome").GetString());
        Assert.Equal(4, evidence.GetProperty("assertions").GetArrayLength());
        Assert.All(evidence.GetProperty("assertions").EnumerateArray(), item => Assert.True(item.GetProperty("passed").GetBoolean()));
        Assert.Contains(evidence.GetProperty("console").EnumerateArray(), item => item.GetProperty("text").GetString() == "fixture log");
        Assert.NotNull(result.ArtifactPath);
        var full = Path.GetFullPath(result.ArtifactPath);
        Assert.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Assert.Equal("viewport.png", Path.GetFileName(full));
        var png = await File.ReadAllBytesAsync(full);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
    }

    [Fact]
    public async Task ReportsConsolePageHttpAndNetworkFailuresAsFailedAcceptance()
    {
        await using var server = new FixtureServer(path => path == "/missing.js"
            ? new(404, "missing", "text/javascript")
            : new(200, """
                <!doctype html><html><body>Fixture<script>
                console.error('fixture console error');
                fetch('/broken').catch(() => {});
                throw new Error('fixture page error');
                </script><script src="/missing.js"></script></body></html>
                """), brokenPath: "/broken");
        var result = await Run(server.Url);
        Assert.True(result.IsError);
        using var output = JsonDocument.Parse(result.Content);
        var evidence = output.RootElement;
        Assert.Equal("failed", evidence.GetProperty("outcome").GetString());
        Assert.Contains(evidence.GetProperty("console").EnumerateArray(), item => item.GetProperty("text").GetString() == "fixture console error");
        Assert.Contains(evidence.GetProperty("pageErrors").EnumerateArray(), item => item.GetString()!.Contains("fixture page error", StringComparison.Ordinal));
        Assert.Contains(evidence.GetProperty("httpErrors").EnumerateArray(), item => item.GetProperty("status").GetInt32() == 404);
        Assert.Contains(evidence.GetProperty("networkFailures").EnumerateArray(), item => item.GetProperty("url").GetString()!.EndsWith("/broken", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnapprovedSubresourceAndWebSocketNeverReachTheirServers()
    {
        await using var unapproved = new FixtureServer(_ => new(200, "unexpected", "text/javascript"));
        await using var server = new FixtureServer(_ => new(200, $$"""
            <!doctype html><html><body>Origins<script src="{{unapproved.Url}}leak.js"></script>
            <script>new WebSocket('{{unapproved.Url.Replace("http://", "ws://", StringComparison.Ordinal)}}socket');</script></body></html>
            """));
        var result = await Run(server.Url);
        Assert.True(result.IsError);
        using var output = JsonDocument.Parse(result.Content);
        var blocked = output.RootElement.GetProperty("blockedRequests").EnumerateArray().ToArray();
        Assert.Contains(blocked, item => item.GetProperty("url").GetString()!.EndsWith("/leak.js", StringComparison.Ordinal));
        Assert.Contains(blocked, item => item.GetProperty("url").GetString()!.EndsWith("/socket", StringComparison.Ordinal));
        Assert.Empty(unapproved.Requests);
    }

    [Fact]
    public async Task ExactAdditionalOriginLoadsItsSubresource()
    {
        await using var additional = new FixtureServer(_ => new(200, "console.log('approved subresource');", "text/javascript"));
        await using var server = new FixtureServer(_ => new(200, $"<!doctype html><body>Ready<script src=\"{additional.Url}asset.js\"></script></body>"));
        var result = await Run(server.Url, allowedOrigins: [additional.Url]);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("/asset.js", additional.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RedirectsAreNeverFollowedEvenWhenTheFirstDestinationIsApproved(bool approveFirstHop)
    {
        await using var forbidden = new FixtureServer(_ => new(200, "must never be reached"));
        await using var hop = new FixtureServer(_ => new(302, "", Location: forbidden.Url));
        await using var origin = new FixtureServer(_ => new(302, "", Location: hop.Url));
        var result = await Run(origin.Url, allowedOrigins: approveFirstHop ? [hop.Url] : []);
        Assert.True(result.IsError);
        using var output = JsonDocument.Parse(result.Content);
        Assert.Contains(output.RootElement.GetProperty("redirects").EnumerateArray(), item => item.GetProperty("destination").GetString() == hop.Url);
        Assert.Empty(hop.Requests);
        Assert.Empty(forbidden.Requests);
    }

    [Fact]
    public async Task Http404AndFailedStatusAssertionCannotBeSuccessful()
    {
        await using var server = new FixtureServer(_ => new(404, "<!doctype html><body>Missing</body>"));
        var result = await Run(server.Url, [new { type = "status", value = 200 }], screenshot: true);
        Assert.True(result.IsError);
        using var output = JsonDocument.Parse(result.Content);
        Assert.Equal(404, output.RootElement.GetProperty("statusCode").GetInt32());
        Assert.False(output.RootElement.GetProperty("assertions")[0].GetProperty("passed").GetBoolean());
        Assert.True(File.Exists(result.ArtifactPath));
    }

    [Fact]
    public async Task FreshContextsDoNotReuseCookiesOrLocalStorage()
    {
        await using var server = new FixtureServer(_ => new(200, """
            <!doctype html><html><body><script>
            document.body.textContent = (document.cookie || localStorage.getItem('fixture')) ? 'DIRTY' : 'CLEAN';
            document.cookie = 'fixture=private'; localStorage.setItem('fixture', 'private');
            </script></body></html>
            """));
        var tool = new BrowserCheckTool(_root);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await tool.ExecuteAsync(Call(server.Url, [new { type = "text", value = "CLEAN" }]), new(_root));
            Assert.False(result.IsError, result.Content);
        }
    }

    [Fact]
    public async Task CancellationClosesTheOwnedBrowserAndAllowsTheNextCheck()
    {
        await using var server = new FixtureServer(_ => new(200, "<!doctype html><body>Ready</body>"), stalledPath: "/stall");
        var tool = new BrowserCheckTool(_root);
        using var cancellation = new CancellationTokenSource();
        var running = tool.ExecuteAsync(Call(server.Url + "stall"), new(_root), cancellation.Token);
        await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(12)));
        Assert.False(Directory.Exists(_root));
        var next = await tool.ExecuteAsync(Call(server.Url, [new { type = "status", value = 200 }]), new(_root));
        Assert.False(next.IsError, next.Content);
    }

    [Fact]
    public async Task TotalBudgetDoesNotMarkUnobservedAssertionsSuccessful()
    {
        await using var server = new FixtureServer(_ => new(200, "<!doctype html><body>Ready</body>"));
        var tool = new BrowserCheckTool(_root);
        var watch = Stopwatch.StartNew();
        var result = await tool.ExecuteAsync(Call(server.Url, [new { type = "selector", value = "#never-created" }], timeout: 3), new(_root));
        Assert.True(result.IsError);
        using var output = JsonDocument.Parse(result.Content);
        Assert.Equal("timed_out", output.RootElement.GetProperty("outcome").GetString());
        Assert.DoesNotContain(output.RootElement.GetProperty("assertions").EnumerateArray(), item => item.GetProperty("passed").GetBoolean());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(16));
    }

    private Task<ToolResult> Run(string url, object[]? assertions = null, bool screenshot = false, string[]? allowedOrigins = null) =>
        new BrowserCheckTool(_root).ExecuteAsync(Call(url, assertions, screenshot, allowedOrigins), new(_root));
    private static ToolCall Call(string url, object[]? assertions = null, bool screenshot = false, string[]? allowedOrigins = null, int timeout = 20) =>
        new(Guid.NewGuid().ToString("N"), BrowserCheckTool.Name, JsonSerializer.Serialize(new
        { url, assertions = assertions ?? [], captureScreenshot = screenshot, allowedOrigins = allowedOrigins ?? [], timeoutSeconds = timeout }, Wire.Json));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record FixtureResponse(int Status, string Body, string ContentType = "text/html; charset=utf-8", string? Location = null);

    // BCL HTTP fixture only: production navigation and transport are entirely the official Playwright SDK.
    private sealed class FixtureServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _responses = [];
        public ConcurrentQueue<string> Requests { get; } = new();
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Url { get; }

        public FixtureServer(Func<string, FixtureResponse> response, string? brokenPath = null, string? stalledPath = null)
        {
            using var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/";
            port.Stop();
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _accept = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                        _responses.Add(Respond(context));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
            });

            async Task Respond(HttpListenerContext context)
            {
                try
                {
                    var path = context.Request.Url!.AbsolutePath;
                    Requests.Enqueue(path);
                    if (path == stalledPath)
                    {
                        Stalled.TrySetResult();
                        await Task.Delay(Timeout.Infinite, _stop.Token);
                    }
                    if (path == brokenPath) { context.Response.Abort(); return; }
                    var result = response(path);
                    var bytes = Encoding.UTF8.GetBytes(result.Body);
                    context.Response.StatusCode = result.Status;
                    context.Response.ContentType = result.ContentType;
                    context.Response.ContentLength64 = bytes.Length;
                    if (result.Location is not null) context.Response.RedirectLocation = result.Location;
                    await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException or IOException) { }
                finally { context.Response.Close(); }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _accept;
            await Task.WhenAll(_responses);
            _stop.Dispose();
        }
    }
}
