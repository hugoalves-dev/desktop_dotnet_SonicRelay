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
    public void BuildConfigurationLanOnlyIgnoresTurnServersEvenIfForceRelayWasSelected()
    {
        var config = SipSorceryPeerConnectionFactory.BuildConfiguration(
            Servers, forceRelay: true, lanOnly: true);

        Assert.Equal(RTCIceTransportPolicy.all, config.iceTransportPolicy);
        Assert.Empty(config.iceServers);
    }

    [Fact]
    public async Task LanOnlyNeverCallsTheIceServerProvider()
    {
        var provider = new FailingIceServersProvider();
        var factory = new SipSorceryPeerConnectionFactory(
            provider, forceRelay: () => true, useLanOnly: () => true);

        await using var peer = await factory.CreateAsync(
            "local-viewer", new WebRtcPublisherOptions(Servers));

        Assert.NotNull(peer);
        Assert.Equal(0, provider.Calls);
    }

    private sealed class FailingIceServersProvider : IIceServersProvider
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<WebRtcIceServer>> GetIceServersAsync(
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("LAN must not use RelayControl.");
        }
    }
}
