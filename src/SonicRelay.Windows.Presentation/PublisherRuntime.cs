using SonicRelay.Windows.ApiClient.DeviceIdentity;
using SonicRelay.Windows.ApiClient.Pairing;
using SonicRelay.Windows.ApiClient.Sessions;
using SonicRelay.Windows.ApiClient.Settings;
using SonicRelay.Windows.ApiClient.WebRtc;
using SonicRelay.Windows.Audio;
using SonicRelay.Windows.Core.Audio;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Diagnostics;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Core.Storage.DeviceIdentity;
using SonicRelay.Windows.Presentation.Pairing;
using SonicRelay.Windows.Presentation.Sessions;
using SonicRelay.Windows.Signaling;
using SonicRelay.Windows.WebRtc;

namespace SonicRelay.Windows.Presentation;

public sealed class PublisherRuntime : IAsyncDisposable
{
    // Google's public STUN server is a development-only fallback for when the
    // backend ICE endpoint is unreachable; it must never be relied on in a
    // release build.
#if DEBUG
    private const bool AllowGoogleStunDevFallback = true;
#else
    private const bool AllowGoogleStunDevFallback = false;
#endif

    private readonly HttpClient httpClient;
    private readonly IPeerConnectionManager peers;
    private readonly IWebRtcPublisher webRtcPublisher;
    private readonly WebRtcAudioBridge audioBridge;
    private readonly AudioPlaybackService? playback;
    private readonly DeviceIdentitySession deviceIdentitySession;
    private readonly SystemNetworkAvailability networkAvailability;
    private readonly ResourceUsageSampler resourceUsageSampler;
    private string? lastLoggedState;
    private bool hadActiveSession;

    private PublisherRuntime(
        HttpClient httpClient,
        PublisherWorkflow workflow,
        Uri backendBaseUrl,
        IPeerConnectionManager peers,
        IWebRtcPublisher webRtcPublisher,
        WebRtcAudioBridge audioBridge,
        AudioPlaybackService? playback,
        RelayPreferenceStore relayPreference,
        AudioQualityStore audioQuality,
        IAudioCaptureService audioCapture,
        AudioOutputPreferenceStore audioOutput,
        DeviceIdentitySession deviceIdentitySession,
        DiagnosticLog diagnosticLog,
        SystemNetworkAvailability networkAvailability,
        ResourceUsageSampler resourceUsageSampler,
        LanServices lan,
        SessionHostRouter sessions,
        CloudAvailability cloud)
    {
        Lan = lan;
        Sessions = sessions;
        this.cloud = cloud;
        // One QR for both transports: the LAN block always, the cloud challenge when one is open.
        LanPairing = new LanPairingViewModel(lan, new PairingQrCodeService(), () =>
            Pairing?.Challenge is { } challenge ? new Lan.Pairing.CloudPairingBlock(challenge.ChallengeId, challenge.Code) : null);
        this.networkAvailability = networkAvailability;
        this.resourceUsageSampler = resourceUsageSampler;
        this.httpClient = httpClient;
        this.peers = peers;
        this.webRtcPublisher = webRtcPublisher;
        this.audioBridge = audioBridge;
        this.playback = playback;
        this.deviceIdentitySession = deviceIdentitySession;
        Workflow = workflow;
        BackendBaseUrl = backendBaseUrl;
        RelayPreference = relayPreference;
        AudioQuality = audioQuality;
        AudioCapture = audioCapture;
        AudioOutput = audioOutput;
        DiagnosticLog = diagnosticLog;
        ReportExporter = new DiagnosticReportExporter();
        Workflow.StateChanged += OnWorkflowStateChanged;
        Workflow.LogAppended += OnWorkflowLogAppended;
        _ = WriteDiagnosticAsync("runtime", "Publisher runtime configured.", new Dictionary<string, string>
        {
            ["backend"] = DiagnosticRedactor.BackendHost(backendBaseUrl)
        });
    }

    public PublisherWorkflow Workflow { get; }
    public Uri BackendBaseUrl { get; }
    public RelayPreferenceStore RelayPreference { get; }
    public AudioQualityStore AudioQuality { get; }
    public IAudioCaptureService AudioCapture { get; }
    public AudioOutputPreferenceStore AudioOutput { get; }
    public DiagnosticLog DiagnosticLog { get; }
    public DiagnosticReportExporter ReportExporter { get; }
    public IWebRtcPublisher WebRtcPublisher => webRtcPublisher;
    public PairingViewModel? Pairing { get; private set; }

