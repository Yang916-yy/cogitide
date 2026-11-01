using System.Collections.Concurrent;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Host.RemoteService;

/// <summary>Foreground SSH/WSL stdio attachment. Its lifetime never owns or cancels daemon runs.</summary>
internal static class RemoteServiceBridge
{
    internal static async Task RunAsync(RemoteServiceEndpoint endpoint, TextReader input, TextWriter output, CancellationToken ct = default)
    {
        await using var connection = new RemoteServiceConnection(endpoint);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var outputGate = new SemaphoreSlim(1, 1);
        using var slots = new SemaphoreSlim(16, 16);
        var pending = new ConcurrentDictionary<long, Task>();
        long next = 0;
        await connection.SubscribeAsync(lifetime.Token);
        async Task WriteAsync<T>(T value)
        {
            await outputGate.WaitAsync(lifetime.Token);
            try
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(value, Wire.Json).AsMemory(), lifetime.Token).WaitAsync(lifetime.Token);
                await output.FlushAsync(lifetime.Token).WaitAsync(lifetime.Token);
            }
            finally { outputGate.Release(); }
        }
        async Task EventsAsync()
        {
            try
            {
                while (await connection.ReadNotificationAsync(lifetime.Token) is { } notification) await WriteAsync(notification);
            }
            finally { lifetime.Cancel(); }
        }
        async Task RequestAsync(RpcRequest request, bool gated)
        {
            try { await WriteAsync(await connection.RequestAsync(request, lifetime.Token)); }
            catch (Exception e) when (!lifetime.IsCancellationRequested && e is not OutOfMemoryException and not StackOverflowException)
            {
                await WriteAsync(new RpcReply(request.Id, Error: "The service connection was lost. The request outcome is unknown; inspect persisted state before retrying."));
                lifetime.Cancel();
            }
            finally { if (gated) slots.Release(); }
        }
        var events = EventsAsync();
        try
        {
            // Console.In and caller-supplied TextReaders may ignore cancellation once a read is
            // pending. Stop waiting locally when the service closes; caller EOF is not required.
            while (await input.ReadLineAsync(lifetime.Token).AsTask().WaitAsync(lifetime.Token) is { } line)
            {
                RpcRequest? request;
                try
                {
                    if (line.Length > ImageInputs.MaxRequestCharacters) throw new JsonException("Request exceeds the image and text size limit.");
                    request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json) ?? throw new JsonException("Empty request.");
                }
                catch (JsonException)
                {
                    await WriteAsync(new RpcReply("invalid", Error: "Invalid or oversized Host request."));
                    continue;
                }
                var control = request.Method is "run.cancel" or "run.steer" or "approval.respond" or "account.login.cancel" or "account.logout" or "account.select";
                if (!control && !await slots.WaitAsync(0, lifetime.Token))
                {
                    await WriteAsync(new RpcReply(request.Id, Error: "Host is busy. Retry after pending operations finish."));
                    continue;
                }
                var number = Interlocked.Increment(ref next);
                var task = RequestAsync(request, !control);
                pending[number] = task;
                _ = task.ContinueWith(_ => pending.TryRemove(number, out var ignored), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            // EOF aborts only local HTTP/WebSocket transport. There is deliberately no run.cancel RPC.
            lifetime.Cancel();
            try { await events; } catch (Exception e) when (e is OperationCanceledException or IOException or System.Net.WebSockets.WebSocketException) { }
            try { await Task.WhenAll(pending.Values); } catch (OperationCanceledException) { }
        }
    }
}
