using System.Net;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Lan;
using SonicRelay.Windows.Lan.Discovery;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Lan.Identity;
using SonicRelay.Windows.Lan.Network;
using SonicRelay.Windows.Lan.Pairing;
using SonicRelay.Windows.Lan.Sessions;
using SonicRelay.Windows.Lan.Trust;
using SonicRelay.Windows.Signaling;

namespace SonicRelay.Windows.Presentation.Sessions;

/// <summary>
/// Everything the publisher needs for LAN sessions (issue #75), composed once per runtime: the
/// identity, trust, pairing windows, signaling hub and the Kestrel host, plus the rule that the
/// host only listens while the connectivity mode allows LAN.
/// </summary>
public sealed class LanServices : IAsyncDisposable
{
    private readonly LanEndpointResolver endpoints = new();
    private readonly string publisherName;
    private readonly SemaphoreSlim applyLock = new(1, 1);
    private readonly Action<string>? log;

    private LanServices(
        LanIdentityStore identityStore,
        LanIdentity identity,
        LanTrustStore trust,
        LanPairingService pairing,
        LanSignalingHub hub,
        LanSignalingHost host,
        ConnectivityPreferenceStore connectivity,
        string publisherName,
        LanDiscoveryService discovery,
        Action<string>? log)
    {
        Discovery = discovery;
        this.log = log;
        IdentityStore = identityStore;
        Identity = identity;
        Trust = trust;
        Pairing = pairing;
        Hub = hub;
        Host = host;
        Connectivity = connectivity;
        this.publisherName = publisherName;
        Sessions = new LanSessionApiClient(hub, identity.PublisherDeviceId);
    }

    public LanIdentityStore IdentityStore { get; }
    public LanIdentity Identity { get; }
    public LanTrustStore Trust { get; }
    public LanPairingService Pairing { get; }
    public LanSignalingHub Hub { get; }
    public LanSignalingHost Host { get; }
    public LanSessionApiClient Sessions { get; }
    public ConnectivityPreferenceStore Connectivity { get; }
    public LanDiscoveryService Discovery { get; }
    public string PublisherName => publisherName;

    public static async Task<LanServices> CreateAsync(
        ITokenProtector protector,
        ConnectivityPreferenceStore connectivity,
        IEnumerable<ISignalingMessageHandler> handlers,
        string publisherName,
        string? storageDirectory = null,
        IPAddress? bindAddress = null,
        Action<string>? log = null,
        ILanServiceAdvertiser? advertiser = null,
        CancellationToken cancellationToken = default)
    {
        var directory = storageDirectory ?? LanStoragePaths.DefaultDirectory;
        var identityStore = new LanIdentityStore(protector, directory);
        var identity = await identityStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        var trust = new LanTrustStore(protector, Path.Combine(directory, LanTrustStore.FileName));
        var pairing = new LanPairingService(identity.Pin, TimeProvider.System);
        var hub = new LanSignalingHub(handlers, trust, identity.PublisherDeviceId.ToString("D"), log: log);
        var host = new LanSignalingHost(identity, pairing, trust, hub,
            new LanHostOptions { BindAddress = bindAddress, PublisherName = publisherName }, log: log);
        var discovery = new LanDiscoveryService(advertiser ?? new MakaretuServiceAdvertiser(), trust);
        return new LanServices(identityStore, identity, trust, pairing, hub, host, connectivity, publisherName, discovery, log);
    }

    /// <summary>The endpoints a phone may use to reach this host on <paramref name="port"/>.</summary>
    public IReadOnlyList<string> EndpointsFor(int port) => endpoints.Resolve(port, Dns.GetHostName());

    /// <summary>
    /// Starts the host when the mode allows LAN (fixed port, else the remembered one with a dynamic
    /// fallback) and stops it otherwise. A bind failure is reported through <see cref="Host"/>.
    /// </summary>
    public async Task ApplyConnectivityAsync(CancellationToken cancellationToken = default)
    {
        await applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ConnectivityModes.AllowsLan(Connectivity.Mode))
            {
                Discovery.Stop();
                if (Host.State != LanHostState.Stopped) await Host.StopAsync().ConfigureAwait(false);
                return;
            }

            var fixedPort = Connectivity.FixedLanPort;
            if (Host.State == LanHostState.Listening && (fixedPort is null || fixedPort == Host.Port)) return;

            await Host.StartAsync(fixedPort ?? Connectivity.LastLanPort, fallbackToDynamic: fixedPort is null, cancellationToken)
                .ConfigureAwait(false);
            if (Host is not { State: LanHostState.Listening, Port: { } port }) return;
            if (port != Connectivity.LastLanPort)
                await Connectivity.SetLastLanPortAsync(port, cancellationToken).ConfigureAwait(false);
            Announce(port);
        }
        finally
        {
            applyLock.Release();
        }
    }

    // mDNS is a convenience: a machine where multicast cannot be used still serves QR/manual
    // pairing and remembered endpoints, so a discovery failure never takes the host down.
    private void Announce(int port)
    {
        try
        {
            Discovery.Start(Identity.Hint, port);
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"mDNS unavailable ({exception.Message}); QR and manual pairing still work.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Discovery.Dispose();
        await Host.DisposeAsync().ConfigureAwait(false);
        await Hub.DisposeAsync().ConfigureAwait(false);
    }
}