    /// <summary>LAN identity, trust, pairing windows, hub and host (issue #75).</summary>
    public LanServices Lan { get; }

    /// <summary>Chooses Cloud/LAN/Dual per session; also the source of each viewer's signaling origin.</summary>
    public SessionHostRouter Sessions { get; }

    public ConnectivityPreferenceStore Connectivity => Lan.Connectivity;

    /// <summary>Offline pairing surface (QR v2, manual code, LAN-paired phones).</summary>
    public LanPairingViewModel LanPairing { get; }

    /// <summary>Whether this device holds a working cloud identity (false offline or in lanOnly).</summary>
    public bool CloudAvailable => cloud.IdentityReady;

    private readonly CloudAvailability cloud;
    private bool lanStarted;
    public event Action? PairingChanged;

    /// <summary>Whether this platform composition can play back what other participants send.</summary>
    public bool SupportsTwoWayAudio => playback is not null;

    private IRelaySettingsApiClient? relaySettingsApi;

    /// <summary>Backend relay-preference sync (shared across this device's pairings).</summary>
    public IRelaySettingsApiClient RelaySettingsApi =>
        relaySettingsApi ??= new RelaySettingsApiClient(httpClient, deviceIdentitySession);

    /// <summary>
    /// Composes the shared publisher runtime for one backend. The platform shell
    /// supplies its capture implementation (WASAPI loopback on Windows, PipeWire on
    /// Linux — issue #32) and, optionally, its own device-credential store and
    /// audio-output preference store (Linux would use Secret Service instead of
    /// DPAPI); omitting either keeps the existing Windows-default behavior.
    /// <paramref name="relayPreferenceOverride"/> exists for tests — the default on-disk
    /// preferences file is otherwise always used. <paramref name="deviceIdentityApiClientOverride"/>
    /// also exists only for tests: it is the narrowest seam that lets a test drive a
    /// device-identity bootstrap through to a genuine success without a real backend,
    /// since <see cref="DeviceIdentitySession"/> otherwise always talks over
    /// <paramref name="backendBaseUrl"/>.
    /// </summary>
    public static PublisherRuntime Create(
        Uri backendBaseUrl,
        IAudioCaptureService audioCapture,
        IDeviceCredentialStore? credentialStoreOverride = null,
        AudioOutputPreferenceStore? audioOutputPreferenceOverride = null,
        RelayPreferenceStore? relayPreferenceOverride = null,
        IDeviceIdentityApiClient? deviceIdentityApiClientOverride = null,
        IAudioPlaybackBackend? playbackBackend = null,
        LanServices? lanOverride = null)
    {
        ArgumentNullException.ThrowIfNull(backendBaseUrl);
        ArgumentNullException.ThrowIfNull(audioCapture);
        if (!backendBaseUrl.IsAbsoluteUri || backendBaseUrl.Scheme is not ("http" or "https"))
            throw new ConfigurationValidationException("Backend URL must be an absolute HTTP or HTTPS URL.");

        var normalized = backendBaseUrl.AbsoluteUri.EndsWith('/') ? backendBaseUrl : new Uri(backendBaseUrl.AbsoluteUri + "/");
        // The backend hosts the signaling WebSocket at /ws/signaling; deriving it from the backend base
        // keeps a single configured address while matching the server route (a bare /signaling returns 404).
        var signalingUrl = new Uri(normalized, "ws/signaling");
        var configuration = new PublisherConfiguration(normalized, signalingUrl, 4);
        configuration.Validate();
        var http = new HttpClient { BaseAddress = normalized, Timeout = TimeSpan.FromSeconds(30) };
        var credentialStore = credentialStoreOverride ?? new UserScopedDeviceCredentialStore();
        var deviceIdentitySession = new DeviceIdentitySession(
            deviceIdentityApiClientOverride ?? new DeviceIdentityApiClient(http),
            credentialStore,
            Environment.MachineName);

        var diagnosticLog = new DiagnosticLog();
        // Samples CPU/memory/network throughout the run (not just while streaming) so a
        // post-mortem has a baseline to compare a suspicious reading against — "was this normal
        // for this machine, or a spike?" needs both.
        var resourceUsageSampler = new ResourceUsageSampler(new ProcessRawResourceCounterSource(), diagnosticLog);
        // The WebRTC publisher needs the signaling client to send offers/candidates,
        // but the client takes its handlers up front — register the publisher through
        // a composite handler after both exist.
        var signalingHandlers = new CompositeSignalingMessageHandler();
        // The network gate keeps a machine with no route at all from spending its reconnect
        // budget on attempts that cannot succeed, and the journal records each recovery step
        // with the generation that produced it — the two things a post-mortem of a failed
        // reconnect needs and the log could not previously answer.
        var networkAvailability = new SystemNetworkAvailability();
        var signaling = new SignalingClient(configuration, deviceIdentitySession, [signalingHandlers],
            networkAvailability, new DiagnosticRecoveryJournal(diagnosticLog));
        var relayPreference = relayPreferenceOverride ?? new RelayPreferenceStore();
        // LAN sessions (issue #75): the identity is loaded here because the hub's participant id
        // and the router must exist before the WebRTC publisher that sends through them. The
        // host itself only starts from StartLanAsync, so composing a runtime opens no port.
        var lan = lanOverride ?? LanServices.CreateAsync(
                TokenProtectors.CreateDefault(),
                new ConnectivityPreferenceStore(),
                [signalingHandlers],
                Environment.MachineName,
                log: message => LogLan(diagnosticLog, message))
            .GetAwaiter().GetResult();
        var cloud = new CloudAvailability();
        var sessions = new SessionHostRouter(
            new SessionApiClient(http, deviceIdentitySession),
            signaling,
            lan.Sessions,
            lan.Hub,
            () => lan.Connectivity.Mode,
            () => cloud.IdentityReady && networkAvailability.IsAvailable,
            log: message => LogLan(diagnosticLog, message),
            ensureCloud: token => cloud.Recover?.Invoke(token) ?? Task.FromResult(false));
        // ICE servers (including short-lived TURN credentials) come from the
        // backend, which serves the SonicRelay coturn deployment. The public
        // Google STUN fallback is a debug-build-only convenience for when the
        // backend request fails; release builds get an empty ICE server list
        // instead of silently depending on Google's STUN server. The relay-mode/coturn
        // preferences are per-device local settings (issue #26 follow-up), read live so a
        // Settings change applies to the next ICE fetch without recreating the runtime.
        var iceServersProvider = new BackendIceServersProvider(
            new WebRtcApiClient(http, deviceIdentitySession),
            () => new RelayPreferenceSnapshot(relayPreference.RelayMode, relayPreference.CoturnUrlOverride),
            allowGoogleStunDevFallback: AllowGoogleStunDevFallback);
        var audioQuality = new AudioQualityStore();
        // The session mode is chosen when the session is created and read here when each peer
        // connection is built, because a connection's audio direction is fixed at construction:
        // a `sendonly` m-line cannot later accept a peer's own audio track.
        var sessionMode = new SessionModeState();
        var peers = new PeerConnectionManager(
            new SipSorceryPeerConnectionFactory(
                iceServersProvider,
                () => relayPreference.ForceRelay,
                () => audioQuality.CurrentProfile,
                () => sessionMode.IsDuplex ? WebRtcAudioDirection.SendRecv : WebRtcAudioDirection.SendOnly,
                // LAN-signaled viewers get host candidates only; cloud viewers keep STUN/TURN.
                viewerId => PeerIcePolicy.For(sessions.OriginOf(viewerId), relayPreference.ForceRelay)),
            new WebRtcPublisherOptions());
        var webRtcPublisher = new WebRtcPublisher(sessions, peers);
        signalingHandlers.Register(webRtcPublisher);

        signaling.ReconnectAttempting += attempt => LogReconnectAttempt(diagnosticLog, attempt);
        signaling.Closed += reason => LogSignalingClosed(diagnosticLog, reason);
        webRtcPublisher.IceRestartRequested += viewerId => LogIceRestart(diagnosticLog, viewerId);
        webRtcPublisher.PeerRebuildRequested += viewerId => LogPeerRebuild(diagnosticLog, viewerId);

        var audio = audioCapture;
        var audioOutput = audioOutputPreferenceOverride ?? new AudioOutputPreferenceStore();
        // Restore the previously selected output device (null = system default).
        audio.SelectOutputDevice(audioOutput.SelectedDeviceId);
        var audioBridge = new WebRtcAudioBridge(audio, webRtcPublisher);

        // Capture is the same system-output mix in both modes, so playback is the only thing
        // two-way audio adds — and the only thing a platform can be missing.
        var playback = playbackBackend is null ? null : new AudioPlaybackService(playbackBackend);
        if (playback is not null)
        {
            webRtcPublisher.RemoteAudioFrameReceived += (_, frame) =>
                playback.Play(frame.Samples, frame.SampleRate, frame.ChannelCount);
        }

        var workflow = new PublisherWorkflow(
            deviceIdentitySession,
            credentialStore,
            sessions,
            sessions,
            audio,
            new PairingApiClient(http, deviceIdentitySession),
            webRtcPublisher,
            playback,
            mode => sessionMode.Mode = mode);
        // Surface WebRTC recovery events in the technical console too — the on-disk
        // diagnostic log is invisible in the UI and these are exactly the lines a user
        // debugging a dropped viewer needs to see.
        webRtcPublisher.IceRestartRequested += _ =>
            workflow.LogActivity("WebRTC: ICE restart requested for a reconnected viewer.");
        webRtcPublisher.PeerRebuildRequested += _ =>
            workflow.LogActivity("WebRTC: rebuilding peer connection after repeated ICE restart failures.");
        var runtime = new PublisherRuntime(
            http,
            workflow,
            normalized,
            peers,
            webRtcPublisher,
            audioBridge,
            playback,
            relayPreference,
            audioQuality,
            audio,
            audioOutput,
            deviceIdentitySession,
            diagnosticLog,
            networkAvailability,
            resourceUsageSampler,
            lan,
            sessions,
            cloud);
        // An `auto` start without RelayControl leaves the cloud unusable; this lets the next
        // session bring it back once the API answers, without restarting the app.
        cloud.Recover = async token =>
        {
            if (cloud.IdentityReady) return true;
            if (!networkAvailability.IsAvailable) return false;
            await deviceIdentitySession.GetAccessTokenAsync(cancellationToken: token).ConfigureAwait(false);
            var stored = await credentialStore.LoadAsync(token).ConfigureAwait(false);
            if (!stored.Succeeded || stored.Credential is null) return false;
            runtime.OnCloudRecovered(stored.Credential.DeviceId);
            return true;
        };
        return runtime;
    }

