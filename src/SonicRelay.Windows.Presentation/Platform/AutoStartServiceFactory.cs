using System.Runtime.InteropServices;

namespace SonicRelay.Windows.Presentation.Platform;

/// <summary>Command-line switches the publisher understands at startup.</summary>
public static class StartupArguments
{
    /// <summary>Start hidden in the tray (or minimized when there is no tray).</summary>
    public const string Minimized = "--minimized";

    public static bool RequestsMinimized(IEnumerable<string> args) =>
        args.Any(arg => string.Equals(arg, Minimized, StringComparison.OrdinalIgnoreCase));
}

public enum AutoStartPlatform
{
    WindowsUnpackaged,
    WindowsPackaged,
    Linux,
    MacOs,
    Other,
}

/// <summary>
/// Picks the launch-at-login mechanism for this install. Returns null where the app cannot
/// manage it itself: MSIX (Windows owns the StartupTask toggle; Settings shows a hint
/// instead) and macOS (not supported yet).
/// </summary>
public static class AutoStartServiceFactory
{
    private const int AppModelErrorNoPackage = 15700;

    public static IAutoStartService? Create(string executablePath, AutoStartPlatform platform) => platform switch
    {
        AutoStartPlatform.WindowsUnpackaged when OperatingSystem.IsWindows() =>
            new WindowsRunKeyAutoStartService(new CurrentUserRunKey(), executablePath),
        AutoStartPlatform.Linux => LinuxXdgAutoStartService.ForCurrentUser(executablePath),
        _ => null,
    };

    /// <summary>
    /// The path launch-at-login should start. Inside an AppImage the process runs from a
    /// temporary mount that vanishes on exit, so the AppImage file itself (the
    /// <c>APPIMAGE</c> variable its runtime sets) is what must be registered.
    /// </summary>
    public static string? ResolveExecutablePath(string? appImage, string? processPath) =>
        string.IsNullOrWhiteSpace(appImage) ? processPath : appImage;

    public static AutoStartPlatform DetectPlatform()
    {
        if (OperatingSystem.IsWindows())
            return IsPackaged() ? AutoStartPlatform.WindowsPackaged : AutoStartPlatform.WindowsUnpackaged;
        if (OperatingSystem.IsLinux()) return AutoStartPlatform.Linux;
        return OperatingSystem.IsMacOS() ? AutoStartPlatform.MacOs : AutoStartPlatform.Other;
    }

    /// <summary>An MSIX-installed process has package identity; an MSI/portable one does not.</summary>
    private static bool IsPackaged()
    {
        var length = 0u;
        return NativeMethods.GetCurrentPackageFullName(ref length, IntPtr.Zero) != AppModelErrorNoPackage;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        internal static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
    }
}
