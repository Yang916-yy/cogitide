using System.IO.Pipelines;
using System.Text;
using StreamJsonRpc;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class BoundedRpcPipesTests
{
    private const int Limit = 8 * 1024;

    [Fact]
    public async Task UnterminatedHeaderIsRejectedBeforeUnboundedBuffering()
    {
        using var stream = new MemoryStream(Enumerable.Repeat((byte)'h', Limit * 4).ToArray());
        var reader = BoundedRpcPipes.LimitReader(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)), Limit);
        using var handler = new HeaderDelimitedMessageHandler(writer: (PipeWriter?)null, reader, new SystemTextJsonFormatter());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.ReadAsync(deadline.Token).AsTask());
        Assert.InRange(stream.Position, Limit + 1, Limit + 4096);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(deadline.Token).AsTask());
    }

    [Fact]
    public async Task OversizedDeclaredBodyIsRejectedUsingOnlySmallInputBuffers()
    {
        var header = Encoding.ASCII.GetBytes("Content-Length: 2147483647\r\n\r\n");
        var payload = header.Concat(Enumerable.Repeat((byte)' ', Limit * 4)).ToArray();
        using var stream = new MemoryStream(payload);
        var reader = BoundedRpcPipes.LimitReader(PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true)), Limit);
        using var handler = new HeaderDelimitedMessageHandler(writer: (PipeWriter?)null, reader, new SystemTextJsonFormatter());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidDataException>(() => handler.ReadAsync(deadline.Token).AsTask());
        Assert.InRange(stream.Position, Limit + 1, Limit + header.Length + 4096);
    }

    [Fact]
    public async Task NormalMessagesStillUseTheMicrosoftParser()
    {
        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":\"bounded 😀\"}");
        var message = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
        using var stream = new MemoryStream(message);
        using var handler = new HeaderDelimitedMessageHandler(writer: (PipeWriter?)null,
            BoundedRpcPipes.CreateReader(stream), new SystemTextJsonFormatter());
        var result = await handler.ReadAsync(default);
        Assert.IsAssignableFrom<StreamJsonRpc.Protocol.JsonRpcResult>(result);
    }
}
