using SonicRelay.Windows.Lan.Discovery;
using SonicRelay.Windows.Lan.Identity;
using SonicRelay.Windows.Lan.Trust;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanDiscoveryServiceTests : IDisposable
{
    private const string PairedViewer = "11111111-1111-1111-1111-111111111111";
    private readonly string trustPath = Path.Combine(Path.GetTempPath(), $"sonicrelay-disc-{Guid.NewGuid():N}.dat");
    private readonly FakeAdvertiser advertiser = new();
    private readonly LanTrustStore trust;
    private readonly LanDiscoveryService discovery;

    public LanDiscoveryServiceTests()
    {
        trust = new LanTrustStore(new PassThroughProtector(), trustPath);
        discovery = new LanDiscoveryService(advertiser, trust);
    }

    [Fact]
    public void Announce_TxtHasOnlyVersionAndHint()
    {
        discovery.Start("abc123", 47321);

        var advertised = Assert.Single(advertiser.Advertised);
        Assert.Equal("SonicRelay-abc123", advertised.Instance);
        Assert.Equal("_sonicrelay._tcp", advertised.ServiceType);
        Assert.Equal(47321, advertised.Port);
        Assert.Equal(new Dictionary<string, string> { ["v"] = "1", ["id"] = "abc123" }, advertised.Txt);
        Assert.Equal("_sonicrelay-viewer._tcp", Assert.Single(advertiser.Browsed));
    }

    [Fact]
    public async Task Nearby_MarksPairedByHint()
    {
        await trust.AddAsync(PairedViewer, "Pixel", "android");
        discovery.Start("abc123", 47321);

        advertiser.Find("SonicRelay-viewer-1", new() { ["v"] = "1", ["id"] = LanIdentity.ComputeHint(PairedViewer) });
        advertiser.Find("SonicRelay-viewer-2", new() { ["v"] = "1", ["id"] = "ffffff" });

        Assert.Equal(2, discovery.Nearby.Count);
        var paired = discovery.Nearby.Single(viewer => viewer.Paired);
        Assert.Equal("Pixel", paired.Name);
        Assert.False(discovery.Nearby.Single(viewer => viewer.Hint == "ffffff").Paired);
    }

    [Fact]
    public void Lost_RemovesViewer()
    {
        discovery.Start("abc123", 47321);
        advertiser.Find("SonicRelay-viewer-1", new() { ["v"] = "1", ["id"] = "ffffff" });

        advertiser.Lose("SonicRelay-viewer-1");

        Assert.Empty(discovery.Nearby);
    }

    [Fact]
    public void UnknownVersion_IsIgnored()
    {
        discovery.Start("abc123", 47321);

        advertiser.Find("SonicRelay-viewer-9", new() { ["v"] = "2", ["id"] = "ffffff" });

        Assert.Empty(discovery.Nearby);
    }

    [Fact]
    public void Stop_StopsAnnouncing()
    {
        discovery.Start("abc123", 47321);

        discovery.Stop();

        Assert.Equal(1, advertiser.Unadvertised);
        Assert.Empty(discovery.Nearby);
    }

    public void Dispose()
    {
        discovery.Dispose();
        if (File.Exists(trustPath)) File.Delete(trustPath);
    }

    private sealed class FakeAdvertiser : ILanServiceAdvertiser
    {
        public List<(string Instance, string ServiceType, int Port, IReadOnlyDictionary<string, string> Txt)> Advertised { get; } = [];
        public List<string> Browsed { get; } = [];
        public int Unadvertised { get; private set; }

        public event Action<DiscoveredService>? ServiceFound;
        public event Action<string>? ServiceLost;

        public void Advertise(string instance, string serviceType, int port, IReadOnlyDictionary<string, string> txt) =>
            Advertised.Add((instance, serviceType, port, txt));

        public void Unadvertise() => Unadvertised++;

        public void Browse(string serviceType) => Browsed.Add(serviceType);

        public void Find(string instance, Dictionary<string, string> txt) => ServiceFound?.Invoke(new DiscoveredService(instance, txt));

        public void Lose(string instance) => ServiceLost?.Invoke(instance);

        public void Dispose()
        {
        }
    }
}
