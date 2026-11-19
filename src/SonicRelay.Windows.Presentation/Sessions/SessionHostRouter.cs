using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Lan.Sessions;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation.Sessions;

/// <summary>
/// The session API and signaling client the publisher workflow talks to (issue #75). It chooses
/// Cloud, LAN or Dual when a session is created — never during one — and from then on delegates
/// to RelayControl, the LAN hub, or both. In <c>auto</c>, a cloud that fails or does not answer
/// within <c>cloudTimeout</c> yields a LAN-only session instead of an error. In <c>lanOnly</c>,
/// nothing here ever touches the cloud clients.
/// </summary>
public sealed class SessionHostRouter : ISessionApiClient, ISignalingClient
{
    private readonly ISessionApiClient cloudSessions;
    private readonly ISignalingClient cloudSignaling;
    private readonly LanSessionApiClient lanSessions;
    private readonly LanSignalingHub hub;
    private readonly Func<string> connectivityMode;
    private readonly Func<bool> cloudUsable;
    private readonly TimeSpan cloudTimeout;
    private readonly Action<string>? log;
    private SignalingConnectionState state = SignalingConnectionState.Disconnected;

    public SessionHostRouter(
        ISessionApiClient cloudSessions,
        ISignalingClient cloudSignaling,
        LanSessionApiClient lanSessions,
        LanSignalingHub hub,
        Func<string> connectivityMode,
        Func<bool> cloudUsable,
        TimeSpan? cloudTimeout = null,
        Action<string>? log = null)
    {
        this.cloudSessions = cloudSessions ?? throw new ArgumentNullException(nameof(cloudSessions));
        this.cloudSignaling = cloudSignaling ?? throw new ArgumentNullException(nameof(cloudSignaling));
        this.lanSessions = lanSessions ?? throw new ArgumentNullException(nameof(lanSessions));
        this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
        this.connectivityMode = connectivityMode ?? throw new ArgumentNullException(nameof(connectivityMode));
        this.cloudUsable = cloudUsable ?? throw new ArgumentNullException(nameof(cloudUsable));
        this.cloudTimeout = cloudTimeout ?? TimeSpan.FromSeconds(5);
        this.log = log;
        cloudSignaling.StateChanged += _ => RecomputeState();
        cloudSignaling.ReconnectAttempting += OnCloudReconnectAttempting;
        cloudSignaling.Closed += OnCloudClosed;
        hub.StateChanged += _ => RecomputeState();
        hub.Closed += OnHubClosed;
    }

    /// <summary>The host of the current (or last) session; null before the first one.</summary>
    public SessionHostKind? ActiveKind { get; private set; }

    public event Action? ActiveKindChanged;

    public SignalingConnectionState State => state;
    public event Action<SignalingConnectionState>? StateChanged;
    public event Action<int>? ReconnectAttempting;
    public event Action<SignalingCloseReason>? Closed;

    /// <summary>Which transport carried <paramref name="viewerId"/>'s signaling; drives its ICE policy.</summary>
    public SignalingOrigin OriginOf(string viewerId) => ActiveKind switch
    {
        SessionHostKind.Lan => SignalingOrigin.Lan,
        SessionHostKind.Dual when hub.Knows(viewerId) => SignalingOrigin.Lan,
        _ => SignalingOrigin.Cloud,
    };

    public async Task<StreamSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        var kind = SessionHostSelector.Select(connectivityMode(), cloudUsable());
        switch (kind)
        {
            case SessionHostKind.Cloud:
                var cloudSession = await cloudSessions.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);
                SetKind(SessionHostKind.Cloud);
                return cloudSession;
            case SessionHostKind.Dual:
                var dual = await TryCreateCloudSessionAsync(request, cancellationToken).ConfigureAwait(false);
                if (dual is not null)
                {
                    lanSessions.Adopt(dual);
                    SetKind(SessionHostKind.Dual);
                    return dual;
                }

