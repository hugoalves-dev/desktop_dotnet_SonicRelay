using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class SessionAutoStarterTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Does_nothing_when_disabled()
    {
        var (control, _, starter) = Create(enabled: false);

        await starter.OnIdentityReadyAsync();

        Assert.Equal(0, control.Creates);
    }

    [Fact]
    public async Task Does_nothing_without_device_identity()
    {
        var (control, _, starter) = Create();
        control.Set(control.State with { IsAuthenticated = false });

        await starter.OnIdentityReadyAsync();

        Assert.Equal(0, control.Creates);
    }

    [Fact]
    public async Task Does_nothing_when_a_session_already_exists()
    {
        var (control, _, starter) = Create();
        control.Set(control.State with { SessionId = Guid.NewGuid() });

        await starter.OnIdentityReadyAsync();

        Assert.Equal(0, control.Creates);
    }

    [Fact]
    public async Task Creates_a_session_and_starts_audio_once()
    {
        var (control, _, starter) = Create();

        await starter.OnIdentityReadyAsync();
        await starter.OnIdentityReadyAsync();

        Assert.Equal(1, control.Creates);
        Assert.Equal(1, control.AudioStarts);
    }

    [Fact]
    public async Task Rotates_the_code_one_minute_before_expiry_while_nobody_listens()
    {
        var (control, time, starter) = Create();
        await starter.OnIdentityReadyAsync();

        time.Advance(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(59));
        Assert.Equal(0, control.Rotations);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, control.Rotations);

        // The renewed code is rotated again before its own expiry.
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(2, control.Rotations);
    }

    [Fact]
    public async Task Does_not_rotate_while_a_viewer_is_connected()
    {
        var (control, time, starter) = Create();
        await starter.OnIdentityReadyAsync();
        control.Set(control.State with { ViewerCount = 1 });

        time.Advance(TimeSpan.FromMinutes(9));

        Assert.Equal(0, control.Rotations);
    }

    [Fact]
    public async Task A_failed_rotation_releases_the_session_and_recreates_it()
    {
        var (control, time, starter) = Create();
        await starter.OnIdentityReadyAsync();
        control.RotationFails = true;

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(1, control.Ends);
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(2, control.Creates);
    }

    [Fact]
    public async Task A_session_that_disappears_is_recreated_with_backoff()
    {
        var (control, time, starter) = Create();
        await starter.OnIdentityReadyAsync();
        control.CreateFails = true;

        control.DropSession();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(2, control.Creates);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(2, control.Creates);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, control.Creates);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(4, control.Creates);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(5, control.Creates);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(6, control.Creates);

        control.CreateFails = false;
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(7, control.Creates);
        Assert.NotNull(control.State.SessionId);

        // Success resets the backoff: the next drop is retried after 5 s again.
        control.DropSession();
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(8, control.Creates);
    }

    [Fact]
    public async Task A_session_the_user_ended_is_not_recreated()
    {
        var (control, time, starter) = Create();
        await starter.OnIdentityReadyAsync();

        starter.NotifyUserEndedSession();
        control.DropSession();
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal(1, control.Creates);
    }

    [Fact]
    public async Task A_failed_audio_start_keeps_the_session_and_does_not_retry()
    {
        var (control, time, starter) = Create();
        control.AudioFails = true;

        await starter.OnIdentityReadyAsync();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(1, control.Creates);
        Assert.NotNull(control.State.SessionId);
    }

    private static (FakeControl, ManualTimeProvider, SessionAutoStarter) Create(bool enabled = true)
    {
        var time = new ManualTimeProvider(Start);
        var control = new FakeControl(time);
        return (control, time, new SessionAutoStarter(control, () => enabled, time));
    }

    private sealed class FakeControl(ManualTimeProvider time) : IPublisherSessionControl
    {
        public PublisherSnapshot State { get; private set; } = new() { IsAuthenticated = true, DeviceId = Guid.NewGuid() };
        public event Action<PublisherSnapshot>? StateChanged;
        public int Creates { get; private set; }
        public int AudioStarts { get; private set; }
        public int Rotations { get; private set; }
        public int Ends { get; private set; }
        public bool CreateFails { get; set; }
        public bool AudioFails { get; set; }
        public bool RotationFails { get; set; }

        public void Set(PublisherSnapshot state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        public void DropSession() => Set(State with { SessionId = null, SessionCode = null, CodeExpiresAt = null });

        public Task CreateSessionAsync(bool duplex = false, CancellationToken cancellationToken = default)
        {
            Creates++;
            Set(CreateFails
                ? State with { ErrorMessage = "backend down" }
                : State with
                {
                    SessionId = Guid.NewGuid(),
                    SessionCode = "ABC123",
                    CodeExpiresAt = time.GetUtcNow().AddMinutes(10),
                    SignalingState = SignalingConnectionState.Connected,
                });
            return Task.CompletedTask;
        }

        public Task StartAudioAsync(CancellationToken cancellationToken = default)
        {
            AudioStarts++;
            if (AudioFails) Set(State with { ErrorMessage = "no device" });
            return Task.CompletedTask;
        }

        public Task RotateCodeAsync(CancellationToken cancellationToken = default)
        {
            Rotations++;
            Set(RotationFails
                ? State with { ErrorMessage = "409" }
                : State with { CodeExpiresAt = time.GetUtcNow().AddMinutes(10) });
            return Task.CompletedTask;
        }

        public Task EndSessionAsync(CancellationToken cancellationToken = default)
        {
            Ends++;
            DropSession();
            return Task.CompletedTask;
        }
    }

    /// <summary>A clock that only moves when the test advances it, firing due timers in order.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = now + by;
            while (true)
            {
                var next = timers.Where(t => t.DueAt is { } at && at <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null) break;
                now = next.DueAt!.Value;
                next.Fire();
            }
            now = target;
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (!owner.timers.Contains(this)) owner.timers.Add(this);
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
                return true;
            }

            public void Fire()
            {
                DueAt = null;
                callback(state);
            }

            public void Dispose() => owner.timers.Remove(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
