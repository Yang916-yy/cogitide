using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Protocol;

namespace Workbench.Host.RemoteService;

/// <summary>Kestrel transport around the existing Host dispatcher. Connections own no runs.</summary>
internal sealed class RemoteHostService : IAsyncDisposable
{
    private readonly RemoteServiceFiles _files;
    private readonly Func<string, JsonElement, Task<object>> _dispatch;
    private readonly ConcurrentDictionary<long, Channel<RpcNotification>> _subscribers = new();
    private readonly SemaphoreSlim _requests = new(16, 16);
    private readonly CancellationTokenSource _lifetime = new();
    private WebApplication? _application;
    private long _nextSubscriber;
    internal RemoteServiceEndpoint Endpoint { get; private set; }

    internal RemoteHostService(RemoteServiceFiles files, Func<string, JsonElement, Task<object>> dispatch)
    {
        _files = files;
        _dispatch = dispatch;
        Endpoint = new(new(files.GetOrCreateServiceId(), Guid.NewGuid().ToString("N"), Wire.Version,
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux", Environment.ProcessId,
            DateTimeOffset.UtcNow), "http://127.0.0.1:1/", RemoteServiceFiles.NewToken());
    }

    internal async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(RemoteHostService).Assembly.GetName().Name });
        // Deployment environment/appsettings cannot add a public listener to this private transport.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = ImageInputs.MaxRequestCharacters * 4L;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Reject browser-origin access and require both unpredictable authentication and installed identity.
            var authorization = context.Request.Headers.Authorization.ToString();
            var expected = Encoding.ASCII.GetBytes("Bearer " + Endpoint.Token);
            var supplied = Encoding.ASCII.GetBytes(authorization);
            if (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address) ||
                context.Request.Headers.ContainsKey("Origin") || context.Request.Headers["X-Cogitide-Service"] != Endpoint.Identity.ServiceId ||
                expected.Length != supplied.Length || !CryptographicOperations.FixedTimeEquals(expected, supplied))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.MapGet("/identity", () => Results.Json(Endpoint.Identity, Wire.Json));
        app.MapPost("/rpc", HandleRpcAsync);
        app.MapGet("/events", HandleEventsAsync);
        _application = app;
        try
        {
            await app.StartAsync(ct);
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
            Endpoint = Endpoint with { Address = (addresses?.Single() ?? throw new InvalidOperationException("Kestrel did not publish its loopback address.")).TrimEnd('/') + "/" };
            await _files.WriteAsync(Endpoint, ct);
        }
        catch { await app.DisposeAsync(); _application = null; throw; }
    }

    internal async Task WaitForShutdownAsync(CancellationToken ct = default)
    {
        var app = _application ?? throw new InvalidOperationException("The service has not started.");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping, ct);
        // Let the caller cancel/join kernel work before Kestrel drains outstanding HTTP requests.
        try { await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token); }
        catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested) { }
    }

    internal Task PublishAsync(RpcNotification notification)
    {
        foreach (var (id, channel) in _subscribers)
            if (!channel.Writer.TryWrite(notification))
            {
                // Never slow or cancel a run because a desktop is offline. Overflow closes that subscriber;
                // the persisted SQLite journal and approval snapshot are the recovery source.
                if (_subscribers.TryRemove(id, out _)) channel.Writer.TryComplete(new IOException("Live delivery overflowed; reconnect and load persisted pages."));
            }
        return Task.CompletedTask;
    }

    private async Task HandleRpcAsync(HttpContext context)
    {
        RpcRequest? request = null;
        bool gated = false;
        try
        {
            request = await JsonSerializer.DeserializeAsync<RpcRequest>(context.Request.Body, Wire.Json, context.RequestAborted)
                ?? throw new JsonException("A request is required.");
            if (request.Method.Length > 128 || request.Id.Length > 128) throw new ArgumentException("The request identifier or method is too large.");
            if (!IsControl(request.Method))
            {
                gated = await _requests.WaitAsync(0, _lifetime.Token);
                if (!gated) throw new InvalidOperationException("Host is busy. Retry after pending operations finish.");
            }
            object result;
            if (request.Method == "remote.service.status") result = Endpoint.Identity;
            else if (request.Method == "remote.service.shutdown")
            {
                result = new { stopping = true };
                context.Response.OnCompleted(() => { _application!.Lifetime.StopApplication(); return Task.CompletedTask; });
            }
            else
            {
                // Deliberately do not pass RequestAborted: losing an HTTP connection is not user cancellation.
                result = await _dispatch(request.Method, request.Params);
                if (request.Method == "initialize")
                {
                    var initialized = JsonSerializer.SerializeToNode(result, Wire.Json)!.AsObject();
                    initialized["remoteService"] = JsonSerializer.SerializeToNode(Endpoint.Identity, Wire.Json);
                    result = initialized;
                }
            }
            await WriteReplyAsync(context, new(request.Id, Wire.Element(result)));
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            if (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
                await WriteReplyAsync(context, new(request?.Id ?? "invalid", Error: e is OperationCanceledException ? "Operation cancelled." : e.Message));
        }
        finally { if (gated) _requests.Release(); }
    }

    private static Task WriteReplyAsync(HttpContext context, RpcReply reply) =>
        context.Response.WriteAsJsonAsync(reply, Wire.Json, cancellationToken: context.RequestAborted);

    private static bool IsControl(string method) => method is "run.cancel" or "run.steer" or "approval.respond" or
        "account.login.cancel" or "account.logout" or "account.select" or "remote.service.shutdown";

    private async Task HandleEventsAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var channel = Channel.CreateBounded<RpcNotification>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        var id = Interlocked.Increment(ref _nextSubscriber);
        _subscribers[id] = channel;
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _lifetime.Token);
        // A ready frame is the subscription barrier: the bridge waits for it before allowing RPCs.
        try
        {
            await SendAsync(socket, new("remote.service.ready", Wire.Element(Endpoint.Identity)), connection.Token);
            var receive = ReceiveCloseAsync(socket, connection);
            try
            {
                await foreach (var notification in channel.Reader.ReadAllAsync(connection.Token)) await SendAsync(socket, notification, connection.Token);
            }
            finally { connection.Cancel(); try { await receive; } catch (OperationCanceledException) { } }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException) { }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Reconnect and load persisted pages.", CancellationToken.None); } catch (WebSocketException) { }
        }
    }

    private static async Task ReceiveCloseAsync(WebSocket socket, CancellationTokenSource connection)
    {
        try
        {
            var message = await socket.ReceiveAsync(new byte[1], connection.Token);
            // The endpoint is notifications-only; any client frame closes its subscription.
            _ = message;
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) when (connection.IsCancellationRequested) { }
        finally { connection.Cancel(); }
    }

    private static Task SendAsync(WebSocket socket, RpcNotification notification, CancellationToken ct) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notification, Wire.Json)).AsMemory(), WebSocketMessageType.Text, true, ct).AsTask();

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        foreach (var channel in _subscribers.Values) channel.Writer.TryComplete();
        if (_application is { } app)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await app.StopAsync(stop.Token); } catch (OperationCanceledException) { }
            await app.DisposeAsync();
        }
        await _files.RemoveOwnEndpointAsync(Endpoint.Identity.InstanceId);
        _lifetime.Dispose();
        // An HTTP dispatcher which exceeded shutdown grace may still release this managed gate.
        // It owns no WaitHandle; do not race a late release with Dispose.
    }
}
