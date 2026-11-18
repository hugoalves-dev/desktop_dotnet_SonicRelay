using System.Net;
using System.Net.WebSockets;

namespace SonicRelay.Windows.Lan.Hosting;

public enum LanHostState
{
    Stopped,
    Listening,
    BindFailed,
}

public sealed class LanHostOptions
{
    /// <summary>Null listens on every interface (dual-stack); tests bind loopback.</summary>
    public IPAddress? BindAddress { get; init; }

    public required string PublisherName { get; init; }

    public int MaxConnections { get; init; } = 16;
    public int MaxRequestHeaderBytes { get; init; } = 8 * 1024;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>An authenticated viewer socket handed to the signaling hub.</summary>
public sealed record LanPeerConnection(string ViewerDeviceId, WebSocket Socket, IPEndPoint? Remote);

/// <summary>Receives authenticated sockets; the returned task completes when the socket is done.</summary>
public interface ILanConnectionSink
{
    Task AcceptAsync(LanPeerConnection connection, CancellationToken cancellationToken);
}

/// <summary>WebSocket close codes of the LAN signaling protocol v1.</summary>
public static class LanCloseCodes
{
    public const WebSocketCloseStatus Unauthorized = (WebSocketCloseStatus)4401;
    public const WebSocketCloseStatus Revoked = (WebSocketCloseStatus)4403;
    public const WebSocketCloseStatus Unavailable = (WebSocketCloseStatus)4409;
    public const WebSocketCloseStatus SessionEnded = (WebSocketCloseStatus)4410;
}
