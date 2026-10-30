using System.ClientModel.Primitives;
using System.Net;

namespace Workbench.Core;

/// <summary>One process-wide BCL connection pool; provider SDKs still own HTTP serialization and SSE.</summary>
internal static class ModelHttpTransport
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(30),
        MaxConnectionsPerServer = 16,
        MaxResponseHeadersLength = 64,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = Timeout.InfiniteTimeSpan };

    // Timeout and cancellation belong to the SDK request and the run budget, not a second HttpClient timer.
    internal static HttpClientPipelineTransport Shared { get; } = new(Client);
    internal static HttpClient SharedClient => Client;
}

/// <summary>Safe to persist: never retains the upstream body, headers, credentials or inner exception.</summary>
public sealed class ModelRequestException : Exception
{
    public int? StatusCode { get; }

    internal ModelRequestException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;

    internal static ModelRequestException ForStatus(int status) => new(status switch
    {
        401 or 403 => $"The model service rejected authentication (HTTP {status}). Check the configured account or API key.",
        429 => "The model service rate limit was reached (HTTP 429). Wait before trying again; this request was not automatically repeated.",
        >= 300 and < 400 => $"The model endpoint returned a redirect (HTTP {status}). Configure the final HTTPS endpoint explicitly.",
        >= 500 and <= 599 => $"The model service is temporarily unavailable (HTTP {status}). This request was not automatically repeated.",
        >= 400 and <= 499 => $"The model service rejected the request (HTTP {status}). Check the endpoint, model and supported parameters.",
        _ => "The model request could not be completed. Check the endpoint and network connection."
    }, status > 0 ? status : null);
}
