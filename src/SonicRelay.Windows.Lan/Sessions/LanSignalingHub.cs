using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Lan.Trust;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Lan.Sessions;

/// <summary>
/// LAN signaling for the publisher (issue #75). To the publisher's <see cref="ISignalingClient"/>
/// consumers it looks like the cloud signaling client; underneath, it plays the backend's
/// session roles for authenticated LAN viewers: it announces joins both ways, stamps
/// <c>from</c> on every viewer message, only lets viewers address the publisher, is the sole
/// authority for <c>participant.capabilities</c>, runs the 30 s reconnect grace and ends the
/// session for everyone on close.
/// </summary>
public sealed class LanSignalingHub : ISignalingClient, ILanConnectionSink
{
    public static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(30);
    public const int MaxMessageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> ViewerRoutedTypes = new(StringComparer.Ordinal)
    {
        SignalingMessageTypes.ViewerReady,
        SignalingMessageTypes.WebRtcAnswer,
        SignalingMessageTypes.WebRtcIceCandidate,
        SignalingMessageTypes.WebRtcRenegotiate,
    };

    private readonly IReadOnlyList<ISignalingMessageHandler> handlers;
    private readonly string publisherId;
    private readonly TimeProvider time;
    private readonly int maxViewers;
    private readonly Action<string>? log;
    private readonly LanTrustStore trust;
    private readonly object gate = new();
    private readonly SemaphoreSlim dispatchLock = new(1, 1);

    // Set while handlers run. A handler that sends (WebRtcPublisher declaring its capabilities
    // from inside its own session.joined) re-enters DispatchAsync on the same async flow; that
    // nested dispatch must run inline instead of waiting on the lock its caller holds.
    private readonly AsyncLocal<bool> insideDispatch = new();
    private readonly Dictionary<string, ViewerSlot> viewers = new(StringComparer.Ordinal);
    private string? sessionId;
    private bool publisherCanSend = true;
    private bool publisherCanReceive;
    private bool publisherMuted;

