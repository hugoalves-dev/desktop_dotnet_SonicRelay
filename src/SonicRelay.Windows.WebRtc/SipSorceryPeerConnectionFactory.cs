using SIPSorcery.Net;
using SonicRelay.Windows.Core.Audio;

namespace SonicRelay.Windows.WebRtc;

/// <summary>
/// Creates <see cref="SipSorceryPeerConnection"/> instances, resolving fresh
/// ICE servers (including short-lived TURN credentials) from the injected
/// provider for every viewer so relayed sessions never start with expired
/// credentials. Falls back to the statically configured options if the
/// provider yields nothing.
/// </summary>
public sealed class SipSorceryPeerConnectionFactory(
    IIceServersProvider iceServersProvider,
    Func<bool>? forceRelay = null,
    Func<AudioQualityProfile>? audioProfile = null,
    Func<WebRtcAudioDirection>? audioDirection = null,
    Func<bool>? useLanOnly = null) : IWebRtcPeerConnectionFactory
{
    private readonly IIceServersProvider iceServersProvider =
        iceServersProvider ?? throw new ArgumentNullException(nameof(iceServersProvider));

    // Read at creation time so a settings toggle applies to the next viewer.
    private readonly Func<bool> forceRelay = forceRelay ?? (() => false);

    // Read at creation time so the selected audio-quality profile applies to the
    // next viewer/stream without disrupting connections already in progress.
    private readonly Func<AudioQualityProfile> audioProfile =
        audioProfile ?? (() => AudioQualityProfile.Default);

    // Read at creation time, from the mode of the session that is live when this viewer
    // joins. A session's mode never changes, so every peer of one session agrees.
    private readonly Func<WebRtcAudioDirection> audioDirection =
        audioDirection ?? (() => WebRtcAudioDirection.SendOnly);

    // This flag must come from the authenticated session transport, never untrusted discovery.
    private readonly Func<bool> useLanOnly = useLanOnly ?? (() => false);

    public async Task<IWebRtcPeerConnection> CreateAsync(
        string viewerId,
        WebRtcPublisherOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerId);
        ArgumentNullException.ThrowIfNull(options);

        // In LAN-only mode do not even query the API-backed provider: besides avoiding
        // STUN/TURN traffic this prevents an offline session from depending on RelayControl.
        var lanOnly = useLanOnly();
        var servers = lanOnly
            ? Array.Empty<WebRtcIceServer>()
            : await ResolveIceServersAsync(options, cancellationToken).ConfigureAwait(false);
        var configuration = BuildConfiguration(servers, forceRelay: !lanOnly && forceRelay(), lanOnly);
        return new SipSorceryPeerConnection(
            viewerId,
            new RTCPeerConnection(configuration),
            audioProfile(),
            audioDirection());
    }

    /// <summary>
    /// Builds the peer configuration. When <paramref name="forceRelay"/> is true the
    /// transport policy is <c>relay</c> (TURN only); otherwise <c>all</c> permits
    /// direct host/srflx candidates. Exposed for testing the policy mapping.
    /// </summary>
    public static RTCConfiguration BuildConfiguration(IReadOnlyList<WebRtcIceServer> servers, bool forceRelay, bool lanOnly = false) =>
        new()
        {
            // Ignore accidentally supplied STUN/TURN entries when LAN-only is selected.
            iceServers = lanOnly ? [] : MapIceServers(servers),
            iceTransportPolicy = lanOnly ? RTCIceTransportPolicy.all :
                forceRelay ? RTCIceTransportPolicy.relay : RTCIceTransportPolicy.all,
        };

    private async Task<IReadOnlyList<WebRtcIceServer>> ResolveIceServersAsync(
        WebRtcPublisherOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await iceServersProvider.GetIceServersAsync(cancellationToken).ConfigureAwait(false);
            if (resolved is { Count: > 0 }) return resolved;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The provider is expected to be resilient, but never let ICE-server
            // resolution abort peer creation; fall back to configured defaults.
        }
        return options.IceServers;
    }

    // SIPSorcery's RTCIceServer.urls holds a single URL, so each URL becomes its
    // own entry while sharing the TURN credentials.
    private static List<RTCIceServer> MapIceServers(IReadOnlyList<WebRtcIceServer> servers)
    {
        var mapped = new List<RTCIceServer>();
        foreach (var server in servers)
        {
            foreach (var url in server.Urls)
            {
                mapped.Add(new RTCIceServer
                {
                    urls = url,
                    username = server.Username,
                    credential = server.Credential,
                    credentialType = RTCIceCredentialType.password
                });
            }
        }
        return mapped;
    }
}
