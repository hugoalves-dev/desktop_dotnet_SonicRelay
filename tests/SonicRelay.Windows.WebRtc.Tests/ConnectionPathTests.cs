namespace SonicRelay.Windows.WebRtc.Tests;

public sealed class ConnectionPathTests
{
    [Theory]
    [InlineData(SignalingOrigin.Lan, PeerConnectionState.Connected, "host->host", ConnectionPath.LanDirect)]
    [InlineData(SignalingOrigin.Lan, PeerConnectionState.Connected, "host->prflx", ConnectionPath.LanDirect)]
    [InlineData(SignalingOrigin.Cloud, PeerConnectionState.Connected, "host->host", ConnectionPath.CloudDirect)]
    [InlineData(SignalingOrigin.Cloud, PeerConnectionState.Connected, "srflx->srflx", ConnectionPath.CloudDirect)]
    [InlineData(SignalingOrigin.Cloud, PeerConnectionState.Connected, "relay->host", ConnectionPath.CloudTurnRelay)]
    [InlineData(SignalingOrigin.Cloud, PeerConnectionState.Connected, "host->relay", ConnectionPath.CloudTurnRelay)]
    [InlineData(SignalingOrigin.Lan, PeerConnectionState.Failed, null, ConnectionPath.Failed)]
    [InlineData(SignalingOrigin.Cloud, PeerConnectionState.Failed, null, ConnectionPath.Failed)]
    public void Resolves(SignalingOrigin origin, PeerConnectionState state, string? pair, ConnectionPath expected)
    {
        Assert.Equal(expected, ConnectionPathResolver.Resolve(origin, state, pair));
    }

    [Theory]
    [InlineData(PeerConnectionState.New)]
    [InlineData(PeerConnectionState.Connecting)]
    [InlineData(PeerConnectionState.Disconnected)]
    public void NotConnected_IsPending_NeverP2P(PeerConnectionState state)
    {
        Assert.Equal(ConnectionPath.Pending, ConnectionPathResolver.Resolve(SignalingOrigin.Lan, state, "host->host"));
    }

    [Fact]
    public void Connected_WithUnknownPair_IsPending()
    {
        Assert.Equal(ConnectionPath.Pending, ConnectionPathResolver.Resolve(SignalingOrigin.Lan, PeerConnectionState.Connected, null));
    }

    [Theory]
    [InlineData(ConnectionPath.LanDirect, "LAN · P2P direct")]
    [InlineData(ConnectionPath.CloudDirect, "RelayControl signaling · P2P direct")]
    [InlineData(ConnectionPath.CloudTurnRelay, "RelayControl signaling · TURN relay")]
    [InlineData(ConnectionPath.Pending, "Connecting…")]
    public void Labels_MatchTheSpec(ConnectionPath path, string label)
    {
        Assert.Equal(label, ConnectionPathResolver.Label(path));
    }

    [Fact]
    public void LanFailure_DiagnosesNoDirectRoute()
    {
        Assert.Equal("ice.no_direct_route", ConnectionPathResolver.DiagnosticCode(SignalingOrigin.Lan, ConnectionPath.Failed));
        Assert.Null(ConnectionPathResolver.DiagnosticCode(SignalingOrigin.Lan, ConnectionPath.LanDirect));
    }
}