                log?.Invoke("cloud.unavailable: RelayControl did not answer; this session is LAN-only.");
                goto default;
            default:
                var lan = await lanSessions.CreateSessionAsync(request, cancellationToken).ConfigureAwait(false);
                SetKind(SessionHostKind.Lan);
                return lan;
        }
    }

    public Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default) =>
        CurrentKind switch
        {
            SessionHostKind.Lan => lanSessions.GetActiveSessionsAsync(cancellationToken),
            SessionHostKind.Dual => GetDualActiveSessionsAsync(cancellationToken),
            _ => cloudSessions.GetActiveSessionsAsync(cancellationToken),
        };

    public async Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        switch (CurrentKind)
        {
            case SessionHostKind.Lan:
                return await lanSessions.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            case SessionHostKind.Dual:
                await lanSessions.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                return await cloudSessions.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            default:
                return await cloudSessions.EndSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Only the cloud has an expiring join code; a dual session rotates it there.</summary>
    public Task<StreamSessionResponse> RotateCodeAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        CurrentKind == SessionHostKind.Lan
            ? lanSessions.RotateCodeAsync(sessionId, cancellationToken)
            : cloudSessions.RotateCodeAsync(sessionId, cancellationToken);

    public async Task<SessionParticipantsResponse> GetParticipantsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        switch (CurrentKind)
        {
            case SessionHostKind.Lan:
                return await lanSessions.GetParticipantsAsync(sessionId, cancellationToken).ConfigureAwait(false);
            case SessionHostKind.Dual:
                var lan = await lanSessions.GetParticipantsAsync(sessionId, cancellationToken).ConfigureAwait(false);
                try
                {
                    var cloud = await cloudSessions.GetParticipantsAsync(sessionId, cancellationToken).ConfigureAwait(false);
                    // The cloud already lists the publisher itself; the LAN adds only its viewers.
                    return cloud with { Participants = [.. cloud.Participants, .. lan.Participants.Where(p => !p.IsSelf)] };
                }
                catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
                {
                    return lan;
                }
            default:
                return await cloudSessions.GetParticipantsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<SessionParticipant> SetAudioPermissionAsync(Guid sessionId, Guid participantId, bool canSendAudio, CancellationToken cancellationToken = default) =>
        OriginOf(participantId.ToString("D")) == SignalingOrigin.Lan
            ? lanSessions.SetAudioPermissionAsync(sessionId, participantId, canSendAudio, cancellationToken)
            : cloudSessions.SetAudioPermissionAsync(sessionId, participantId, canSendAudio, cancellationToken);

    public async Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // In a dual session RelayControl is the one describing the publisher to itself.
        hub.EchoPublisherToHandlers = CurrentKind != SessionHostKind.Dual;
        switch (CurrentKind)
        {
            case SessionHostKind.Lan:
                await hub.ConnectAsync(sessionId, cancellationToken).ConfigureAwait(false);
                break;
            case SessionHostKind.Dual:
                await hub.ConnectAsync(sessionId, cancellationToken).ConfigureAwait(false);
                try
                {
                    await cloudSignaling.ConnectAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
                {
                    // LAN viewers still work; the cloud half is reported, not fatal.
                    log?.Invoke($"cloud.unavailable: RelayControl signaling failed ({exception.Message}); LAN viewers only.");
                }

                break;
            default:
                await cloudSignaling.ConnectAsync(sessionId, cancellationToken).ConfigureAwait(false);
                break;
        }

        RecomputeState();
    }

    public async Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        switch (CurrentKind)
        {
            case SessionHostKind.Lan:
                await hub.SendAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            case SessionHostKind.Dual when message.To is { } to:
                await (hub.Knows(to) ? hub.SendAsync(message, cancellationToken) : cloudSignaling.SendAsync(message, cancellationToken)).ConfigureAwait(false);
                return;
            case SessionHostKind.Dual:
                await hub.SendAsync(message, cancellationToken).ConfigureAwait(false);
                if (cloudSignaling.State == SignalingConnectionState.Connected)
                    await cloudSignaling.SendAsync(message, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await cloudSignaling.SendAsync(message, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        switch (CurrentKind)
        {
            case SessionHostKind.Lan:
                await hub.CloseAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SessionHostKind.Dual:
                await hub.CloseAsync(cancellationToken).ConfigureAwait(false);
                await cloudSignaling.CloseAsync(cancellationToken).ConfigureAwait(false);
                break;
            default:
                await cloudSignaling.CloseAsync(cancellationToken).ConfigureAwait(false);
                break;
        }

        RecomputeState();
    }

    public async ValueTask DisposeAsync()
    {
        await hub.DisposeAsync().ConfigureAwait(false);
        await cloudSignaling.DisposeAsync().ConfigureAwait(false);
    }

    // Before the first session, API calls follow the configured mode so `lanOnly` never
    // reaches RelayControl even for a viewer-count refresh.
    private SessionHostKind CurrentKind =>
        ActiveKind ?? (connectivityMode() == ConnectivityModes.LanOnly ? SessionHostKind.Lan : SessionHostKind.Cloud);

    private async Task<StreamSessionResponse?> TryCreateCloudSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(cloudTimeout);
        try
        {
            return await cloudSessions.CreateSessionAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            log?.Invoke($"cloud.unavailable: {exception.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<ActiveSessionResponse>> GetDualActiveSessionsAsync(CancellationToken cancellationToken)
    {
        var lan = await lanSessions.GetActiveSessionsAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cloud = await cloudSessions.GetActiveSessionsAsync(cancellationToken).ConfigureAwait(false);
            return cloud
                .Select(session => lan.FirstOrDefault(local => local.Id == session.Id) is { } local
                    ? session with { ViewerCount = session.ViewerCount + local.ViewerCount }
                    : session)
                .ToArray();
        }
        catch (Exception exception) when (IsCloudFailure(exception, cancellationToken))
        {
            return lan;
        }
    }

    // A failure of the cloud half, as opposed to the caller cancelling.
    private static bool IsCloudFailure(Exception exception, CancellationToken callerToken) =>
        !callerToken.IsCancellationRequested
        && exception is HttpRequestException or OperationCanceledException or TimeoutException
            or ApiClient.Errors.ApiClientException or System.Net.WebSockets.WebSocketException or InvalidOperationException
            or SignalingSessionGoneException;

    private void SetKind(SessionHostKind kind)
    {
        if (ActiveKind == kind) return;
        ActiveKind = kind;
        ActiveKindChanged?.Invoke();
    }

    private void RecomputeState()
    {
        var next = CurrentKind switch
        {
            SessionHostKind.Lan => hub.State,
            SessionHostKind.Dual when hub.State == SignalingConnectionState.Connected
                || cloudSignaling.State == SignalingConnectionState.Connected => SignalingConnectionState.Connected,
            SessionHostKind.Dual => hub.State == SignalingConnectionState.Closed ? hub.State : cloudSignaling.State,
            _ => cloudSignaling.State,
        };
        if (next == state) return;
        state = next;
        StateChanged?.Invoke(next);
    }

    private void OnCloudReconnectAttempting(int attempt)
    {
        if (CurrentKind != SessionHostKind.Lan) ReconnectAttempting?.Invoke(attempt);
    }

    // In a dual session the LAN half keeps serving viewers, so only a cloud-only session ends
    // when RelayControl's signaling gives up.
    private void OnCloudClosed(SignalingCloseReason reason)
    {
        if (CurrentKind == SessionHostKind.Cloud) Closed?.Invoke(reason);
    }

    private void OnHubClosed(SignalingCloseReason reason)
    {
        if (CurrentKind != SessionHostKind.Cloud) Closed?.Invoke(reason);
    }
}