    private void OnCloudRecovered(Guid cloudDeviceId)
    {
        cloud.IdentityReady = true;
        LogLan(DiagnosticLog, "RelayControl is reachable again; new sessions include the cloud.");
        SetPairing(new PairingViewModel(
            new PairingApiClient(httpClient, deviceIdentitySession, () => deviceIdentitySession.CurrentDeviceId),
            new PairingQrCodeService(),
            cloudDeviceId));
    }

    /// <summary>
    /// Bootstraps the cloud identity unless the mode is <c>lanOnly</c>; when the cloud is not
    /// usable and the mode allows LAN, the publisher becomes ready with its LAN identity instead,
    /// so an offline machine can still host LAN sessions (issue #75).
    /// </summary>
    public async Task InitializeDeviceIdentityAsync(CancellationToken cancellationToken = default)
    {
        SetPairing(null);
        cloud.IdentityReady = false;
        var mode = Connectivity.Mode;
        if (mode != ConnectivityModes.LanOnly)
        {
            await Workflow.InitializeDeviceIdentityAsync(cancellationToken);
            cloud.IdentityReady = Workflow.State is { IsAuthenticated: true, ErrorMessage: null };
        }

        if (!cloud.IdentityReady && ConnectivityModes.AllowsLan(mode))
        {
            await Workflow.InitializeLocalIdentityAsync(Lan.Identity.PublisherDeviceId, cancellationToken);
            return;
        }

        if (!cloud.IdentityReady || Workflow.State.DeviceId is not { } deviceId)
        {
            return;
        }

        SetPairing(new PairingViewModel(
            new PairingApiClient(httpClient, deviceIdentitySession, () => deviceIdentitySession.CurrentDeviceId),
            new PairingQrCodeService(),
            deviceId));
    }

