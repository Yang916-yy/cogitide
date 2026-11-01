using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Host.RemoteService;

/// <summary>BCL HTTP/WebSocket client used only on the remote endpoint by the stdio bridge.</summary>
internal sealed class RemoteServiceConnection : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly ClientWebSocket _events = new();
    internal RemoteServiceEndpoint Endpoint { get; }

    internal RemoteServiceConnection(RemoteServiceEndpoint endpoint)
    {
        RemoteServiceFiles.Validate(endpoint, endpoint.Identity.ServiceId);
        Endpoint = endpoint;
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            BaseAddress = new(endpoint.Address), Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        _http.DefaultRequestHeaders.Add("X-Cogitide-Service", endpoint.Identity.ServiceId);
        _events.Options.SetRequestHeader("Authorization", "Bearer " + endpoint.Token);
        _events.Options.SetRequestHeader("X-Cogitide-Service", endpoint.Identity.ServiceId);
        _events.Options.Proxy = null;
        _events.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    }

    internal async Task<RemoteServiceIdentity> VerifyAsync(CancellationToken ct = default)
    {
        var identity = await _http.GetFromJsonAsync<RemoteServiceIdentity>("identity", Wire.Json, ct)
            ?? throw new InvalidDataException("The service did not return its identity.");
        if (identity != Endpoint.Identity) throw new InvalidDataException("The service endpoint identity no longer matches discovery.");
        return identity;
    }

    internal async Task SubscribeAsync(CancellationToken ct = default)
    {
        await VerifyAsync(ct);
        var address = new UriBuilder(new Uri(new Uri(Endpoint.Address), "events")) { Scheme = "ws" }.Uri;
        await _events.ConnectAsync(address, ct);
        var ready = await ReadNotificationAsync(ct);
        if (ready?.Method != "remote.service.ready" || ready.Params.Deserialize<RemoteServiceIdentity>(Wire.Json) != Endpoint.Identity)
            throw new InvalidDataException("The service subscription did not confirm its endpoint identity.");
    }

    internal async Task<RpcReply> RequestAsync(RpcRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("rpc", request, Wire.Json, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RpcReply>(Wire.Json, ct) ?? throw new InvalidDataException("The service returned an empty reply.");
    }

    internal async Task<RpcNotification?> ReadNotificationAsync(CancellationToken ct = default)
    {
        var buffer = new byte[16_384];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _events.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > ImageInputs.MaxRequestCharacters * 4L)
                throw new InvalidDataException("The service notification exceeds the supported text limit.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return JsonSerializer.Deserialize<RpcNotification>(message.GetBuffer().AsSpan(0, checked((int)message.Length)), Wire.Json)
            ?? throw new InvalidDataException("The service notification is empty.");
    }

    public ValueTask DisposeAsync()
    {
        _events.Abort();
        _events.Dispose();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
