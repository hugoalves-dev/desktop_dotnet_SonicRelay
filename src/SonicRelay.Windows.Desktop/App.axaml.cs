using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SonicRelay.Windows.Core.Configuration;
using Avalonia.Controls;
using SonicRelay.Windows.Presentation;
using SonicRelay.Windows.Presentation.Platform;
using SonicRelay.Windows.Desktop.ViewModels;
using SonicRelay.Windows.Desktop.Views;

namespace SonicRelay.Windows.Desktop;

public partial class App : Application
{
    /// <summary>The platforms with a real capture adapter behind <c>DesktopRuntimeFactory</c>.</summary>
    private static bool IsSupportedPlatform =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // On Windows, Linux and macOS the shell attaches a live publisher runtime
            // (real capture + the configured backend) and opens on the sign-in surface
            // until a session is restored or the user signs in. Elsewhere — the headless
            // render tests, or an unsupported OS — the shell opens on a representative
            // preview so the layout and design system stay verifiable (issues #32, #62).
            var viewModel = IsSupportedPlatform
                ? new MainWindowViewModel()
                : MainWindowViewModel.CreatePreview();

            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;

            if (IsSupportedPlatform) ConfigureStartup(viewModel);

            // Tray + minimize/close-to-tray + reconnect (issue #32). Never let a missing tray
            // backend stop the app from launching.
            try
            {
                var tray = new DesktopTrayController(desktop, mainWindow, viewModel);
                desktop.Exit += (_, _) => tray.Dispose();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                viewModel.LogDiagnostic("tray", "Tray integration unavailable; closing the window will exit normally instead of minimizing to tray.");
            }

            // Launched at sign-in: start minimized. Set after the tray controller subscribes,
            // so with a tray the window is hidden there exactly like a manual minimize.
            if (StartupArguments.RequestsMinimized(desktop.Args ?? []))
                mainWindow.WindowState = WindowState.Minimized;

            if (IsSupportedPlatform)
                _ = AttachConfiguredRuntimeAsync(viewModel);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureStartup(MainWindowViewModel viewModel)
    {
        var platform = AutoStartServiceFactory.DetectPlatform();
        var exe = AutoStartServiceFactory.ResolveExecutablePath(
            Environment.GetEnvironmentVariable("APPIMAGE"), Environment.ProcessPath);
        var autoStart = exe is null ? null : AutoStartServiceFactory.Create(exe, platform);
        var preferences = new PublisherStartupPreferenceStore();
        var startup = new StartupSettingsViewModel(autoStart, platform, preferences);
        viewModel.ConfigureStartup(preferences, startup);
        _ = LoadStartupAsync(startup);
    }

    private static async Task LoadStartupAsync(StartupSettingsViewModel startup)
    {
        // The registered state only drives the switch; failing to read it must not stop launch.
        try { await startup.LoadAsync(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Security.SecurityException) { }
    }

    private static async Task AttachConfiguredRuntimeAsync(MainWindowViewModel viewModel)
    {
        try
        {
            var configuration = await new UserConfigurationLoader().LoadAsync();
            var runtime = DesktopRuntimeFactory.Create(configuration.BackendBaseUrl);
            if (runtime is null) return; // unsupported platform: stay on the sign-in surface
            viewModel.Attach(runtime);
            // Bootstrap (or reuse) this device's persistent credential and exchange it for a
            // short-lived access token so a returning device reconnects automatically instead
            // of re-pairing (issue #26). A missing/unreachable backend simply leaves the
            // sign-in surface showing.
            await runtime.InitializeDeviceIdentityAsync();
            await viewModel.StartSessionIfConfiguredAsync();
        }
        catch
        {
            // Backend unreachable, no stored session, or a missing platform capture
            // dependency (PipeWire tools not installed on Linux, the bundled
            // ScreenCaptureKit helper missing on macOS) at startup: stay on the
            // sign-in surface so the user can retry once the condition is resolved.
        }
    }
}
