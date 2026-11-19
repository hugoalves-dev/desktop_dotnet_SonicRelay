using System.Text.Json;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Lan.Sessions;
using SonicRelay.Windows.Lan.Trust;
using SonicRelay.Windows.Presentation.Sessions;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class SessionHostRouterTests : IAsyncLifetime
{
    private static readonly Guid PublisherId = Guid.Parse("8f14e45f-ceea-467e-9b4a-1f3a2b6c7d8e");
    private readonly string trustPath = Path.Combine(Path.GetTempPath(), $"sonicrelay-router-{Guid.NewGuid():N}.dat");
    private readonly CloudSessions cloud = new();
    private readonly CloudSignaling cloudSignaling = new();
    private LanSignalingHub hub = null!;
    private string mode = ConnectivityModes.Auto;
    private bool cloudUsable = true;
    private SessionHostRouter router = null!;

    public Task InitializeAsync()
    {
        hub = new LanSignalingHub([], new LanTrustStore(new NullProtector(), trustPath), PublisherId.ToString("D"));
        router = new SessionHostRouter(cloud, cloudSignaling, new LanSessionApiClient(hub, PublisherId), hub,
            () => mode, () => cloudUsable, TimeSpan.FromMilliseconds(500));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await hub.DisposeAsync();
        if (File.Exists(trustPath)) File.Delete(trustPath);
    }

    [Theory]
    [InlineData(ConnectivityModes.CloudOnly, true, SessionHostKind.Cloud)]
    [InlineData(ConnectivityModes.CloudOnly, false, SessionHostKind.Cloud)]
    [InlineData(ConnectivityModes.LanOnly, true, SessionHostKind.Lan)]
    [InlineData(ConnectivityModes.Auto, true, SessionHostKind.Dual)]
    [InlineData(ConnectivityModes.Auto, false, SessionHostKind.Lan)]
    public void Selector_Matrix(string connectivity, bool usable, SessionHostKind expected)
    {
        Assert.Equal(expected, SessionHostSelector.Select(connectivity, usable));
    }

    [Fact]
    public async Task LanOnly_NeverCallsCloudApiOrSignaling()
    {
        mode = ConnectivityModes.LanOnly;
        cloud.Strict = cloudSignaling.Strict = true;

        var session = await router.CreateSessionAsync(new CreateSessionRequest());
        await router.ConnectAsync(session.Id.ToString("D"));
        await router.GetActiveSessionsAsync();
        await router.EndSessionAsync(session.Id);

        Assert.Equal("LAN", session.Code);
        Assert.Equal(SessionHostKind.Lan, router.ActiveKind);
        Assert.Equal(0, cloud.Calls);
        Assert.Equal(0, cloudSignaling.Calls);
    }

    [Fact]
    public async Task Auto_ApiDown_FallsBackToLanOnly()
    {
        cloud.Failure = new HttpRequestException("offline");

        var session = await router.CreateSessionAsync(new CreateSessionRequest());

        Assert.Equal("LAN", session.Code);
        Assert.Equal(SessionHostKind.Lan, router.ActiveKind);
    }

    [Fact]
    public async Task Auto_ApiHangs_FallsBackWithinTimeout()
    {
        cloud.Hang = true;

        var session = await router.CreateSessionAsync(new CreateSessionRequest()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(SessionHostKind.Lan, router.ActiveKind);
        Assert.Equal("LAN", session.Code);
    }

    [Fact]
    public async Task CloudOnly_ApiDown_Throws()
    {
        mode = ConnectivityModes.CloudOnly;
        cloud.Failure = new HttpRequestException("offline");

        await Assert.ThrowsAsync<HttpRequestException>(() => router.CreateSessionAsync(new CreateSessionRequest()));
    }

    [Fact]
    public async Task Auto_ApiUp_IsDual_WithOneSessionId()
    {
        var session = await router.CreateSessionAsync(new CreateSessionRequest(Mode: SessionModes.Duplex));
        await router.ConnectAsync(session.Id.ToString("D"));

        Assert.Equal(SessionHostKind.Dual, router.ActiveKind);
        Assert.Equal(cloud.Created!.Id, session.Id);
        Assert.Equal(session.Id.ToString("D"), hub.SessionId);
        Assert.Equal(SessionModes.Duplex, hub.SessionMode);
        Assert.Equal(1, cloudSignaling.Connects);
        Assert.Equal(SignalingConnectionState.Connected, router.State);
        Assert.False(hub.EchoPublisherToHandlers);
    }

    [Fact]
    public async Task LanSession_EchoesPublisherStateFromTheHub()
    {
        mode = ConnectivityModes.LanOnly;
        var session = await router.CreateSessionAsync(new CreateSessionRequest());

        await router.ConnectAsync(session.Id.ToString("D"));

        Assert.True(hub.EchoPublisherToHandlers);
    }

    [Fact]
    public async Task Dual_CloudSignalingFails_KeepsLan()
    {
        cloudSignaling.ConnectFailure = new HttpRequestException("ws down");
        var session = await router.CreateSessionAsync(new CreateSessionRequest());

        await router.ConnectAsync(session.Id.ToString("D"));

        Assert.Equal(SignalingConnectionState.Connected, router.State);
        Assert.Equal(session.Id.ToString("D"), hub.SessionId);
    }

    [Fact]
    public async Task Routing_ToCloudViewer_GoesToCloud_AndOriginIsCloud()
    {
        var session = await router.CreateSessionAsync(new CreateSessionRequest());
        await router.ConnectAsync(session.Id.ToString("D"));

        await router.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.WebRtcOffer, session.Id.ToString("D"), "cloud-viewer",
            JsonSerializer.SerializeToElement(new { type = "offer", sdp = "v=0" })));

        Assert.Single(cloudSignaling.Sent);
        Assert.Equal(SignalingOrigin.Cloud, router.OriginOf("cloud-viewer"));
    }

    [Fact]
    public async Task Routing_Broadcast_GoesToBoth()
    {
        var session = await router.CreateSessionAsync(new CreateSessionRequest(Mode: SessionModes.Duplex));
        await router.ConnectAsync(session.Id.ToString("D"));

        await router.SendAsync(new SignalingMessageEnvelope(SignalingMessageTypes.ParticipantAudioStateChanged, session.Id.ToString("D"),
            Payload: JsonSerializer.SerializeToElement(new { muted = true })));

        Assert.Single(cloudSignaling.Sent);
        Assert.True(hub.Participants.Single(p => p.IsPublisher).AudioMuted);
    }

    [Fact]
    public async Task LanSession_OriginOfKnownViewerIsLan()
    {
        mode = ConnectivityModes.LanOnly;
        await router.CreateSessionAsync(new CreateSessionRequest());

        Assert.Equal(SignalingOrigin.Lan, router.OriginOf("anyone"));
    }

    private sealed class NullProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }

    private sealed class CloudSessions : ISessionApiClient
    {
        public bool Strict { get; set; }
        public bool Hang { get; set; }
        public Exception? Failure { get; set; }
        public int Calls { get; private set; }
        public StreamSessionResponse? Created { get; private set; }

        private void Touch()
        {
            Calls++;
            if (Strict) throw new InvalidOperationException("The cloud API must not be called.");
            if (Failure is not null) throw Failure;
        }

        public async Task<StreamSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
        {
            Touch();
            if (Hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            Created = new StreamSessionResponse(Guid.NewGuid(), PublisherId, "live", 4, now.AddHours(1), now, null, now, "123456", request.Mode ?? SessionModes.Broadcast);
            return Created;
        }

        public Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
        {
            Touch();
            return Task.FromResult<IReadOnlyList<ActiveSessionResponse>>([]);
        }

        public Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Touch();
            return Task.FromResult(Created! with { Status = "ended" });
        }

        public Task<SessionParticipantsResponse> GetParticipantsAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            Touch();
            return Task.FromResult(new SessionParticipantsResponse(sessionId, SessionModes.Broadcast, []));
        }

        public Task<SessionParticipant> SetAudioPermissionAsync(Guid sessionId, Guid participantId, bool canSendAudio, CancellationToken cancellationToken = default)
        {
            Touch();
            throw new NotSupportedException();
        }
    }

    private sealed class CloudSignaling : ISignalingClient
    {
        public bool Strict { get; set; }
        public Exception? ConnectFailure { get; set; }
        public int Calls { get; private set; }
        public int Connects { get; private set; }
        public List<SignalingMessageEnvelope> Sent { get; } = [];
        public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Disconnected;
        public event Action<SignalingConnectionState>? StateChanged;
        public event Action<int>? ReconnectAttempting { add { } remove { } }
        public event Action<SignalingCloseReason>? Closed { add { } remove { } }

        private void Touch()
        {
            Calls++;
            if (Strict) throw new InvalidOperationException("Cloud signaling must not be used.");
        }

        public Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            Touch();
            if (ConnectFailure is not null) throw ConnectFailure;
            Connects++;
            State = SignalingConnectionState.Connected;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
        {
            Touch();
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            Touch();
            State = SignalingConnectionState.Closed;
            StateChanged?.Invoke(State);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
