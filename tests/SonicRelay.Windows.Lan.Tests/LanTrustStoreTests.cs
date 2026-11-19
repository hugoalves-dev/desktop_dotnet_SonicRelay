using System.Text;
using SonicRelay.Windows.Lan.Trust;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanTrustStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"sonicrelay-trust-{Guid.NewGuid():N}.dat");

    private LanTrustStore Create() => new(new PassThroughProtector(), path);

    [Fact]
    public async Task Add_ReturnsCredential_StoresOnlyHash()
    {
        var credential = await Create().AddAsync("viewer-1", "Pixel", "android");

        Assert.Equal(43, credential.Length); // 256-bit base64url
        var onDisk = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(credential, onDisk);
        Assert.Contains("viewer-1", onDisk);
    }

    [Fact]
    public async Task Authenticate_RightCredential()
    {
        var store = Create();
        var credential = await store.AddAsync("viewer-1", "Pixel", "android");

        var device = new LanTrustStore(new PassThroughProtector(), path).Authenticate("viewer-1", credential);

        Assert.NotNull(device);
        Assert.Equal("Pixel", device!.Name);
    }

    [Fact]
    public async Task Authenticate_Wrong_ReturnsNull()
    {
        var store = Create();
        await store.AddAsync("viewer-1", "Pixel", "android");

        Assert.Null(store.Authenticate("viewer-1", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.Null(store.Authenticate("viewer-2", "anything"));
    }

    [Fact]
    public async Task Revoked_ReturnsNull_AndRaisesEvent()
    {
        var store = Create();
        var credential = await store.AddAsync("viewer-1", "Pixel", "android");
        string? revoked = null;
        store.Revoked += id => revoked = id;

        await store.RevokeAsync("viewer-1");

        Assert.Equal("viewer-1", revoked);
        Assert.Null(store.Authenticate("viewer-1", credential));
        Assert.True(Assert.Single(new LanTrustStore(new PassThroughProtector(), path).Devices).Revoked);
    }

    [Fact]
    public async Task Repair_ReplacesOldCredential()
    {
        var store = Create();
        var old = await store.AddAsync("viewer-1", "Pixel", "android");
        await store.RevokeAsync("viewer-1");

        var renewed = await store.AddAsync("viewer-1", "Pixel 9", "android");

        Assert.Null(store.Authenticate("viewer-1", old));
        Assert.NotNull(store.Authenticate("viewer-1", renewed));
        Assert.Equal("Pixel 9", Assert.Single(store.Devices).Name);
    }

    [Fact]
    public async Task Persists_Protected()
    {
        var protector = new XorProtector();
        var credential = await new LanTrustStore(protector, path).AddAsync("viewer-1", "Pixel", "android");

        var raw = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain("viewer-1", raw);
        Assert.NotNull(new LanTrustStore(protector, path).Authenticate("viewer-1", credential));
    }

    [Fact]
    public async Task Touch_UpdatesLastSeen()
    {
        var store = Create();
        await store.AddAsync("viewer-1", "Pixel", "android");

        await store.TouchAsync("viewer-1");

        Assert.NotNull(Assert.Single(store.Devices).LastSeenAt);
    }

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
