using System.Net;
using System.Text;
using System.Text.Json;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Core.Storage;
using SonicRelay.Windows.Lan.Discovery;
using SonicRelay.Windows.Lan.Pairing;
using SonicRelay.Windows.Presentation.Pairing;
using SonicRelay.Windows.Presentation.Sessions;

namespace SonicRelay.Windows.Presentation.Tests;

public sealed class LanPairingViewModelTests : IAsyncLifetime
{
    private const string Viewer = "11111111-1111-1111-1111-111111111111";
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"sonicrelay-lanpair-{Guid.NewGuid():N}");
    private readonly RecordingQr qr = new();
    private CloudPairingBlock? cloud;
    private LanServices lan = null!;
    private LanPairingViewModel viewModel = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        lan = await LanServices.CreateAsync(
            new NullProtector(),
            new ConnectivityPreferenceStore(Path.Combine(directory, "connectivity.json")),
            handlers: [],
            publisherName: "DESKTOP-01",
            storageDirectory: directory,
            bindAddress: IPAddress.Loopback,
            advertiser: new SilentAdvertiser());
        viewModel = new LanPairingViewModel(lan, qr, () => cloud);
    }

    public async Task DisposeAsync()
    {
        viewModel.Dispose();
        await lan.DisposeAsync();
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void Open_WhenHostStopped_StaysUnavailable()
    {
        viewModel.Open();

        Assert.False(viewModel.IsAvailable);
        Assert.Null(viewModel.QrCodePng);
        Assert.Contains("not listening", viewModel.HostStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_BuildsLanQr_WithEndpointsAndManualCode()
    {
        await lan.ApplyConnectivityAsync();

        viewModel.Open();

        Assert.True(viewModel.IsAvailable);
        Assert.NotNull(viewModel.QrCodePng);
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", viewModel.ManualCode!);
        Assert.Contains(viewModel.Endpoints, endpoint => endpoint.EndsWith($":{lan.Host.Port}", StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(qr.LastPayload!);
        Assert.Equal(lan.Identity.Pin, payload.RootElement.GetProperty("lan").GetProperty("pin").GetString());
        Assert.False(payload.RootElement.TryGetProperty("cloud", out _));
    }

    [Fact]
    public async Task Open_IncludesTheCloudChallengeWhenOneIsActive()
    {
        await lan.ApplyConnectivityAsync();
        cloud = new CloudPairingBlock(Guid.NewGuid(), "123456");

        viewModel.Open();

        using var payload = JsonDocument.Parse(qr.LastPayload!);
        Assert.Equal("123456", payload.RootElement.GetProperty("cloud").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Tick_AfterExpiry_ClearsTheWindow()
    {
        await lan.ApplyConnectivityAsync();
        viewModel.Open();

        viewModel.Tick(DateTimeOffset.UtcNow.AddMinutes(3));

        Assert.Null(viewModel.QrCodePng);
        Assert.Null(viewModel.ManualCode);
    }

    [Fact]
    public async Task SecondsRemaining_CountsDownFrom120()
    {
        await lan.ApplyConnectivityAsync();
        viewModel.Open();

        Assert.InRange(viewModel.SecondsRemaining(DateTimeOffset.UtcNow), 118, 120);
    }

    [Fact]
    public async Task Redeemed_ClearsWindow_AndListsTheDevice()
    {
        await lan.ApplyConnectivityAsync();
        viewModel.Open();
        var window = lan.Pairing.Current!;

        Assert.Equal(LanRedeemResult.Accepted, lan.Pairing.Redeem(window.PairingId, window.Secret, null));
        await lan.Trust.AddAsync(Viewer, "Pixel", "android");

        Assert.Null(viewModel.ManualCode);
        Assert.Equal("Pixel", Assert.Single(viewModel.PairedDevices).Name);
    }

    [Fact]
    public async Task Revoke_RemovesFromPairedDevices()
    {
        await lan.Trust.AddAsync(Viewer, "Pixel", "android");

        await viewModel.RevokeAsync(Viewer);

        Assert.Empty(viewModel.PairedDevices);
    }

    [Fact]
    public async Task CloudOnly_ReportsLanDisabled()
    {
        await lan.Connectivity.SetModeAsync(ConnectivityModes.CloudOnly);
        await lan.ApplyConnectivityAsync();

        Assert.Contains("Cloud only", viewModel.HostStatus, StringComparison.Ordinal);
    }

    private sealed class RecordingQr : IPairingQrCodeService
    {
        public string? LastPayload { get; private set; }

        public byte[] RenderPng(string payload)
        {
            LastPayload = payload;
            return Encoding.UTF8.GetBytes(payload);
        }
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
