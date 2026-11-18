using System.Net;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Presentation.Sessions;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class LanServicesTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-lansvc-{Guid.NewGuid():N}");
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
            bindAddress: IPAddress.Loopback);
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
    public void Endpoints_IncludeTheHostName()
    {
        Assert.EndsWith(".local:47321", services.EndpointsFor(47321)[^1]);
    }

    private sealed class NullProtector : ITokenProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;
        public byte[] Unprotect(byte[] ciphertext) => ciphertext;
    }
}
