namespace SonicRelay.Windows.Presentation;

/// <summary>The slice of <see cref="PublisherWorkflow"/> that <see cref="SessionAutoStarter"/> drives.</summary>
public interface IPublisherSessionControl
{
    PublisherSnapshot State { get; }
    event Action<PublisherSnapshot>? StateChanged;
    Task CreateSessionAsync(bool duplex = false, CancellationToken cancellationToken = default);
    Task StartAudioAsync(CancellationToken cancellationToken = default);
    Task RotateCodeAsync(CancellationToken cancellationToken = default);
    Task EndSessionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// "Start streaming when the app opens" (flutter_mobile-web_SonicRelay#65): once the device
/// identity is ready it creates a broadcast session and starts audio, then keeps a session
/// available for a paired viewer with auto-connect:
/// <list type="bullet">
/// <item>a session still waiting for its first viewer expires with its join code, so the code
/// is rotated one minute before it expires while nobody is listening;</item>
/// <item>a session that disappears without the user ending it is recreated with a bounded
/// backoff (5 s, 30 s, 2 min, then every 5 min) — never a tight loop;</item>
/// <item>once the user ends the session, nothing is recreated until the next launch.</item>
/// </list>
/// Every workflow command reports failure through its state rather than by throwing, so the
/// outcome of each step is read back from <see cref="IPublisherSessionControl.State"/>.
/// </summary>
public sealed class SessionAutoStarter : IDisposable
{
    private static readonly TimeSpan RotationMargin = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ViewerRecheck = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RecreateDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private readonly IPublisherSessionControl workflow;
    private readonly Func<bool> enabled;
    private readonly TimeProvider time;
    private ITimer? rotationTimer;
    private ITimer? recreateTimer;
    private Guid? managedSessionId;
    private int failedCreates;
    private bool started;
    private bool userEnded;
    private bool releasing;

    public SessionAutoStarter(IPublisherSessionControl workflow, Func<bool> enabled, TimeProvider time)
    {
        this.workflow = workflow;
        this.enabled = enabled;
        this.time = time;
        workflow.StateChanged += OnStateChanged;
    }

    public async Task OnIdentityReadyAsync()
    {
        if (started || userEnded || !enabled()) return;
        var state = workflow.State;
        if (!state.IsAuthenticated || state.SessionId is not null) return;
        started = true;
        await StartSessionAsync();
    }

    /// <summary>Call before the user's own "End session" runs, so it is not mistaken for a drop.</summary>
    public void NotifyUserEndedSession()
    {
        userEnded = true;
        managedSessionId = null;
        CancelTimers();
    }

    public void Dispose()
    {
        workflow.StateChanged -= OnStateChanged;
        CancelTimers();
    }

    private async Task StartSessionAsync()
    {
        if (userEnded) return;
        await workflow.CreateSessionAsync();
        if (workflow.State.SessionId is not { } sessionId)
        {
            failedCreates++;
            ScheduleRecreate();
            return;
        }

        failedCreates = 0;
        managedSessionId = sessionId;
        // A failed audio start leaves its error on screen and the session in place: retrying
        // a missing capture device would only repeat the same failure.
        await workflow.StartAudioAsync();
        ScheduleRotation();
    }

    private void ScheduleRotation()
    {
        rotationTimer?.Dispose();
        if (workflow.State.CodeExpiresAt is not { } expiresAt) return;
        var due = expiresAt - RotationMargin - time.GetUtcNow();
        rotationTimer = time.CreateTimer(_ => _ = RotateAsync(), null,
            due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }

    private async Task RotateAsync()
    {
        var state = workflow.State;
        if (userEnded || state.SessionId is null || state.SessionId != managedSessionId) return;
        if (state.ViewerCount > 0)
        {
            // An active session outlives its code; check again in case the viewer leaves.
            rotationTimer?.Dispose();
            rotationTimer = time.CreateTimer(_ => _ = RotateAsync(), null, ViewerRecheck, Timeout.InfiniteTimeSpan);
            return;
        }

        var previousExpiry = state.CodeExpiresAt;
        await workflow.RotateCodeAsync();
        if (workflow.State.CodeExpiresAt != previousExpiry)
        {
            ScheduleRotation();
            return;
        }

        // The backend refused (typically 409: the session already ended). Release it locally
        // and let the drop handling create a fresh one.
        releasing = true;
        try { await workflow.EndSessionAsync(); }
        finally { releasing = false; }
        HandleDrop();
    }

    private void OnStateChanged(PublisherSnapshot state)
    {
        if (releasing || managedSessionId is null || state.SessionId == managedSessionId) return;
        HandleDrop();
    }

    private void HandleDrop()
    {
        if (userEnded || managedSessionId is null) return;
        managedSessionId = null;
        rotationTimer?.Dispose();
        rotationTimer = null;
        ScheduleRecreate();
    }

    private void ScheduleRecreate()
    {
        recreateTimer?.Dispose();
        var delay = RecreateDelays[Math.Min(failedCreates, RecreateDelays.Length - 1)];
        recreateTimer = time.CreateTimer(_ => _ = StartSessionAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void CancelTimers()
    {
        rotationTimer?.Dispose();
        recreateTimer?.Dispose();
        rotationTimer = null;
        recreateTimer = null;
    }
}