    public LanSignalingHub(
        IEnumerable<ISignalingMessageHandler> handlers,
        LanTrustStore trust,
        string publisherParticipantId,
        TimeProvider? time = null,
        int maxViewers = 15,
        Action<string>? log = null)
    {
        this.handlers = handlers?.ToArray() ?? throw new ArgumentNullException(nameof(handlers));
        this.trust = trust ?? throw new ArgumentNullException(nameof(trust));
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherParticipantId);
        publisherId = publisherParticipantId;
        this.time = time ?? TimeProvider.System;
        this.maxViewers = maxViewers;
        this.log = log;
        trust.Revoked += OnRevoked;
    }

    public SignalingConnectionState State { get; private set; } = SignalingConnectionState.Disconnected;
    public event Action<SignalingConnectionState>? StateChanged;
    public event Action<int>? ReconnectAttempting { add { } remove { } }
    public event Action<SignalingCloseReason>? Closed;

    /// <summary>Raised when the set of connected viewers changes (viewer count, origin routing).</summary>
    public event Action? ViewersChanged;

    /// <summary>
    /// Whether the publisher's own join and state broadcasts are echoed to the local handlers.
    /// True for a LAN-only session, where the hub is the only backend. False in a dual-homed
    /// session: RelayControl already describes the publisher to itself under its cloud
    /// participant id, and a second description under the LAN id would appear as a phantom
    /// participant. Viewers on the LAN still receive the publisher's state either way.
    /// </summary>
    public bool EchoPublisherToHandlers { get; set; } = true;

    /// <summary>The session mode the next/current session runs in; set before <see cref="ConnectAsync"/>.</summary>
    public string SessionMode { get; set; } = ParticipantAudioState.BroadcastMode;

    public string? SessionId
    {
        get
        {
            lock (gate) return sessionId;
        }
    }

    /// <summary>Viewers with a live socket.</summary>
    public IReadOnlyCollection<string> ConnectedViewers
    {
        get
        {
            lock (gate) return viewers.Where(pair => pair.Value.Socket is not null).Select(pair => pair.Key).ToArray();
        }
    }

    /// <summary>Whether <paramref name="viewerId"/> belongs to this hub (connected or in its grace period).</summary>
    public bool Knows(string viewerId)
    {
        lock (gate) return viewers.ContainsKey(viewerId);
    }

    public IReadOnlyList<ParticipantAudioState> Participants
    {
        get
        {
            lock (gate)
            {
                return [PublisherState(), .. viewers.Select(pair => ViewerState(pair.Key, pair.Value))];
            }
        }
    }

    public async Task ConnectAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ParticipantAudioState self;
        lock (gate)
        {
            this.sessionId = sessionId;
            var duplex = string.Equals(SessionMode, ParticipantAudioState.DuplexMode, StringComparison.OrdinalIgnoreCase);
            publisherCanSend = true;
            publisherCanReceive = duplex;
            publisherMuted = false;
            self = PublisherState();
        }

        SetState(SignalingConnectionState.Connected);
        if (EchoPublisherToHandlers)
            await DispatchAsync(new SignalingMessageEnvelope(SignalingMessageTypes.SessionJoined, sessionId, Payload: ToJson(self)), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        string? session;
        lock (gate) session = sessionId;
        if (session is null) throw new InvalidOperationException("The LAN session is not live.");

        if (message.To is { } target)
        {
            ViewerSlot? slot;
            lock (gate) viewers.TryGetValue(target, out slot);
            if (slot?.Socket is null)
            {
                log?.Invoke($"LAN signaling: viewer {target} is not connected; dropped {message.Type}.");
                return;
            }

            await SendToAsync(slot, message with { From = publisherId, SessionId = session }, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (message.Type)
        {
            case SignalingMessageTypes.ParticipantCapabilities:
                lock (gate)
                {
                    publisherCanSend = ReadBool(message.Payload, "canSendAudio") ?? publisherCanSend;
                    publisherCanReceive = ReadBool(message.Payload, "canReceiveAudio") ?? publisherCanReceive;
                }

                await BroadcastStateAsync(message.Type, publisherId, cancellationToken).ConfigureAwait(false);
                break;
            case SignalingMessageTypes.ParticipantAudioStateChanged:
                lock (gate) publisherMuted = ReadBool(message.Payload, "muted") ?? publisherMuted;
                await BroadcastStateAsync(message.Type, publisherId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                foreach (var slot in LiveSlots())
                    await SendToAsync(slot, message with { From = publisherId, SessionId = session }, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Grants or revokes a viewer's permission to send audio (duplex) and broadcasts it.</summary>
    public async Task SetAudioPermissionAsync(string viewerId, bool allowed, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!viewers.TryGetValue(viewerId, out var slot)) throw new InvalidOperationException("Unknown participant.");
            slot.AudioSendAllowed = allowed;
        }

        await BroadcastStateAsync(SignalingMessageTypes.ParticipantCapabilities, viewerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        string? session;
        ViewerSlot[] slots;
        lock (gate)
        {
            session = sessionId;
            sessionId = null;
            slots = viewers.Values.ToArray();
            viewers.Clear();
        }

        if (session is null) return;
        foreach (var slot in slots)
        {
            slot.Grace?.Cancel();
            if (slot.Socket is not { } socket) continue;
            try
            {
                await SendToAsync(slot, new SignalingMessageEnvelope(SignalingMessageTypes.SessionEnded, session, From: publisherId), cancellationToken).ConfigureAwait(false);
                await socket.CloseOutputAsync(LanCloseCodes.SessionEnded, "session_ended", cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException)
            {
            }
        }

        ViewersChanged?.Invoke();
        Closed?.Invoke(SignalingCloseReason.NormalClosure);
        SetState(SignalingConnectionState.Closed);
    }

    public async ValueTask DisposeAsync()
    {
        trust.Revoked -= OnRevoked;
        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>Serves one authenticated viewer socket until it closes.</summary>
    public async Task AcceptAsync(LanPeerConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var viewerId = connection.ViewerDeviceId;
        var socket = connection.Socket;
        ViewerSlot slot;
        bool reconnect;
        WebSocket? replaced = null;
        string session;
        lock (gate)
        {
            if (sessionId is null)
            {
                slot = null!;
                reconnect = false;
                session = null!;
            }
            else
            {
                session = sessionId;
                if (viewers.TryGetValue(viewerId, out var existing))
                {
                    reconnect = true;
                    existing.Grace?.Cancel();
                    existing.Grace = null;
                    replaced = existing.Socket;
                    existing.Socket = socket;
                    slot = existing;
                }
                else if (viewers.Count(pair => pair.Value.Socket is not null) >= maxViewers)
                {
                    slot = null!;
                    reconnect = false;
                }
                else
                {
                    reconnect = false;
                    slot = new ViewerSlot { Socket = socket };
                    viewers[viewerId] = slot;
                }
            }
        }

        if (slot is null)
        {
            await RejectAsync(socket, LanCloseCodes.Unavailable, session is null ? "no_session" : "full", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (replaced is not null) _ = RejectAsync(replaced, WebSocketCloseStatus.NormalClosure, "replaced", CancellationToken.None);
        ViewersChanged?.Invoke();
        log?.Invoke($"LAN viewer {viewerId} {(reconnect ? "reconnected" : "joined")}.");

        ParticipantAudioState viewerState, publisherState;
        lock (gate)
        {
            viewerState = ViewerState(viewerId, slot);
            publisherState = PublisherState();
        }

        await SendToAsync(slot, new SignalingMessageEnvelope(SignalingMessageTypes.SessionJoined, session, Payload: ToJson(viewerState)), cancellationToken).ConfigureAwait(false);
        await SendToAsync(slot, new SignalingMessageEnvelope(SignalingMessageTypes.SessionJoined, session, From: publisherId, Payload: ToJson(publisherState)), cancellationToken).ConfigureAwait(false);
        await DispatchAsync(new SignalingMessageEnvelope(
            reconnect ? SignalingMessageTypes.ParticipantReconnected : SignalingMessageTypes.SessionJoined,
            session,
            From: viewerId,
            Payload: ToJson(viewerState)), cancellationToken).ConfigureAwait(false);

        await ReceiveLoopAsync(viewerId, slot, socket, session, cancellationToken).ConfigureAwait(false);
        await OnSocketEndedAsync(viewerId, slot, socket, session).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(string viewerId, ViewerSlot slot, WebSocket socket, string session, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxMessageBytes + 1];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length >= buffer.Length)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "too_big", cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    length += result.Count;
                }
                while (!result.EndOfMessage);

                if (length > MaxMessageBytes)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "too_big", cancellationToken).ConfigureAwait(false);
                    return;
                }

                await HandleViewerMessageAsync(viewerId, slot, Encoding.UTF8.GetString(buffer, 0, length), session, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // A dropped socket ends the loop; the grace period decides what it means.
        }
    }

    private async Task HandleViewerMessageAsync(string viewerId, ViewerSlot slot, string json, string session, CancellationToken cancellationToken)
    {
        SignalingMessageEnvelope message;
        try
        {
            message = SignalingMessageEnvelope.Deserialize(json);
        }
        catch (SignalingProtocolException)
        {
            log?.Invoke($"LAN viewer {viewerId} sent an invalid envelope; dropped.");
            return;
        }

        if (message.Type == SignalingMessageTypes.Ping)
        {
            await SendToAsync(slot, new SignalingMessageEnvelope(SignalingMessageTypes.Pong, session), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(message.SessionId, session, StringComparison.Ordinal))
        {
            log?.Invoke($"LAN viewer {viewerId} addressed another session; dropped {message.Type}.");
            return;
        }

        if (ViewerRoutedTypes.Contains(message.Type))
        {
            if (!string.Equals(message.To, publisherId, StringComparison.Ordinal))
            {
                log?.Invoke($"LAN viewer {viewerId} addressed a non-publisher participant; dropped {message.Type}.");
                return;
            }

            await DispatchAsync(message with { From = viewerId }, cancellationToken).ConfigureAwait(false);
            return;
        }

        switch (message.Type)
        {
            case SignalingMessageTypes.ParticipantCapabilities:
                lock (gate)
                {
                    slot.DeclaredCanSendAudio = ReadBool(message.Payload, "canSendAudio") ?? slot.DeclaredCanSendAudio;
                    slot.CanReceiveAudio = ReadBool(message.Payload, "canReceiveAudio") ?? slot.CanReceiveAudio;
                }

                await BroadcastStateAsync(message.Type, viewerId, cancellationToken).ConfigureAwait(false);
                break;
            case SignalingMessageTypes.ParticipantAudioStateChanged:
                lock (gate) slot.AudioMuted = ReadBool(message.Payload, "muted") ?? slot.AudioMuted;
                await BroadcastStateAsync(message.Type, viewerId, cancellationToken).ConfigureAwait(false);
                break;
            default:
                log?.Invoke($"LAN viewer {viewerId} sent {message.Type}, which viewers may not send; dropped.");
                break;
        }
    }

    private async Task OnSocketEndedAsync(string viewerId, ViewerSlot slot, WebSocket socket, string session)
    {
        CancellationTokenSource grace;
        lock (gate)
        {
            // Replaced by a newer socket, removed by revocation, or the session ended: nothing to do.
            if (!ReferenceEquals(slot.Socket, socket) || !string.Equals(sessionId, session, StringComparison.Ordinal)
                || !viewers.ContainsKey(viewerId))
            {
                return;
            }

            slot.Socket = null;
            grace = new CancellationTokenSource();
            slot.Grace = grace;
        }

        ViewersChanged?.Invoke();
        await DispatchAsync(new SignalingMessageEnvelope(SignalingMessageTypes.ParticipantDisconnected, session, From: viewerId), CancellationToken.None).ConfigureAwait(false);
        _ = ExpireAfterGraceAsync(viewerId, slot, session, grace.Token);
    }

    private async Task ExpireAfterGraceAsync(string viewerId, ViewerSlot slot, string session, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReconnectGrace, time, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (gate)
        {
            if (!viewers.TryGetValue(viewerId, out var current) || !ReferenceEquals(current, slot) || slot.Socket is not null) return;
            viewers.Remove(viewerId);
        }

        ViewersChanged?.Invoke();
        await DispatchAsync(new SignalingMessageEnvelope(SignalingMessageTypes.SessionLeft, session, From: viewerId), CancellationToken.None).ConfigureAwait(false);
    }

    private void OnRevoked(string viewerId) => _ = RevokeViewerAsync(viewerId);

    private async Task RevokeViewerAsync(string viewerId)
    {
        ViewerSlot? slot;
        string? session;
        lock (gate)
        {
            session = sessionId;
            if (!viewers.Remove(viewerId, out slot)) return;
        }

        slot.Grace?.Cancel();
        log?.Invoke($"lan.revoked: closing LAN viewer {viewerId}.");
        if (slot.Socket is { } socket) await RejectAsync(socket, LanCloseCodes.Revoked, "revoked", CancellationToken.None).ConfigureAwait(false);
        ViewersChanged?.Invoke();
        if (session is not null)
            await DispatchAsync(new SignalingMessageEnvelope(SignalingMessageTypes.SessionLeft, session, From: viewerId), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task BroadcastStateAsync(string type, string participantId, CancellationToken cancellationToken)
    {
        string? session;
        ParticipantAudioState? state;
        lock (gate)
        {
            session = sessionId;
            state = participantId == publisherId
                ? PublisherState()
                : viewers.TryGetValue(participantId, out var slot) ? ViewerState(participantId, slot) : null;
        }

        if (session is null || state is null) return;
        var envelope = new SignalingMessageEnvelope(type, session, From: participantId, Payload: ToJson(state));
        foreach (var slot in LiveSlots()) await SendToAsync(slot, envelope, cancellationToken).ConfigureAwait(false);
        if (participantId != publisherId || EchoPublisherToHandlers)
            await DispatchAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private ViewerSlot[] LiveSlots()
    {
        lock (gate) return viewers.Values.Where(slot => slot.Socket is not null).ToArray();
    }

    private ParticipantAudioState PublisherState() => new(
        publisherId, "publisher", SessionMode, AudioSendAllowed: true, publisherCanSend, publisherCanReceive, publisherMuted);

    // Like the backend, declared intent is clamped to false while the participant is not authorized.
    private ParticipantAudioState ViewerState(string viewerId, ViewerSlot slot) => new(
        viewerId, "viewer", SessionMode, slot.AudioSendAllowed, slot.AudioSendAllowed && slot.DeclaredCanSendAudio,
        slot.CanReceiveAudio, slot.AudioMuted);

    private async Task DispatchAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken)
    {
        if (insideDispatch.Value)
        {
            await InvokeHandlersAsync(message, cancellationToken).ConfigureAwait(false);
            return;
        }

        await dispatchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            insideDispatch.Value = true;
            await InvokeHandlersAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            insideDispatch.Value = false;
            dispatchLock.Release();
        }
    }

    private async Task InvokeHandlersAsync(SignalingMessageEnvelope message, CancellationToken cancellationToken)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log?.Invoke($"LAN signaling handler failed on {message.Type}: {exception.Message}");
            }
        }
    }

    private async Task SendToAsync(ViewerSlot slot, SignalingMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        var socket = slot.Socket;
        if (socket is null) return;
        var bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
        await slot.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException)
        {
            // The receive loop notices the dead socket and starts the grace period.
        }
        finally
        {
            slot.SendLock.Release();
        }
    }

    private static async Task RejectAsync(WebSocket socket, WebSocketCloseStatus status, string reason, CancellationToken cancellationToken)
    {
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(status, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException or OperationCanceledException)
        {
        }
    }

    private void SetState(SignalingConnectionState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private static JsonElement ToJson(ParticipantAudioState state) => JsonSerializer.SerializeToElement(new
    {
        participantId = state.ParticipantId,
        role = state.Role,
        sessionMode = state.SessionMode,
        audioSendAllowed = state.AudioSendAllowed,
        canSendAudio = state.CanSendAudio,
        canReceiveAudio = state.CanReceiveAudio,
        audioMuted = state.AudioMuted,
    }, JsonOptions);

    private static bool? ReadBool(JsonElement? payload, string name) =>
        payload is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private sealed class ViewerSlot
    {
        public WebSocket? Socket { get; set; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource? Grace { get; set; }
        public bool AudioSendAllowed { get; set; }
        public bool DeclaredCanSendAudio { get; set; }
        public bool CanReceiveAudio { get; set; } = true;
        public bool AudioMuted { get; set; }
    }
}
