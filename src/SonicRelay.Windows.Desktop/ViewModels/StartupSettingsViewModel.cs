using SonicRelay.Windows.Core.Configuration;
using SonicRelay.Windows.Presentation.Platform;

namespace SonicRelay.Windows.Desktop.ViewModels;

/// <summary>
/// Settings → Startup: launch at sign-in and "start streaming when the app opens"
/// (flutter_mobile-web_SonicRelay#65). Both off by default. Launch at sign-in lives in the
/// OS (Run key / XDG entry), so its state is read back from <see cref="IAutoStartService"/>
/// rather than stored here; a Store install cannot toggle it in-app and shows a hint.
/// </summary>
public sealed class StartupSettingsViewModel : ViewModelBase
{
    public const string StoreStartupHint = "Turn this on in Windows Settings → Apps → Startup.";

    private readonly IAutoStartService? autoStart;
    private readonly PublisherStartupPreferenceStore store;
    private bool launchAtLogin;

    public StartupSettingsViewModel(
        IAutoStartService? autoStart,
        AutoStartPlatform platform,
        PublisherStartupPreferenceStore store)
    {
        this.autoStart = autoStart;
        this.store = store;
        ShowStoreStartupHint = platform == AutoStartPlatform.WindowsPackaged;
    }

    public bool CanToggleLaunchAtLogin => autoStart is not null;
    public bool ShowStoreStartupHint { get; }

    public bool LaunchAtLogin
    {
        get => launchAtLogin;
        set => _ = SetLaunchAtLoginAsync(value);
    }

    public bool StartSessionOnLaunch
    {
        get => store.StartSessionOnLaunch;
        set => _ = SetStartSessionOnLaunchAsync(value);
    }

    /// <summary>Reads whether launch at sign-in is currently registered.</summary>
    public async Task LoadAsync()
    {
        if (autoStart is null) return;
        SetProperty(ref launchAtLogin, await autoStart.IsEnabledAsync(), nameof(LaunchAtLogin));
    }

    public async Task SetLaunchAtLoginAsync(bool value)
    {
        if (autoStart is null) return;
        await autoStart.SetEnabledAsync(value);
        SetProperty(ref launchAtLogin, value, nameof(LaunchAtLogin));
    }

    public async Task SetStartSessionOnLaunchAsync(bool value)
    {
        await store.SetStartSessionOnLaunchAsync(value);
        RaisePropertyChanged(nameof(StartSessionOnLaunch));
    }
}
