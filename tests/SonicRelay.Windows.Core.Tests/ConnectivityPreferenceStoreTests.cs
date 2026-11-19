using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Core.Tests;

public sealed class ConnectivityPreferenceStoreTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"sonicrelay-connectivity-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_ToAuto()
    {
        var store = new ConnectivityPreferenceStore(path);

        Assert.Equal(ConnectivityModes.Auto, store.Mode);
        Assert.Null(store.FixedLanPort);
        Assert.Null(store.LastLanPort);
    }

    [Fact]
    public async Task Persists_AndReloads()
    {
        var store = new ConnectivityPreferenceStore(path);
        await store.SetModeAsync(ConnectivityModes.LanOnly);
        await store.SetFixedLanPortAsync(47321);
        await store.SetLastLanPortAsync(50000);

        var reloaded = new ConnectivityPreferenceStore(path);
        Assert.Equal(ConnectivityModes.LanOnly, reloaded.Mode);
        Assert.Equal(47321, reloaded.FixedLanPort);
        Assert.Equal(50000, reloaded.LastLanPort);
    }

    [Fact]
    public async Task InvalidMode_Throws()
    {
        var store = new ConnectivityPreferenceStore(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetModeAsync("lan"));
        Assert.Equal(ConnectivityModes.Auto, store.Mode);
    }

    [Fact]
    public void CorruptFile_FallsBackToAuto()
    {
        File.WriteAllText(path, "{ not json");

        Assert.Equal(ConnectivityModes.Auto, new ConnectivityPreferenceStore(path).Mode);
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(65536)]
    public async Task FixedPort_OutOfRange_Throws(int port)
    {
        var store = new ConnectivityPreferenceStore(path);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SetFixedLanPortAsync(port));
    }

    [Fact]
    public async Task ModeChange_RaisesChanged()
    {
        var store = new ConnectivityPreferenceStore(path);
        var raised = 0;
        store.Changed += () => raised++;

        await store.SetModeAsync(ConnectivityModes.CloudOnly);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Literals_MatchTheSharedProtocolStrings()
    {
        Assert.Equal("auto", ConnectivityModes.Auto);
        Assert.Equal("lanOnly", ConnectivityModes.LanOnly);
        Assert.Equal("cloudOnly", ConnectivityModes.CloudOnly);
        Assert.False(ConnectivityModes.IsValid("LANONLY"));
    }

    public void Dispose()
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
