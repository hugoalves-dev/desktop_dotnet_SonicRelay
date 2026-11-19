using System.Runtime.Versioning;
using Microsoft.Win32;

namespace SonicRelay.Windows.Presentation.Platform;

/// <summary>The per-user Run key, abstracted so the service is testable off Windows.</summary>
public interface IRunKey
{
    string? GetValue(string name);
    void SetValue(string name, string value);
    void DeleteValue(string name);
}

/// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. No elevation needed.</summary>
[SupportedOSPlatform("windows")]
public sealed class CurrentUserRunKey : IRunKey
{
    private const string Path = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? GetValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>
/// Launch at sign-in for an unpackaged (MSI/portable) Windows install. An MSIX install
/// cannot use this: its registry writes are virtualized, so the Run value would be ignored.
/// </summary>
public sealed class WindowsRunKeyAutoStartService(IRunKey runKey, string executablePath) : IAutoStartService
{
    public const string ValueName = "SonicRelayPublisher";

    private string Command => $"\"{executablePath}\" {StartupArguments.Minimized}";

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(runKey.GetValue(ValueName) is not null);

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (enabled) runKey.SetValue(ValueName, Command);
        else runKey.DeleteValue(ValueName);
        return Task.CompletedTask;
    }
}
