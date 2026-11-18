using System.Buffers.Text;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonicRelay.Windows.Lan.Identity;
using SonicRelay.Windows.Lan.Pairing;
using SonicRelay.Windows.Lan.Trust;

namespace SonicRelay.Windows.Lan.Hosting;

/// <summary>
/// The publisher's local signaling endpoint (issue #75): Kestrel over TLS with the pinned
/// <see cref="LanIdentity"/> certificate. Two routes only — <c>POST /lan/v1/pair</c> and the
/// authenticated <c>GET /lan/v1/signaling</c> WebSocket — everything else is 404, and there is
/// no unauthenticated information endpoint. Runs as the current user on any port; it never
/// touches the firewall.
/// </summary>
public sealed class LanSignalingHost(
    LanIdentity identity,
    LanPairingService pairing,
    LanTrustStore trust,
    ILanConnectionSink sink,
    LanHostOptions options,
    TimeProvider? time = null,
    Action<string>? log = null) : IAsyncDisposable
{
    public const string AuthorizationScheme = "SonicRelay-Lan";
    public const string DeviceHeader = "X-SonicRelay-Device";
    private const int MaxPairBodyBytes = 4 * 1024;
    private const int MaxFieldLength = 128;

    private readonly AttemptLimiter limiter = new(time ?? TimeProvider.System);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private WebApplication? app;

    public LanHostState State { get; private set; } = LanHostState.Stopped;
    public int? Port { get; private set; }
    public string? Error { get; private set; }
    public event Action? StateChanged;

    /// <summary>
    /// Starts listening on <paramref name="port"/> (null/0 = dynamic). With
    /// <paramref name="fallbackToDynamic"/>, a busy remembered port falls back to a dynamic one;
    /// a fixed port that is busy is reported as <see cref="LanHostState.BindFailed"/>.
    /// </summary>
    public async Task StartAsync(int? port, bool fallbackToDynamic = false, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            try
            {
                await StartCoreAsync(port ?? 0, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) when (fallbackToDynamic && port is > 0)
            {
                await StopCoreAsync().ConfigureAwait(false);
                await StartCoreAsync(0, cancellationToken).ConfigureAwait(false);
            }

            Error = null;
            State = LanHostState.Listening;
            log?.Invoke($"LAN host listening on port {Port} (pin {LanIdentity.ShortPin(identity.Pin)}).");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = LanHostState.BindFailed;
            Error = exception.Message;
            log?.Invoke($"lan.bind_failed: could not listen on port {port}: {exception.Message}");
        }
        finally
        {
            lifecycle.Release();
        }

        StateChanged?.Invoke();
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            State = LanHostState.Stopped;
            Error = null;
        }
        finally
        {
            lifecycle.Release();
        }

        StateChanged?.Invoke();
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StartCoreAsync(int port, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ApplicationName = "SonicRelay.Lan" });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = options.MaxConnections;
            kestrel.Limits.MaxConcurrentUpgradedConnections = options.MaxConnections;
            kestrel.Limits.MaxRequestHeadersTotalSize = options.MaxRequestHeaderBytes;
            kestrel.Limits.MaxRequestBodySize = MaxPairBodyBytes;
            kestrel.Limits.RequestHeadersTimeout = options.HandshakeTimeout;
            void Https(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listen) =>
                listen.UseHttps(https =>
                {
                    https.ServerCertificate = identity.Certificate;
                    https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                    https.HandshakeTimeout = options.HandshakeTimeout;
                });
            if (options.BindAddress is { } address) kestrel.Listen(address, port, Https);
            else kestrel.ListenAnyIP(port, Https);
        });

        var web = builder.Build();
        web.UseWebSockets(new WebSocketOptions { KeepAliveInterval = options.KeepAliveInterval });
        web.MapPost("/lan/v1/pair", PairAsync);
        web.Map("/lan/v1/signaling", SignalingAsync);
        web.MapFallback(context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });

        app = web;
        await web.StartAsync(cancellationToken).ConfigureAwait(false);
        var addresses = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        Port = addresses?.Select(value => new Uri(value.Replace("*", "localhost", StringComparison.Ordinal)).Port).FirstOrDefault();
    }

    private async Task StopCoreAsync()
    {
        if (app is null) return;
        var running = app;
        app = null;
        Port = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await running.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Connections that did not drain in time are aborted by DisposeAsync below.
        }
        finally
        {
            await running.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task PairAsync(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (limiter.IsBlocked(remote))
        {
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited").ConfigureAwait(false);
            return;
        }

        PairRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<PairRequest>(context.Request.Body, JsonOptions, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null || !request.IsWellFormed())
        {
            limiter.RecordFailure(remote);
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "pairing_invalid").ConfigureAwait(false);
            return;
        }

        var result = pairing.Redeem(request.Pid, request.DecodeSecret(), request.Code);
        if (result != LanRedeemResult.Accepted)
        {
            limiter.RecordFailure(remote);
            log?.Invoke($"LAN pairing rejected from {remote}: {result}.");
            var (status, code) = result == LanRedeemResult.Expired
                ? (StatusCodes.Status410Gone, "pairing_expired")
                : (StatusCodes.Status400BadRequest, "pairing_invalid");
            await WriteErrorAsync(context, status, code).ConfigureAwait(false);
            return;
        }

        var credential = await trust.AddAsync(request.ViewerDeviceId!, request.ViewerName ?? "", request.Platform ?? "", context.RequestAborted).ConfigureAwait(false);
        log?.Invoke($"LAN pairing accepted for device '{request.ViewerName}'.");
        await context.Response.WriteAsJsonAsync(new PairResponse(
            credential,
            identity.PublisherDeviceId.ToString("D"),
            options.PublisherName,
            identity.Pin), JsonOptions, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task SignalingAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var remote = context.Connection.RemoteIpAddress;
        if (limiter.IsBlocked(remote))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        var viewerId = context.Request.Headers[DeviceHeader].ToString();
        var credential = ReadCredential(context.Request.Headers.Authorization.ToString());
        var device = credential is null ? null : trust.Authenticate(viewerId, credential);
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        if (device is null)
        {
            var revoked = trust.Devices.Any(known => known.ViewerDeviceId == viewerId && known.Revoked);
            if (!revoked) limiter.RecordFailure(remote);
            log?.Invoke(revoked ? "lan.revoked: a revoked device tried to connect." : "LAN signaling rejected: bad credential.");
            await socket.CloseAsync(
                revoked ? LanCloseCodes.Revoked : LanCloseCodes.Unauthorized,
                revoked ? "revoked" : "unauthorized",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        _ = trust.TouchAsync(device.ViewerDeviceId);
        var endpoint = remote is null ? null : new IPEndPoint(remote, context.Connection.RemotePort);
        await sink.AcceptAsync(new LanPeerConnection(device.ViewerDeviceId, socket, endpoint), context.RequestAborted).ConfigureAwait(false);
    }

    private static string? ReadCredential(string header)
    {
        const string prefix = AuthorizationScheme + " ";
        return header.StartsWith(prefix, StringComparison.Ordinal) ? header[prefix.Length..].Trim() : null;
    }

    private static Task WriteErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorResponse(code), JsonOptions, context.RequestAborted);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record PairRequest(Guid? Pid, string? Sec, string? Code, string? ViewerDeviceId, string? ViewerName, string? Platform)
    {
        public bool IsWellFormed() =>
            // A GUID, like every participant id the session API exposes.
            Guid.TryParseExact(ViewerDeviceId, "D", out _)
            && (ViewerName?.Length ?? 0) <= MaxFieldLength
            && (Platform?.Length ?? 0) <= MaxFieldLength
            && ((Pid is not null && Sec is not null) || Code is not null);

        public byte[]? DecodeSecret()
        {
            if (Sec is null) return null;
            try
            {
                return Base64Url.DecodeFromChars(Sec);
            }
            catch (FormatException)
            {
                return [];
            }
        }
    }

    private sealed record PairResponse(string Credential, string PublisherDeviceId, string PublisherName, string Pin);

    private sealed record ErrorResponse(string Error);
}
