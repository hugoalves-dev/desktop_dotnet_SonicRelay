using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SonicRelay.Windows.Lan.Network;

/// <summary>The slice of a network interface the resolver needs; lets tests feed fakes.</summary>
public sealed record LanInterfaceInfo(
    string Name,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> Addresses);

/// <summary>
/// Lists the endpoints a phone may use to reach the LAN host (QR / pairing page): every up,
/// non-loopback, non-link-local unicast address — Ethernet first, then Wi-Fi, other adapters,
/// and tunnels/VPNs last — plus <c>&lt;host&gt;.local</c>. Capped at <see cref="MaxEndpoints"/>
/// addresses so the QR stays scannable. Kestrel itself listens on every interface.
/// </summary>
public sealed class LanEndpointResolver(Func<IEnumerable<LanInterfaceInfo>>? source = null)
{
    public const int MaxEndpoints = 4;

    private readonly Func<IEnumerable<LanInterfaceInfo>> source = source ?? SystemInterfaces;

    public IReadOnlyList<string> Resolve(int port, string hostName)
    {
        var addresses = source()
            .Where(nic => nic.Status == OperationalStatus.Up && nic.Type != NetworkInterfaceType.Loopback)
            .OrderBy(nic => Rank(nic.Type))
            .SelectMany(nic => nic.Addresses.Where(IsUsable))
            .Distinct()
            .Take(MaxEndpoints - 1)
            .Select(address => Format(address, port))
            .ToList();
        addresses.Add($"{hostName}.local:{port}");
        return addresses;
    }

    private static int Rank(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
            or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        NetworkInterfaceType.Tunnel => 3,
        _ => 2,
    };

    private static bool IsUsable(IPAddress address) =>
        !IPAddress.IsLoopback(address)
        && address.AddressFamily switch
        {
            AddressFamily.InterNetwork => !IsIpv4LinkLocal(address),
            AddressFamily.InterNetworkV6 => !address.IsIPv6LinkLocal && !address.IsIPv6Multicast,
            _ => false,
        };

    private static bool IsIpv4LinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static string Format(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private static IEnumerable<LanInterfaceInfo> SystemInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(nic => new LanInterfaceInfo(
            nic.Name,
            nic.NetworkInterfaceType,
            nic.OperationalStatus,
            nic.GetIPProperties().UnicastAddresses.Select(unicast => unicast.Address).ToArray()));
}
