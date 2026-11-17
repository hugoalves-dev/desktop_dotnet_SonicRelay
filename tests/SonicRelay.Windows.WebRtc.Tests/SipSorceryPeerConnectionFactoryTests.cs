using SIPSorcery.Net;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.WebRtc.Tests;

public sealed class SipSorceryPeerConnectionFactoryTests
{
    private static readonly WebRtcIceServer[] Servers =
    [
        new(["turn:relay.example:3478"], "user", "secret"),
    ];

    [Fact]
    public void BuildConfigurationAllowsDirectIceByDefault()
    {
        var config = SipSorceryPeerConnectionFactory.BuildConfiguration(Servers, forceRelay: false);

        Assert.Equal(RTCIceTransportPolicy.all, config.iceTransportPolicy);
        Assert.Equal("turn:relay.example:3478", Assert.Single(config.iceServers).urls);
    }

    [Fact]
    public void BuildConfigurationForcesRelayOnlyIce()
    {
        var config = SipSorceryPeerConnectionFactory.BuildConfiguration(Servers, forceRelay: true);

        Assert.Equal(RTCIceTransportPolicy.relay, config.iceTransportPolicy);
        // TURN credentials are still carried so relay can actually connect.
        Assert.Equal("secret", Assert.Single(config.iceServers).credential);
    }

    [Fact]
    public async Task LanViewer_GetsNoIceServers_AndProviderNotCalled()
    {
        var provider = new CountingProvider(Servers);
        var factory = new SipSorceryPeerConnectionFactory(
            provider,
            forceRelay: () => true,
            icePolicy: _ => PeerIcePolicy.For(SignalingOrigin.Lan, forceRelay: true));

        var config = await factory.ResolveConfigurationAsync("viewer-1", new WebRtcPublisherOptions(Servers));

        Assert.Empty(config.iceServers);
        Assert.Equal(RTCIceTransportPolicy.all, config.iceTransportPolicy);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task CloudViewer_KeepsProviderServers()
    {
        var provider = new CountingProvider(Servers);
        var factory = new SipSorceryPeerConnectionFactory(
            provider,
            icePolicy: _ => PeerIcePolicy.For(SignalingOrigin.Cloud, forceRelay: false));

        var config = await factory.ResolveConfigurationAsync("viewer-1", new WebRtcPublisherOptions());

        Assert.Equal("turn:relay.example:3478", Assert.Single(config.iceServers).urls);
        Assert.Equal(RTCIceTransportPolicy.all, config.iceTransportPolicy);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task CloudViewer_ForceRelay_UsesRelayPolicy()
    {
        var factory = new SipSorceryPeerConnectionFactory(new CountingProvider(Servers), forceRelay: () => true);

        var config = await factory.ResolveConfigurationAsync("viewer-1", new WebRtcPublisherOptions());

        Assert.Equal(RTCIceTransportPolicy.relay, config.iceTransportPolicy);
    }

    [Fact]
    public void LanPolicy_IgnoresForceRelay()
    {
        Assert.Equal(new PeerIcePolicy(UseConfiguredServers: false, ForceRelay: false),
            PeerIcePolicy.For(SignalingOrigin.Lan, forceRelay: true));
        Assert.Equal(new PeerIcePolicy(UseConfiguredServers: true, ForceRelay: true),
            PeerIcePolicy.For(SignalingOrigin.Cloud, forceRelay: true));
    }

    private sealed class CountingProvider(IReadOnlyList<WebRtcIceServer> servers) : IIceServersProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<WebRtcIceServer>> GetIceServersAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(servers);
        }
    }
}
