using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Lan.Hosting;
using SonicRelay.Windows.Lan.Pairing;
using SonicRelay.Windows.Lan.Trust;
using SonicRelay.Windows.Presentation.Sessions;

namespace SonicRelay.Windows.Presentation.Pairing;

/// <summary>
/// The offline pairing surface (issue #75): opens a 120 s LAN pairing window and shows it as
/// one QR (v2, carrying the active cloud challenge too when there is one), a typed code and the
/// addresses a phone on another subnet can enter. Also lists and revokes LAN-paired phones.
/// Works with no RelayControl at all.
/// </summary>
public sealed class LanPairingViewModel : IDisposable
{
    private readonly LanServices lan;
    private readonly IPairingQrCodeService qrCodes;
    private readonly Func<CloudPairingBlock?> cloudBlock;

    public LanPairingViewModel(LanServices lan, IPairingQrCodeService qrCodes, Func<CloudPairingBlock?>? cloudBlock = null)
    {
        this.lan = lan ?? throw new ArgumentNullException(nameof(lan));
        this.qrCodes = qrCodes ?? throw new ArgumentNullException(nameof(qrCodes));
        this.cloudBlock = cloudBlock ?? (() => null);
        lan.Pairing.WindowChanged += OnWindowChanged;
        lan.Trust.Changed += Notify;
        lan.Host.StateChanged += Notify;
    }

    public LanPairingWindow? Window { get; private set; }
    public byte[]? QrCodePng { get; private set; }
    public string? ManualCode => Window?.ManualCode;
    public IReadOnlyList<string> Endpoints { get; private set; } = [];

    public bool IsAvailable => lan.Host.State == LanHostState.Listening;

    public string HostStatus => lan.Host.State switch
    {
        LanHostState.Listening => $"Local network pairing is listening on port {lan.Host.Port}.",
        LanHostState.BindFailed => $"lan.bind_failed: the local listener could not start ({lan.Host.Error}). Choose another port in Settings.",
        _ when !ConnectivityModes.AllowsLan(lan.Connectivity.Mode) => "Local network is disabled (Cloud only).",
        _ => "The local listener is not listening yet.",
    };

    public IReadOnlyList<LanTrustedDevice> PairedDevices => lan.Trust.Devices.Where(device => !device.Revoked).ToArray();

    public event Action? StateChanged;

    /// <summary>Opens a fresh single-use window (replacing any open one).</summary>
    public void Open()
    {
        if (!IsAvailable || lan.Host.Port is not { } port)
        {
            Clear();
            return;
        }

        var window = lan.Pairing.Open();
        Endpoints = lan.EndpointsFor(port);
        var payload = LanPairingPayload.BuildQrJson(
            cloudBlock(),
            new LanPairingBlock(window, lan.Identity.Pin, lan.Identity.PublisherDeviceId, lan.PublisherName, Endpoints));
        QrCodePng = qrCodes.RenderPng(payload);
        Window = window;
        Notify();
    }

    public int SecondsRemaining(DateTimeOffset now) =>
        Window is null ? 0 : Math.Max(0, (int)Math.Ceiling((Window.ExpiresAt - now).TotalSeconds));

    /// <summary>Called every second by the view; clears an expired window.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (Window is not null && now >= Window.ExpiresAt) Clear();
    }

    public async Task RevokeAsync(string viewerDeviceId, CancellationToken cancellationToken = default)
    {
        await lan.Trust.RevokeAsync(viewerDeviceId, cancellationToken).ConfigureAwait(false);
        Notify();
    }

    public void Dispose()
    {
        lan.Pairing.WindowChanged -= OnWindowChanged;
        lan.Trust.Changed -= Notify;
        lan.Host.StateChanged -= Notify;
    }

    // A window that is no longer the service's current one was redeemed, exhausted or closed.
    private void OnWindowChanged()
    {
        if (Window is not null && !ReferenceEquals(lan.Pairing.Current, Window)) Clear();
    }

    private void Clear()
    {
        Window = null;
        QrCodePng = null;
        Endpoints = [];
        Notify();
    }

    private void Notify() => StateChanged?.Invoke();
}
