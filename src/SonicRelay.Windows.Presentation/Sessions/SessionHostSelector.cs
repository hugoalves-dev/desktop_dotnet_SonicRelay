using SonicRelay.Windows.Core.Configuration;

namespace SonicRelay.Windows.Presentation.Sessions;

/// <summary>Where a session's signaling lives (issue #75).</summary>
public enum SessionHostKind
{
    /// <summary>RelayControl only — today's behaviour.</summary>
    Cloud,

    /// <summary>The publisher's own LAN host only; no RelayControl at all.</summary>
    Lan,

    /// <summary>One session reachable through both RelayControl and the LAN host.</summary>
    Dual,
}

public static class SessionHostSelector
{
    /// <summary>
    /// Picks the host for the next session. <paramref name="cloudUsable"/> means this device has a
    /// cloud identity and a network; in <c>auto</c> its absence means a LAN-only session rather
    /// than an error.
    /// </summary>
    public static SessionHostKind Select(string connectivityMode, bool cloudUsable) => connectivityMode switch
    {
        ConnectivityModes.CloudOnly => SessionHostKind.Cloud,
        ConnectivityModes.LanOnly => SessionHostKind.Lan,
        _ => cloudUsable ? SessionHostKind.Dual : SessionHostKind.Lan,
    };
}
