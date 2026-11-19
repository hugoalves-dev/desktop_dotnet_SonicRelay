using SonicRelay.Windows.ApiClient.Sessions;

namespace SonicRelay.Windows.Lan.Sessions;

/// <summary>
/// The session API of a LAN-only session (issue #75): the session, its participants and their
/// audio permissions live in this process, backed by the <see cref="LanSignalingHub"/>. Lets
/// <c>PublisherWorkflow</c> run unchanged with no RelayControl at all.
/// </summary>
public sealed class LanSessionApiClient(LanSignalingHub hub, Guid publisherDeviceId, TimeProvider? time = null) : ISessionApiClient
{
    public const string LanSessionCode = "LAN";

    private readonly LanSignalingHub hub = hub ?? throw new ArgumentNullException(nameof(hub));
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private readonly object gate = new();
    private StreamSessionResponse? current;

    public Task<StreamSessionResponse> CreateSessionAsync(CreateSessionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = SessionModes.IsDuplex(request.Mode) ? SessionModes.Duplex : SessionModes.Broadcast;
        var now = time.GetUtcNow();
        var session = new StreamSessionResponse(
            Guid.NewGuid(), publisherDeviceId, "live", request.MaxViewers ?? 15,
            DateTimeOffset.MaxValue, now, null, now, LanSessionCode, mode);
        lock (gate) current = session;
        hub.SessionMode = mode;
        return Task.FromResult(session);
    }

    /// <summary>Adopts an existing (cloud) session id so a dual-homed session has one identity.</summary>
    public void Adopt(StreamSessionResponse session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (gate) current = session;
        hub.SessionMode = SessionModes.IsDuplex(session.Mode) ? SessionModes.Duplex : SessionModes.Broadcast;
    }

    public Task<IReadOnlyList<ActiveSessionResponse>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
    {
        StreamSessionResponse? session;
        lock (gate) session = current;
        IReadOnlyList<ActiveSessionResponse> result = session is null
            ? []
            : [new ActiveSessionResponse(session.Id, session.SourceDeviceId, session.Status, session.MaxViewers,
                session.CodeExpiresAt, session.StartedAt, session.EndedAt, session.CreatedAt,
                hub.ConnectedViewers.Count, session.Mode)];
        return Task.FromResult(result);
    }

    public async Task<StreamSessionResponse> EndSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        StreamSessionResponse? session;
        lock (gate)
        {
            session = current?.Id == sessionId ? current : null;
            if (session is not null) current = null;
        }

        await hub.CloseAsync(cancellationToken).ConfigureAwait(false);
        var ended = time.GetUtcNow();
        return (session ?? new StreamSessionResponse(sessionId, publisherDeviceId, "ended", 0, ended, null, ended, ended, null))
            with { Status = "ended", EndedAt = ended };
    }

    /// <summary>A LAN session's code never expires, so rotating keeps the session as it is.</summary>
    public Task<StreamSessionResponse> RotateCodeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (current?.Id != sessionId) throw new InvalidOperationException("The LAN session has ended.");
            return Task.FromResult(current);
        }
    }

    public Task<SessionParticipantsResponse> GetParticipantsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var participants = hub.Participants
            .Where(state => Guid.TryParse(state.ParticipantId, out _))
            .Select(state => new SessionParticipant(
                Guid.Parse(state.ParticipantId), state.Role, "connected", state.AudioSendAllowed, state.CanSendAudio,
                state.CanReceiveAudio, state.AudioMuted, now, null, IsSelf: state.IsPublisher))
            .ToArray();
        return Task.FromResult(new SessionParticipantsResponse(sessionId, hub.SessionMode, participants));
    }

    public async Task<SessionParticipant> SetAudioPermissionAsync(Guid sessionId, Guid participantId, bool canSendAudio, CancellationToken cancellationToken = default)
    {
        var id = participantId.ToString("D");
        await hub.SetAudioPermissionAsync(id, canSendAudio, cancellationToken).ConfigureAwait(false);
        var participants = await GetParticipantsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return participants.Participants.Single(participant => participant.ParticipantId == participantId);
    }
}
