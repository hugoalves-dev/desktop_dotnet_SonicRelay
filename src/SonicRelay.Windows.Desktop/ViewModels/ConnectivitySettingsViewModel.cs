using System.Globalization;
using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Presentation.Sessions;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// The "Connectivity" settings card (issue #75): Auto / LAN only / Cloud only, an optional fixed
/// LAN port (so an administrator can open one narrow firewall rule), the listener status, and
/// resetting the LAN identity. Mode changes start/stop the LAN host immediately and apply to
/// the next session; an active session is never migrated.
/// </summary>
public sealed class ConnectivitySettingsViewModel : ViewModelBase
{
    private readonly LanServices lan;
    private string mode;
    private string fixedPortInput;
    private string? error;
    private string status = "";

    public ConnectivitySettingsViewModel(LanServices lan)
    {
        this.lan = lan ?? throw new ArgumentNullException(nameof(lan));
        mode = lan.Connectivity.Mode;
        fixedPortInput = lan.Connectivity.FixedLanPort?.ToString(CultureInfo.InvariantCulture) ?? "";
        SaveCommand = new RelayCommand(SaveAsync);
        ResetIdentityCommand = new RelayCommand(ResetIdentityAsync);
        lan.Host.StateChanged += () => RaisePropertyChanged(nameof(HostStatus));
    }

    public IReadOnlyList<string> ModeOptions { get; } =
        [ConnectivityModes.Auto, ConnectivityModes.LanOnly, ConnectivityModes.CloudOnly];

    public string Mode
    {
        get => mode;
        set => SetProperty(ref mode, value);
    }

    /// <summary>Blank = dynamic port (remembered across runs).</summary>
    public string FixedPortInput
    {
        get => fixedPortInput;
        set => SetProperty(ref fixedPortInput, value);
    }

    public string? Error
    {
        get => error;
        private set => SetProperty(ref error, value);
    }

    public string Status
    {
        get => status;
        private set => SetProperty(ref status, value);
    }

    public string HostStatus => lan.Host.State switch
    {
        Lan.Hosting.LanHostState.Listening => $"Listening for paired phones on port {lan.Host.Port}.",
        Lan.Hosting.LanHostState.BindFailed => $"lan.bind_failed: {lan.Host.Error}",
        _ => "The local listener is off.",
    };

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetIdentityCommand { get; }

    public async Task SaveAsync()
    {
        int? port = null;
        if (!string.IsNullOrWhiteSpace(fixedPortInput))
        {
            if (!int.TryParse(fixedPortInput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed is < ConnectivityPreferenceStore.MinPort or > ConnectivityPreferenceStore.MaxPort)
            {
                Error = $"Enter a port between {ConnectivityPreferenceStore.MinPort} and {ConnectivityPreferenceStore.MaxPort}, or leave it blank.";
                return;
            }

            port = parsed;
        }

        Error = null;
        await lan.Connectivity.SetFixedLanPortAsync(port);
        await lan.Connectivity.SetModeAsync(mode);
        Status = "Saved. Applies to the next session.";
    }

    /// <summary>New certificate and device id; every phone must pair again.</summary>
    public async Task ResetIdentityAsync()
    {
        await lan.IdentityStore.ResetAsync();
        foreach (var device in lan.Trust.Devices.Where(device => !device.Revoked))
            await lan.Trust.RevokeAsync(device.ViewerDeviceId);
        Status = "LAN identity reset and every paired phone revoked. Restart SonicRelay to apply the new identity.";
    }
}
