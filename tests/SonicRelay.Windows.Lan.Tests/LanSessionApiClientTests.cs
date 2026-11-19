using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Lan.Sessions;
using SonicRelay.Windows.Lan.Trust;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanSessionApiClientTests : IAsyncLifetime
{
    private static readonly Guid PublisherId = Guid.Parse("8f14e45f-ceea-467e-9b4a-1f3a2b6c7d8e");
    private const string ViewerA = "11111111-1111-1111-1111-111111111111";
    private readonly string trustPath = Path.Combine(Path.GetTempPath(), $"sonicrelay-apitrust-{Guid.NewGuid():N}.dat");
    private readonly RecordingHandler handler = new();
    private LanSignalingHub hub = null!;
    private LanSessionApiClient api = null!;
    private TestViewer? viewer;

    public Task InitializeAsync()
    {
        hub = new LanSignalingHub([handler], new LanTrustStore(new PassThroughProtector(), trustPath), PublisherId.ToString("D"));
        api = new LanSessionApiClient(hub, PublisherId);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await hub.DisposeAsync();
        if (viewer is not null) await viewer.DisposeAsync();
        if (File.Exists(trustPath)) File.Delete(trustPath);
    }

    private async Task JoinViewerAsync(Guid sessionId)
    {
        await hub.ConnectAsync(sessionId.ToString("D"));
        var pair = await SocketPair.CreateAsync();
        viewer = new TestViewer(ViewerA, pair, hub.AcceptAsync(new LanPeerConnection(ViewerA, pair.Server, null), CancellationToken.None));
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);
    }

    [Fact]
    public async Task CreateSession_IsLocal_AndEchoesMode()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest(Mode: SessionModes.Duplex));

        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(PublisherId, session.SourceDeviceId);
        Assert.Equal("LAN", session.Code);
        Assert.Equal(SessionModes.Duplex, session.Mode);
        Assert.Equal(SessionModes.Duplex, hub.SessionMode);
    }

    [Fact]
    public async Task CreateSession_DefaultsToBroadcast()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest());

        Assert.Equal(SessionModes.Broadcast, session.Mode);
    }

    [Fact]
    public async Task ActiveSessions_ReportConnectedViewers()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest());
        await JoinViewerAsync(session.Id);

        var active = Assert.Single(await api.GetActiveSessionsAsync());

        Assert.Equal(session.Id, active.Id);
        Assert.Equal(1, active.ViewerCount);
    }

    [Fact]
    public async Task Participants_IncludePublisherAndViewer()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest(Mode: SessionModes.Duplex));
        await JoinViewerAsync(session.Id);

        var response = await api.GetParticipantsAsync(session.Id);

        Assert.Equal(SessionModes.Duplex, response.Mode);
        Assert.Contains(response.Participants, p => p.Role == "publisher" && p.IsSelf);
        Assert.Contains(response.Participants, p => p.ParticipantId == Guid.Parse(ViewerA) && !p.AudioSendAllowed);
    }

    [Fact]
    public async Task SetAudioPermission_UpdatesTheViewer()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest(Mode: SessionModes.Duplex));
        await JoinViewerAsync(session.Id);

        var participant = await api.SetAudioPermissionAsync(session.Id, Guid.Parse(ViewerA), canSendAudio: true);

        Assert.True(participant.AudioSendAllowed);
    }

    [Fact]
    public async Task RotateCode_KeepsTheLanSession()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest());

        var rotated = await api.RotateCodeAsync(session.Id);

        Assert.Equal(session.Id, rotated.Id);
        Assert.Equal("LAN", rotated.Code);
    }

    [Fact]
    public async Task RotateCode_WithoutThatSession_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.RotateCodeAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task EndSession_ClosesHub_AndForgetsSession()
    {
        var session = await api.CreateSessionAsync(new CreateSessionRequest());
        await hub.ConnectAsync(session.Id.ToString("D"));

        var ended = await api.EndSessionAsync(session.Id);

        Assert.Equal("ended", ended.Status);
        Assert.Empty(await api.GetActiveSessionsAsync());
        Assert.Null(hub.SessionId);
    }
}
