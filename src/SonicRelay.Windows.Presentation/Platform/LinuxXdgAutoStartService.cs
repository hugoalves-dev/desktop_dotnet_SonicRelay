namespace SonicRelay.Windows.Presentation.Platform;

/// <summary>
/// Launch at sign-in on Linux desktops through an XDG autostart entry
/// (<c>$XDG_CONFIG_HOME/autostart</c>, falling back to <c>~/.config/autostart</c>).
/// </summary>
public sealed class LinuxXdgAutoStartService(string executablePath, string configHome) : IAutoStartService
{
    public const string FileName = "sonicrelay-publisher.desktop";

    private string EntryPath => Path.Combine(configHome, "autostart", FileName);

    public static string ResolveConfigHome(string? xdgConfigHome, string home) =>
        string.IsNullOrWhiteSpace(xdgConfigHome) ? Path.Combine(home, ".config") : xdgConfigHome;

    public static LinuxXdgAutoStartService ForCurrentUser(string executablePath) => new(
        executablePath,
        ResolveConfigHome(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(EntryPath));

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            File.Delete(EntryPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        var entry = string.Join('\n',
            "[Desktop Entry]",
            "Type=Application",
            "Name=SonicRelay Publisher",
            $"Exec=\"{executablePath}\" {StartupArguments.Minimized}",
            "X-GNOME-Autostart-enabled=true",
            "");
        await File.WriteAllTextAsync(EntryPath, entry, cancellationToken);
    }
}
