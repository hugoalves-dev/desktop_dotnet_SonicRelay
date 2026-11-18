using System.Net;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Lan.Discovery;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Presentation.Sessions;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class LanServicesTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-lansvc-{Guid.NewGuid():N}");
    private readonly FakeAdvertiser advertiser = new();
    private LanServices services = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        services = await LanServices.CreateAsync(
            new NullProtector(),
            new ConnectivityPreferenceStore(Path.Combine(directory, "connectivity.json")),
            handlers: [],
            publisherName: "DESKTOP-01",
            storageDirectory: directory,
            bindAddress: IPAddress.Loopback,
            advertiser: advertiser);
    }

    public async Task DisposeAsync()
    {
        await services.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Auto_StartsHost_AndRemembersPort()
    {
        await services.ApplyConnectivityAsync();

        Assert.Equal(LanHostState.Listening, services.Host.State);
        Assert.Equal(services.Host.Port, services.Connectivity.LastLanPort);
    }

    [Fact]
    public async Task CloudOnly_StopsHost()
    {
        await services.ApplyConnectivityAsync();

        await services.Connectivity.SetModeAsync(ConnectivityModes.CloudOnly);
        await services.ApplyConnectivityAsync();

        Assert.Equal(LanHostState.Stopped, services.Host.State);
    }

    [Fact]
    public async Task Restart_ReusesRememberedPort()
    {
        await services.ApplyConnectivityAsync();
        var port = services.Host.Port;
        await services.Host.StopAsync();

        await services.ApplyConnectivityAsync();

        Assert.Equal(port, services.Host.Port);
    }

    [Fact]
    public async Task Listening_AnnouncesOnMdns_WithTheHostPort()
    {
        await services.ApplyConnectivityAsync();

        Assert.Equal(services.Host.Port, advertiser.AdvertisedPort);
        Assert.Equal($"SonicRelay-{services.Identity.Hint}", advertiser.AdvertisedInstance);
    }

    [Fact]
    public async Task CloudOnly_StopsAnnouncing()
    {
        await services.ApplyConnectivityAsync();
        await services.Connectivity.SetModeAsync(ConnectivityModes.CloudOnly);

        await services.ApplyConnectivityAsync();

        Assert.Equal(1, advertiser.Unadvertised);
    }

    [Fact]
    public async Task MdnsFailure_DoesNotStopTheHost()
    {
        advertiser.Fail = true;

        await services.ApplyConnectivityAsync();

        Assert.Equal(LanHostState.Listening, services.Host.State);
    }

    [Fact]
    public void Endpoints_IncludeTheHostName()
    {
        Assert.EndsWith(".local:47321", services.EndpointsFor(47321)[^1]);
    }

    private sealed class FakeAdvertiser : ILanServiceAdvertiser
    {
        public bool Fail { get; set; }
        public int? AdvertisedPort { get; private set; }
        public string? AdvertisedInstance { get; private set; }
        public int Unadvertised { get; private set; }

        public event Action<DiscoveredService>? ServiceFound { add { } remove { } }
        public event Action<string>? ServiceLost { add { } remove { } }

        public void Advertise(string instance, string serviceType, int port, IReadOnlyDictionary<string, string> txt)
        {
            if (Fail) throw new System.Net.Sockets.SocketException(10049);
            AdvertisedInstance = instance;
            AdvertisedPort = port;
        }

        public void Unadvertise() => Unadvertised++;

        public void Browse(string serviceType)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class NullProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }
}
