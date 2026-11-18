using Makaretu.Dns;

namespace SonicRelay.Windows.Lan.Discovery;

/// <summary>
/// <see cref="ILanServiceAdvertiser"/> over <c>Makaretu.Dns.Multicast</c> (MIT): RFC 6762/6763
/// announce and browse on every interface, IPv4 and IPv6, as the current user. Chosen in the
/// issue #75 spike because SIPSorcery already ships it for ICE mDNS candidates, so LAN discovery
/// adds no dependency (its maintained ".New" fork clashes type-for-type with it).
/// </summary>
public sealed class MakaretuServiceAdvertiser : ILanServiceAdvertiser
{
    private readonly object gate = new();
    private MulticastService? mdns;
    private ServiceDiscovery? discovery;
    private ServiceProfile? profile;
    private readonly HashSet<string> browsed = new(StringComparer.OrdinalIgnoreCase);

    public event Action<DiscoveredService>? ServiceFound;
    public event Action<string>? ServiceLost;

    public void Advertise(string instance, string serviceType, int port, IReadOnlyDictionary<string, string> txt)
    {
        var sd = Ensure();
        var next = new ServiceProfile(instance, serviceType, checked((ushort)port));
        foreach (var (key, value) in txt) next.AddProperty(key, value);
        lock (gate)
        {
            if (profile is not null) sd.Unadvertise(profile);
            profile = next;
        }

        sd.Advertise(next);
        sd.Announce(next);
    }

    public void Unadvertise()
    {
        ServiceDiscovery? sd;
        ServiceProfile? current;
        lock (gate)
        {
            sd = discovery;
            current = profile;
            profile = null;
        }

        if (sd is not null && current is not null) sd.Unadvertise(current);
    }

    public void Browse(string serviceType)
    {
        var sd = Ensure();
        lock (gate) browsed.Add(serviceType);
        sd.QueryServiceInstances(serviceType);
    }

    private ServiceDiscovery Ensure()
    {
        lock (gate)
        {
            if (discovery is not null) return discovery;
            mdns = new MulticastService();
            discovery = new ServiceDiscovery(mdns);
            discovery.ServiceInstanceDiscovered += OnDiscovered;
            discovery.ServiceInstanceShutdown += OnShutdown;
            mdns.Start();
            return discovery;
        }
    }

    private void OnDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        var instance = e.ServiceInstanceName;
        var serviceType = string.Join('.', instance.Labels.Skip(1).Take(2));
        lock (gate)
        {
            if (!browsed.Contains(serviceType)) return;
        }

        var txt = e.Message.Answers.Concat(e.Message.AdditionalRecords)
            .OfType<TXTRecord>()
            .Where(record => record.Name == instance)
            .SelectMany(record => record.Strings)
            .Select(entry => entry.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()[1], StringComparer.OrdinalIgnoreCase);
        ServiceFound?.Invoke(new DiscoveredService(instance.ToString(), txt));
    }

    private void OnShutdown(object? sender, ServiceInstanceShutdownEventArgs e) =>
        ServiceLost?.Invoke(e.ServiceInstanceName.ToString());

    public void Dispose()
    {
        Unadvertise();
        lock (gate)
        {
            discovery?.Dispose();
            mdns?.Stop();
            mdns?.Dispose();
            discovery = null;
            mdns = null;
        }
    }
}
