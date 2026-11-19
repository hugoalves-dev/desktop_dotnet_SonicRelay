using System.Net;
using System.Net.NetworkInformation;
using SonicRelay.Windows.Lan.Network;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanEndpointResolverTests
{
    private static LanInterfaceInfo Nic(string name, NetworkInterfaceType type, params string[] addresses) =>
        new(name, type, OperationalStatus.Up, addresses.Select(IPAddress.Parse).ToArray());

    [Fact]
    public void Resolver_OrdersAndCaps()
    {
        var resolver = new LanEndpointResolver(() =>
        [
            Nic("vpn", NetworkInterfaceType.Tunnel, "10.8.0.2"),
            Nic("wifi", NetworkInterfaceType.Wireless80211, "192.168.2.30"),
            Nic("eth", NetworkInterfaceType.Ethernet, "192.168.1.20"),
            Nic("other", NetworkInterfaceType.Ppp, "172.16.0.5"),
        ]);

        var endpoints = resolver.Resolve(47321, "desk");

        Assert.Equal(
            ["192.168.1.20:47321", "192.168.2.30:47321", "172.16.0.5:47321", "desk.local:47321"],
            endpoints);
    }

    [Fact]
    public void Resolver_SkipsLoopbackLinkLocalAndDownInterfaces()
    {
        var resolver = new LanEndpointResolver(() =>
        [
            Nic("lo", NetworkInterfaceType.Loopback, "127.0.0.1", "::1"),
            Nic("eth", NetworkInterfaceType.Ethernet, "169.254.10.10", "fe80::1", "192.168.1.20"),
            new LanInterfaceInfo("down", NetworkInterfaceType.Ethernet, OperationalStatus.Down, [IPAddress.Parse("192.168.9.9")]),
        ]);

        Assert.Equal(["192.168.1.20:47321", "desk.local:47321"], resolver.Resolve(47321, "desk"));
    }

    [Fact]
    public void Resolver_BracketsIpv6()
    {
        var resolver = new LanEndpointResolver(() => [Nic("eth", NetworkInterfaceType.Ethernet, "fd00::20")]);

        Assert.Equal(["[fd00::20]:47321", "desk.local:47321"], resolver.Resolve(47321, "desk"));
    }

    [Fact]
    public void Resolver_ReturnsOnlyHostnameWhenNoAddresses()
    {
        var resolver = new LanEndpointResolver(() => []);

        Assert.Equal(["desk.local:47321"], resolver.Resolve(47321, "desk"));
    }
}
