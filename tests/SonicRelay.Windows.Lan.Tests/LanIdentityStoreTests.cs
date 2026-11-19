using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SonicRelay.Windows.Lan.Identity;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanIdentityStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-lan-id-{Guid.NewGuid():N}");

    [Fact]
    public async Task Creates_P256_FiveYearCert()
    {
        var identity = await new LanIdentityStore(new PassThroughProtector(), directory).LoadOrCreateAsync();

        using var key = identity.Certificate.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key!.KeySize);
        Assert.True(identity.Certificate.HasPrivateKey);
        var validity = identity.Certificate.NotAfter - identity.Certificate.NotBefore;
        Assert.InRange(validity.TotalDays, 365 * 5 - 2, 365 * 5 + 3);
    }

    [Fact]
    public async Task Reload_ReturnsSamePinAndDeviceId()
    {
        var first = await new LanIdentityStore(new PassThroughProtector(), directory).LoadOrCreateAsync();
        var second = await new LanIdentityStore(new PassThroughProtector(), directory).LoadOrCreateAsync();

        Assert.Equal(first.Pin, second.Pin);
        Assert.Equal(first.PublisherDeviceId, second.PublisherDeviceId);
        Assert.True(second.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task Reset_ChangesPin()
    {
        var store = new LanIdentityStore(new PassThroughProtector(), directory);
        var first = await store.LoadOrCreateAsync();

        var reset = await store.ResetAsync();

        Assert.NotEqual(first.Pin, reset.Pin);
        Assert.Equal(reset.Pin, (await new LanIdentityStore(new PassThroughProtector(), directory).LoadOrCreateAsync()).Pin);
    }

    [Fact]
    public async Task StoredFile_IsWhatTheProtectorProduced()
    {
        var protector = new XorProtector();
        await new LanIdentityStore(protector, directory).LoadOrCreateAsync();

        Assert.True(protector.ProtectCalls > 0);
        var reloaded = await new LanIdentityStore(protector, directory).LoadOrCreateAsync();
        Assert.True(protector.UnprotectCalls > 0);
        Assert.True(reloaded.Certificate.HasPrivateKey);
    }

    [Fact]
    public void Pin_IsBase64UrlSha256OfDer()
    {
        byte[] der = [1, 2, 3];
        var expected = Convert.ToBase64String(SHA256.HashData(der)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, LanIdentity.ComputePin(der));
        Assert.Equal("A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc-4E", LanIdentity.ComputePin(der));
    }

    [Fact]
    public void Hint_IsSixHexOfSha256OfTheDeviceIdString()
    {
        var id = Guid.Parse("8f14e45f-ceea-467e-9b4a-1f3a2b6c7d8e");
        var expected = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id.ToString("D"))))[..6];

        Assert.Equal(expected, LanIdentity.ComputeHint(id.ToString("D")));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
