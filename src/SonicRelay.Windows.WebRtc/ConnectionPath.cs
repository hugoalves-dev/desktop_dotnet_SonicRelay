namespace SonicRelay.Windows.WebRtc;

/// <summary>The path a viewer's audio actually took (issue #75), shown in the UI.</summary>
public enum ConnectionPath
{
    /// <summary>ICE has not connected yet; nothing may be claimed about the path.</summary>
    Pending,
    LanDirect,
    CloudDirect,
    CloudTurnRelay,
    Failed,
}

public static class ConnectionPathResolver
{
    /// <summary>
    /// Derives the effective path from the signaling origin and the nominated ICE pair
    /// (<c>"local->remote"</c> candidate types). Never reports a direct path before ICE is
    /// connected, and any relay candidate means TURN carried the media.
    /// </summary>
    public static ConnectionPath Resolve(SignalingOrigin origin, PeerConnectionState state, string? selectedCandidatePair)
    {
        if (state == PeerConnectionState.Failed) return ConnectionPath.Failed;
        if (state != PeerConnectionState.Connected || string.IsNullOrWhiteSpace(selectedCandidatePair)) return ConnectionPath.Pending;
        if (selectedCandidatePair.Contains("relay", StringComparison.OrdinalIgnoreCase)) return ConnectionPath.CloudTurnRelay;
        return origin == SignalingOrigin.Lan ? ConnectionPath.LanDirect : ConnectionPath.CloudDirect;
    }

    public static string Label(ConnectionPath path) => path switch
    {
        ConnectionPath.LanDirect => "LAN · P2P direct",
        ConnectionPath.CloudDirect => "RelayControl signaling · P2P direct",
        ConnectionPath.CloudTurnRelay => "RelayControl signaling · TURN relay",
        ConnectionPath.Failed => "No media path",
        _ => "Connecting…",
    };

    /// <summary>
    /// The diagnostic for a failed path. A LAN peer that fails had working signaling but no
    /// direct UDP route — firewall, ACL or client isolation — which signaling cannot bypass.
    /// </summary>
    public static string? DiagnosticCode(SignalingOrigin origin, ConnectionPath path) =>
        path == ConnectionPath.Failed
            ? origin == SignalingOrigin.Lan ? "ice.no_direct_route" : "ice.failed"
            : null;
}
