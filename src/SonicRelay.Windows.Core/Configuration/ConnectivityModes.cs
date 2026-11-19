namespace SonicRelay.Windows.Core.Configuration;

/// <summary>
/// Which signaling transports a session may use (issue #75). Independent of
/// <see cref="RelayModes"/>, which only governs TURN for cloud-signaled peers. The literals
/// match the Flutter client string-for-string; they are local preferences and never reach
/// the backend.
/// </summary>
public static class ConnectivityModes
{
    /// <summary>LAN host always on; cloud session too when the API answers.</summary>
    public const string Auto = "auto";

    /// <summary>LAN host only; RelayControl, STUN and TURN are never contacted.</summary>
    public const string LanOnly = "lanOnly";

    /// <summary>Today's cloud-only behaviour; the LAN host and mDNS stay off.</summary>
    public const string CloudOnly = "cloudOnly";

    public static bool IsValid(string? value) => value is Auto or LanOnly or CloudOnly;

    public static bool AllowsLan(string mode) => mode is Auto or LanOnly;

    public static bool AllowsCloud(string mode) => mode is Auto or CloudOnly;
}
