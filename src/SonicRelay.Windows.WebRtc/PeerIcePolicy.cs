namespace SonicRelay.Windows.WebRtc;

/// <summary>Which transport carried a viewer's signaling (issue #75).</summary>
public enum SignalingOrigin
{
    Cloud,
    Lan,
}

/// <summary>
/// The ICE configuration one viewer's peer connection is built with. A LAN-signaled peer never
/// uses STUN/TURN — host candidates cover same-subnet and routed-subnet paths, and a LAN session
/// must not acquire an internet dependency — so <see cref="ForceRelay"/> only applies to cloud peers.
/// </summary>
public sealed record PeerIcePolicy(bool UseConfiguredServers, bool ForceRelay)
{
    public static PeerIcePolicy For(SignalingOrigin origin, bool forceRelay) =>
        origin == SignalingOrigin.Lan
            ? new PeerIcePolicy(UseConfiguredServers: false, ForceRelay: false)
            : new PeerIcePolicy(UseConfiguredServers: true, ForceRelay: forceRelay);
}
