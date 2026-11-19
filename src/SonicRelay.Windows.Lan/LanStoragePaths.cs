namespace SonicRelay.Windows.Lan;

/// <summary>User-scoped folder for LAN identity and trust, beside the device credential.</summary>
public static class LanStoragePaths
{
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SonicRelay",
        "WindowsPublisher");
}