    /// <summary>
    /// Starts (or stops) the LAN host for the current connectivity mode and keeps following mode
    /// changes. Called by the app shell once; never by composition, so tests open no ports.
    /// </summary>
    public Task StartLanAsync(CancellationToken cancellationToken = default)
    {
        if (!lanStarted)
        {
            lanStarted = true;
            Connectivity.Changed += () => _ = Lan.ApplyConnectivityAsync();
        }

        return Lan.ApplyConnectivityAsync(cancellationToken);
    }

    private void SetPairing(PairingViewModel? value)
    {
        if (ReferenceEquals(Pairing, value)) return;
        Pairing = value;
        PairingChanged?.Invoke();
    }

    private void OnWorkflowStateChanged(PublisherSnapshot state)
    {
        // The publisher closes signaling locally on session end, so it never
        // receives its own session.ended; tear down peer connections here when
        // the active session clears.
        var hasSession = state.SessionId is not null;
        if (hadActiveSession && !hasSession)
        {
            _ = peers.RemoveAllAsync();
        }
        hadActiveSession = hasSession;

        var signature = $"{state.IsAuthenticated}|{state.SignalingState}|{state.AudioState}|{state.ViewerCount}|{state.ErrorMessage}";
        if (signature == lastLoggedState) return;
        lastLoggedState = signature;
        _ = WriteDiagnosticAsync("publisher-state", state.ErrorMessage ?? "Publisher status changed.", new Dictionary<string, string>
        {
            ["authenticated"] = state.IsAuthenticated.ToString(),
            ["signaling"] = state.SignalingState.ToString(),
            ["audio"] = state.AudioState.ToString(),
            ["viewerCount"] = state.ViewerCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
    }

    /// <summary>
    /// Persists every workflow activity line, independent of <see cref="OnWorkflowStateChanged"/>'s
    /// state-signature dedup. That dedup exists to keep "publisher-state" from spamming a line per
    /// no-op update, but it also means a message like "Session ended." — which changes only
    /// SessionId, a field the signature does not track — never reached the on-disk log even though
    /// it was right there in the UI's ActivityLog. This is the only place that gap is closed, so a
    /// post-mortem of a dropped connection always has the actual reason, not just a state trail
    /// that quietly stops.
    /// </summary>
    private void OnWorkflowLogAppended(string message) =>
        _ = WriteDiagnosticAsync("workflow", message, NoProperties);

    private async Task WriteDiagnosticAsync(string category, string message, IReadOnlyDictionary<string, string> properties)
    {
        try
        {
            await DiagnosticLog.WriteAsync(category, message, properties);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Diagnostics must never interrupt publisher operation.
        }
    }

    private static readonly IReadOnlyDictionary<string, string> NoProperties = new Dictionary<string, string>();

    private static async void LogLan(DiagnosticLog log, string message)
    {
        try { await log.WriteAsync("lan", message, NoProperties); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogReconnectAttempt(DiagnosticLog log, int attempt)
    {
        try { await log.WriteAsync("reconnect-attempt", $"Signaling reconnect attempt {attempt + 1}.", NoProperties); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogSignalingClosed(DiagnosticLog log, SignalingCloseReason reason)
    {
        try
        {
            await log.WriteAsync("signaling-closed", "Signaling connection closed.", new Dictionary<string, string>
            {
                ["reason"] = reason.ToString()
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogIceRestart(DiagnosticLog log, string viewerId)
    {
        try
        {
            await log.WriteAsync("ice-restart", "ICE restart requested for a reconnected viewer.", new Dictionary<string, string>
            {
                ["viewerId"] = viewerId
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static async void LogPeerRebuild(DiagnosticLog log, string viewerId)
    {
        try
        {
            await log.WriteAsync("peer-rebuild", "Peer connection rebuilt after repeated ICE restart failures.", new Dictionary<string, string>
            {
                ["viewerId"] = viewerId
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Workflow.StateChanged -= OnWorkflowStateChanged;
        Workflow.LogAppended -= OnWorkflowLogAppended;
        // Stop the audio pump before the workflow disposes the capture service,
        // then tear down the WebRTC publisher (which disposes the peer manager).
        await audioBridge.DisposeAsync();
        await Workflow.DisposeAsync();
        // After the workflow, which stops playback while the peer connections it belongs to
        // are still up; disposing it first would leave the receive path writing to a
        // disposed device.
        if (playback is not null) await playback.DisposeAsync();
        await webRtcPublisher.DisposeAsync();
        LanPairing.Dispose();
        await Lan.DisposeAsync();
        await resourceUsageSampler.DisposeAsync();
        deviceIdentitySession.Dispose();
        networkAvailability.Dispose();
        httpClient.Dispose();
        DiagnosticLog.Dispose();
    }
}

/// <summary>Mutable flag shared by the runtime and the session router.</summary>
public sealed class CloudAvailability
{
    public bool IdentityReady { get; set; }

    /// <summary>Tries to restore a cloud identity that was unavailable; true when usable.</summary>
    public Func<CancellationToken, Task<bool>>? Recover { get; set; }
}
