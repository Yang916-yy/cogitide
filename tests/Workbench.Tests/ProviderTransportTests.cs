using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ProviderTransportTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task ServiceErrorsNeverPersistResponseBodiesAndAreNotRetried(int status)
    {
        var handler = new ResponseHandler(() =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            { Content = new StringContent("{\"error\":{\"message\":\"fixture-private-secret\"}}", Encoding.UTF8, "application/json") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
            return response;
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => Collect(Create(http).StreamAsync([new("user", "test")], [])));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("fixture-private-secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?api_key=secret")]
    [InlineData("https://example.com/v1#secret")]
    public void EndpointsCannotSmuggleCredentialsOrParameters(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleModel(new(endpoint, "fixture"), "fixture-key"));

    [Fact]
    public async Task ToolDataWithNormalStopCannotBeExecuted()
    {
        var handler = Sse(Chunk(new { tool_calls = new[] { Call(0, "{}") } }) + Chunk(new { }, "stop"));
        using var http = new HttpClient(handler);
        var received = new List<ModelUpdate>();
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in Create(http).StreamAsync([new("user", "test")], [])) received.Add(item); });
        Assert.Empty(received.SelectMany(x => x.ToolCalls ?? []));
    }

    [Fact]
    public async Task ToolCountAndAggregateArgumentsHaveWholeResponseBudgets()
    {
        foreach (var calls in new[]
        {
            Enumerable.Range(0, 65).Select(i => Call(i, "{}")).ToArray(),
            new[] { Call(0, "{\"text\":\"" + new string('x', 600_000) + "\"}"), Call(1, "{\"text\":\"" + new string('y', 600_000) + "\"}") }
        })
        {
            using var http = new HttpClient(Sse(Chunk(new { tool_calls = calls }) + Chunk(new { }, "tool_calls")));
            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(Create(http).StreamAsync([new("user", "test")], [])));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StreamCannotChangeToolIdentityOrAppendAfterCompletion(bool changeIdentity)
    {
        var body = changeIdentity
            ? Chunk(new { tool_calls = new[] { Call(0, "{}") } }) +
              Chunk(new { tool_calls = new[] { new { index = 0, id = "different-call", type = "function", function = new { name = "", arguments = "" } } } }) + Chunk(new { }, "tool_calls")
            : Chunk(new { }, "stop") + Chunk(new { tool_calls = new[] { Call(0, "{}") } }) + Chunk(new { }, "tool_calls");
        using var http = new HttpClient(Sse(body));
        List<ModelUpdate> received = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var item in Create(http).StreamAsync([new("user", "test")], [])) received.Add(item); });
        Assert.Empty(received.SelectMany(x => x.ToolCalls ?? []));
    }

    [Fact]
    public async Task StopCancelsAStreamStalledAfterResponseHeaders()
    {
        using var stalled = new TailStream("", fail: false);
        using var http = new HttpClient(new ResponseHandler(() => StreamResponse(stalled)));
        using var cancel = new CancellationTokenSource();
        var request = Collect(Create(http).StreamAsync([new("user", "test")], [], cancel.Token));
        await stalled.ReachedTail.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BrokenStreamIsNotReplayedAndCannotExposeIncompleteCalls()
    {
        using var broken = new TailStream(Chunk(new { content = "partial", tool_calls = new[] { Call(0, "{}") } }), fail: true);
        var handler = new ResponseHandler(() => StreamResponse(broken));
        using var http = new HttpClient(handler);
        List<ModelUpdate> received = [];
        var error = await Assert.ThrowsAsync<ModelRequestException>(async () =>
        { await foreach (var item in Create(http).StreamAsync([new("user", "test")], [])) received.Add(item); });
        Assert.DoesNotContain("fixture-private-secret", error.ToString());
        Assert.Empty(received.SelectMany(x => x.ToolCalls ?? []));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DefaultBclTransportDoesNotFollowRedirects()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        origin.Start(); destination.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var target = (IPEndPoint)destination.LocalEndpoint;
        var respond = Task.Run(async () =>
        {
            using var connection = await origin.AcceptTcpClientAsync(deadline.Token);
            using var stream = connection.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
            var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{target.Port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(bytes, deadline.Token);
        }, deadline.Token);
        var port = ((IPEndPoint)origin.LocalEndpoint).Port;
        var provider = new OpenAiCompatibleModel(new($"http://127.0.0.1:{port}/v1", "fixture"), "fixture-key");
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => Collect(provider.StreamAsync([new("user", "test")], [], deadline.Token)));
        await respond;
        Assert.Equal(307, error.StatusCode);
        Assert.False(destination.Pending());
    }

    private static OpenAiCompatibleModel Create(HttpClient http) => new(new("http://localhost:9000/v1", "fixture"), "fixture-key", http);
    private static object Call(int index, string arguments) => new { index, id = $"call-{index}", type = "function", function = new { name = "read_file", arguments } };
    private static string Chunk(object delta, string? reason = null) => "data: " + JsonSerializer.Serialize(new
    { id = "completion", @object = "chat.completion.chunk", created = 1, model = "fixture", choices = new[] { new { index = 0, delta, finish_reason = reason } } }) + "\n\n";
    private static ResponseHandler Sse(string body) => new(() => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StringContent(body + "data: [DONE]\n\n", Encoding.UTF8, "text/event-stream") });
    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }
    private static async Task<List<ModelUpdate>> Collect(IAsyncEnumerable<ModelUpdate> source)
    { List<ModelUpdate> updates = []; await foreach (var item in source) updates.Add(item); return updates; }
    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response()); }
    }
    private sealed class TailStream(string prefix, bool fail) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        public TaskCompletionSource ReachedTail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _prefix.ReadAsync(buffer, cancellationToken);
            if (count > 0) return count;
            ReachedTail.TrySetResult();
            if (fail) throw new IOException("fixture-private-secret");
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _prefix.Dispose(); base.Dispose(disposing); }
    }
}
