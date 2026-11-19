using SonicRelay.Windows.Lan.Identity;
using SonicRelay.Windows.Lan.Trust;

namespace SonicRelay.Windows.Lan.Discovery;

/// <summary>A DNS-SD instance seen on the link, with its TXT key/values.</summary>
public sealed record DiscoveredService(string Instance, IReadOnlyDictionary<string, string> Txt);

/// <summary>A phone announcing itself on the LAN. <see cref="Paired"/> is a hint only — identity
/// is confirmed when it authenticates over TLS, never from multicast.</summary>
public sealed record NearbyViewer(string Instance, string Hint, bool Paired, string? Name);

/// <summary>mDNS/DNS-SD transport, abstracted so discovery logic is testable without multicast.</summary>
public interface ILanServiceAdvertiser : IDisposable
{
    event Action<DiscoveredService>? ServiceFound;
    event Action<string>? ServiceLost;

    void Advertise(string instance, string serviceType, int port, IReadOnlyDictionary<string, string> txt);
    void Unadvertise();
    void Browse(string serviceType);
}

/// <summary>
/// Announces this publisher as <c>_sonicrelay._tcp</c> and lists phones announcing
/// <c>_sonicrelay-viewer._tcp</c> (issue #75). TXT carries only the protocol version and a
/// non-identifying 6-hex hint; no name, pin or credential is ever multicast.
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    public const string PublisherServiceType = "_sonicrelay._tcp";
    public const string ViewerServiceType = "_sonicrelay-viewer._tcp";
    public const string ProtocolVersion = "1";

    private readonly ILanServiceAdvertiser advertiser;
    private readonly LanTrustStore trust;
    private readonly object gate = new();
    private readonly Dictionary<string, NearbyViewer> nearby = new(StringComparer.OrdinalIgnoreCase);
    private bool running;

    public LanDiscoveryService(ILanServiceAdvertiser advertiser, LanTrustStore trust)
    {
        this.advertiser = advertiser ?? throw new ArgumentNullException(nameof(advertiser));
        this.trust = trust ?? throw new ArgumentNullException(nameof(trust));
        advertiser.ServiceFound += OnFound;
        advertiser.ServiceLost += OnLost;
    }

    public IReadOnlyList<NearbyViewer> Nearby
    {
        get
        {
            lock (gate) return nearby.Values.ToArray();
        }
    }

    public event Action? NearbyChanged;

    public void Start(string hint, int port)
    {
        lock (gate)
        {
            if (running) advertiser.Unadvertise();
            running = true;
        }

        advertiser.Advertise(
            $"SonicRelay-{hint}",
            PublisherServiceType,
            port,
            new Dictionary<string, string> { ["v"] = ProtocolVersion, ["id"] = hint });
        advertiser.Browse(ViewerServiceType);
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            running = false;
            nearby.Clear();
        }

        advertiser.Unadvertise();
        NearbyChanged?.Invoke();
    }

    private void OnFound(DiscoveredService service)
    {
        if (!service.Txt.TryGetValue("v", out var version) || version != ProtocolVersion) return;
        if (!service.Txt.TryGetValue("id", out var hint) || hint.Length != 6) return;

        var paired = trust.Devices.FirstOrDefault(device =>
            !device.Revoked && LanIdentity.ComputeHint(device.ViewerDeviceId) == hint);
        lock (gate)
        {
            if (!running) return;
            nearby[service.Instance] = new NearbyViewer(service.Instance, hint, paired is not null, paired?.Name);
        }

        NearbyChanged?.Invoke();
    }

    private void OnLost(string instance)
    {
        bool removed;
        lock (gate) removed = nearby.Remove(instance);
        if (removed) NearbyChanged?.Invoke();
    }

    public void Dispose()
    {
        advertiser.ServiceFound -= OnFound;
        advertiser.ServiceLost -= OnLost;
        Stop();
        advertiser.Dispose();
    }
}
