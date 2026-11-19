using System.Net.WebSockets;
using Microsoft.Extensions.Time.Testing;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Lan.Sessions;
using SonicRelay.Windows.Lan.Trust;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanSignalingHubTests : IAsyncLifetime
{
    private const string PublisherId = "8f14e45f-ceea-467e-9b4a-1f3a2b6c7d8e";
    private const string ViewerA = "11111111-1111-1111-1111-111111111111";
    private const string ViewerB = "22222222-2222-2222-2222-222222222222";
    private const string SessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
    private readonly RecordingHandler handler = new();
    private readonly string trustPath = Path.Combine(Path.GetTempPath(), $"sonicrelay-hubtrust-{Guid.NewGuid():N}.dat");
    private readonly List<TestViewer> viewers = [];
    private LanTrustStore trust = null!;
    private LanSignalingHub hub = null!;

    public Task InitializeAsync()
    {
        trust = new LanTrustStore(new PassThroughProtector(), trustPath, time);
        hub = new LanSignalingHub([handler], trust, PublisherId, time, maxViewers: 2);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await hub.DisposeAsync();
        foreach (var viewer in viewers) await viewer.DisposeAsync();
        if (File.Exists(trustPath)) File.Delete(trustPath);
    }

    private async Task<TestViewer> ConnectAsync(string viewerId)
    {
        var pair = await SocketPair.CreateAsync();
        var serving = hub.AcceptAsync(new LanPeerConnection(viewerId, pair.Server, null), CancellationToken.None);
        var viewer = new TestViewer(viewerId, pair, serving);
        viewers.Add(viewer);
        return viewer;
    }

    private async Task StartSessionAsync(string mode = "broadcast")
    {
        hub.SessionMode = mode;
        await hub.ConnectAsync(SessionId);
    }

    [Fact]
    public async Task Connect_AnnouncesPublisherSelfJoin()
    {
        await StartSessionAsync();

        var self = handler.Next(SignalingMessageTypes.SessionJoined);
        Assert.Null(self.From);
        Assert.Equal(SessionId, self.SessionId);
        var state = ParticipantAudioState.TryParse(self.Payload)!;
        Assert.Equal(PublisherId, state.ParticipantId);
        Assert.True(state.IsPublisher);
        Assert.Equal(SignalingConnectionState.Connected, hub.State);
    }

    [Fact]
    public async Task ViewerJoin_EmitsSessionJoined_BothWays()
    {
        await StartSessionAsync();

        var viewer = await ConnectAsync(ViewerA);

        var toPublisher = handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);
        Assert.Equal("viewer", ParticipantAudioState.TryParse(toPublisher.Payload)!.Role);
        var publisherPresence = viewer.Next(SignalingMessageTypes.SessionJoined);
        Assert.True(publisherPresence.From is null || publisherPresence.From == PublisherId);
        Assert.Contains(ViewerA, hub.ConnectedViewers);
    }

    [Fact]
    public async Task ViewerReceivesPublisherJoinWithPublisherRole()
    {
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);

        var self = viewer.Next(SignalingMessageTypes.SessionJoined);
        var publisher = viewer.Next(SignalingMessageTypes.SessionJoined);

        Assert.Null(self.From);
        Assert.Equal(PublisherId, publisher.From);
        Assert.Equal("publisher", ParticipantAudioState.TryParse(publisher.Payload)!.Role);
    }

    [Fact]
    public async Task FromIsStampedByHub()
    {
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await viewer.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.ViewerReady, SessionId, PublisherId, From: "spoofed"));

        Assert.Equal(ViewerA, handler.Next(SignalingMessageTypes.ViewerReady).From);
    }

    [Fact]
    public async Task ViewerToViewer_IsDropped()
    {
        await StartSessionAsync();
        var a = await ConnectAsync(ViewerA);
        var b = await ConnectAsync(ViewerB);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerB);

        await a.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.WebRtcAnswer, SessionId, ViewerB, Json.Of(new { type = "answer", sdp = "x" })));

        Assert.False(b.Received(SignalingMessageTypes.WebRtcAnswer, TimeSpan.FromMilliseconds(400)));
        Assert.False(handler.Received(SignalingMessageTypes.WebRtcAnswer, ViewerA, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task WrongSessionId_IsDropped()
    {
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await viewer.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.ViewerReady, "other-session", PublisherId));

        Assert.False(handler.Received(SignalingMessageTypes.ViewerReady, ViewerA, TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public async Task PublisherOffer_RoutedToViewer()
    {
        await StartSessionAsync();
        var a = await ConnectAsync(ViewerA);
        var b = await ConnectAsync(ViewerB);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerB);

        await hub.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.WebRtcOffer, SessionId, ViewerA, Json.Of(new { type = "offer", sdp = "v=0" })));

        var offer = a.Next(SignalingMessageTypes.WebRtcOffer);
        Assert.Equal(PublisherId, offer.From);
        Assert.False(b.Received(SignalingMessageTypes.WebRtcOffer, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Ping_IsAnsweredWithPong()
    {
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);

        await viewer.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.Ping, SessionId));

        viewer.Next(SignalingMessageTypes.Pong);
    }

    [Fact]
    public async Task Drop_Then_Reconnect_Within30s_EmitsReconnected()
    {
        await StartSessionAsync();
        var first = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await first.DropAsync();
        handler.Next(SignalingMessageTypes.ParticipantDisconnected, ViewerA);
        time.Advance(TimeSpan.FromSeconds(20));
        await ConnectAsync(ViewerA);

        handler.Next(SignalingMessageTypes.ParticipantReconnected, ViewerA);
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.False(handler.Received(SignalingMessageTypes.SessionLeft, ViewerA, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task Drop_After30s_EmitsLeft()
    {
        await StartSessionAsync();
        var first = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await first.DropAsync();
        handler.Next(SignalingMessageTypes.ParticipantDisconnected, ViewerA);
        time.Advance(TimeSpan.FromSeconds(31));

        handler.Next(SignalingMessageTypes.SessionLeft, ViewerA);
        Assert.DoesNotContain(ViewerA, hub.ConnectedViewers);
    }

    [Fact]
    public async Task NoSession_Closes4409()
    {
        var viewer = await ConnectAsync(ViewerA);

        Assert.Equal(LanCloseCodes.Unavailable, await viewer.WaitClosedAsync());
    }

    [Fact]
    public async Task MaxViewers_Closes4409()
    {
        await StartSessionAsync();
        await ConnectAsync(ViewerA);
        await ConnectAsync(ViewerB);

        var third = await ConnectAsync("33333333-3333-3333-3333-333333333333");

        Assert.Equal(LanCloseCodes.Unavailable, await third.WaitClosedAsync());
    }

    [Fact]
    public async Task Close_SendsSessionEnded_And4410()
    {
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await hub.CloseAsync();

        viewer.Next(SignalingMessageTypes.SessionEnded);
        Assert.Equal(LanCloseCodes.SessionEnded, await viewer.WaitClosedAsync());
        Assert.Equal(SignalingConnectionState.Closed, hub.State);
    }

    [Fact]
    public async Task Revoke_ClosesLiveSocket()
    {
        await trust.AddAsync(ViewerA, "Pixel", "android");
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await trust.RevokeAsync(ViewerA);

        Assert.Equal(LanCloseCodes.Revoked, await viewer.WaitClosedAsync());
        handler.Next(SignalingMessageTypes.SessionLeft, ViewerA);
    }

    [Fact]
    public async Task Permission_BroadcastsCapabilities()
    {
        await StartSessionAsync("duplex");
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await hub.SetAudioPermissionAsync(ViewerA, allowed: true);

        var toPublisher = ParticipantAudioState.TryParse(handler.Next(SignalingMessageTypes.ParticipantCapabilities, ViewerA).Payload)!;
        Assert.True(toPublisher.AudioSendAllowed);
        var toViewer = viewer.Next(SignalingMessageTypes.ParticipantCapabilities);
        Assert.True(ParticipantAudioState.TryParse(toViewer.Payload)!.AudioSendAllowed);
    }

    [Fact]
    public async Task ViewerCapabilities_CannotRaiseAuthorization()
    {
        await StartSessionAsync("duplex");
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await viewer.SendAsync(new SignalingMessageEnvelope(
            SignalingMessageTypes.ParticipantCapabilities,
            SessionId,
            Payload: Json.Of(new { canSendAudio = true, canReceiveAudio = true, audioSendAllowed = true })));

        var state = ParticipantAudioState.TryParse(handler.Next(SignalingMessageTypes.ParticipantCapabilities, ViewerA).Payload)!;
        Assert.False(state.AudioSendAllowed);
        // Like the backend, declared intent is clamped to false while not authorized.
        Assert.False(state.CanSendAudio);
        Assert.False(state.IsAudioTrusted);
    }

    [Fact]
    public async Task DualMode_DoesNotEchoPublisherStateToHandlers_ButStillTellsViewers()
    {
        hub.EchoPublisherToHandlers = false;
        await StartSessionAsync("duplex");
        var viewer = await ConnectAsync(ViewerA);
        handler.Next(SignalingMessageTypes.SessionJoined, ViewerA);

        await hub.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.ParticipantAudioStateChanged, SessionId,
            Payload: Json.Of(new { muted = true })));

        Assert.True(ParticipantAudioState.TryParse(viewer.Next(SignalingMessageTypes.ParticipantAudioStateChanged).Payload)!.AudioMuted);
        Assert.False(handler.Received(SignalingMessageTypes.ParticipantAudioStateChanged, PublisherId, TimeSpan.FromMilliseconds(300)));
        lock (handler.All)
            Assert.DoesNotContain(handler.All, m => m.Type == SignalingMessageTypes.SessionJoined && m.From is null);
    }

    [Fact]
    public async Task HandlerSendingDuringDispatch_DoesNotDeadlock()
    {
        // WebRtcPublisher declares its capabilities from inside its own session.joined handler.
        LanSignalingHub? reentrant = null;
        var replying = new ReplyingHandler(async () =>
            await reentrant!.SendAsync(new SignalingMessageEnvelope(
                SignalingMessageTypes.ParticipantCapabilities, SessionId,
                Payload: Json.Of(new { canSendAudio = true, canReceiveAudio = true }))));
        reentrant = new LanSignalingHub([replying, handler], trust, PublisherId, time);
        reentrant.SessionMode = "duplex";

        await reentrant.ConnectAsync(SessionId).WaitAsync(TimeSpan.FromSeconds(5));

        handler.Next(SignalingMessageTypes.ParticipantCapabilities, PublisherId);
        await reentrant.DisposeAsync();
    }

    private sealed class ReplyingHandler(Func<Task> onSelfJoin) : ISignalingMessageHandler
    {
        public Task HandleAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default) =>
            message.Type == SignalingMessageTypes.SessionJoined && message.From is null ? onSelfJoin() : Task.CompletedTask;
    }

    [Fact]
    public async Task OversizedMessage_ClosesWith1009()
    {
        await StartSessionAsync();
        var viewer = await ConnectAsync(ViewerA);

        await viewer.SendRawAsync(new string('x', 70 * 1024));

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, await viewer.WaitClosedAsync());
    }
}
