using System.Net;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Lan.Discovery;
using SonicRelay.Windows.Presentation.Sessions;

namespace SonicRelay.Windows.Desktop.Tests;

public sealed class ConnectivitySettingsViewModelTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-connvm-{Guid.NewGuid():N}");
    private LanServices lan = null!;
    private ConnectivitySettingsViewModel viewModel = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        lan = await LanServices.CreateAsync(new NullProtector(),
            new ConnectivityPreferenceStore(Path.Combine(directory, "connectivity.json")),
            handlers: [], publisherName: "D", storageDirectory: directory, bindAddress: IPAddress.Loopback,
            advertiser: new SilentAdvertiser());
        viewModel = new ConnectivitySettingsViewModel(lan);
    }

    public async Task DisposeAsync()
    {
        await lan.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Settings_ModeChange_Persists()
    {
        viewModel.Mode = ConnectivityModes.LanOnly;
        viewModel.FixedPortInput = "47321";

        await viewModel.SaveAsync();

        Assert.Null(viewModel.Error);
        var reloaded = new ConnectivityPreferenceStore(Path.Combine(directory, "connectivity.json"));
        Assert.Equal(ConnectivityModes.LanOnly, reloaded.Mode);
        Assert.Equal(47321, reloaded.FixedLanPort);
    }

    [Fact]
    public async Task BlankPort_MeansDynamic()
    {
        viewModel.FixedPortInput = "  ";

        await viewModel.SaveAsync();

        Assert.Null(lan.Connectivity.FixedLanPort);
    }

    [Theory]
    [InlineData("80")]
    [InlineData("70000")]
    [InlineData("abc")]
    public async Task InvalidPort_ShowsError_AndSavesNothing(string port)
    {
        viewModel.Mode = ConnectivityModes.LanOnly;
        viewModel.FixedPortInput = port;

        await viewModel.SaveAsync();

        Assert.NotNull(viewModel.Error);
        Assert.Equal(ConnectivityModes.Auto, lan.Connectivity.Mode);
    }

    [Fact]
    public async Task ResetIdentity_RevokesEveryPairedDevice()
    {
        await lan.Trust.AddAsync("11111111-1111-1111-1111-111111111111", "Pixel", "android");
        var pin = lan.Identity.Pin;

        await viewModel.ResetIdentityAsync();

        Assert.All(lan.Trust.Devices, device => Assert.True(device.Revoked));
        Assert.NotEqual(pin, (await lan.IdentityStore.LoadOrCreateAsync()).Pin);
        Assert.Contains("Restart", viewModel.Status, StringComparison.Ordinal);
    }

    private sealed class SilentAdvertiser : ILanServiceAdvertiser
    {
        public event Action<DiscoveredService>? ServiceFound { add { } remove { } }
        public event Action<string>? ServiceLost { add { } remove { } }
        public void Advertise(string instance, string serviceType, int port, IReadOnlyDictionary<string, string> txt) { }
        public void Unadvertise() { }
        public void Browse(string serviceType) { }
        public void Dispose() { }
    }

    private sealed class NullProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }
}
