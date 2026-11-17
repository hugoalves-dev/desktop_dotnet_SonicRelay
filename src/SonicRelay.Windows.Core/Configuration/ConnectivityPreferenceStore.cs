using System.Text.Json;

namespace SonicRelay.Windows.Core.Configuration;

/// <summary>
/// Per-device connectivity preferences (issue #75): the <see cref="ConnectivityModes"/> value,
/// an optional fixed LAN port (so an administrator can open one narrow firewall rule) and the
/// last dynamically chosen port (reused so remembered endpoints on paired phones stay valid).
/// Kept in its own file so it never races <see cref="RelayPreferenceStore"/> writes.
/// </summary>
public sealed class ConnectivityPreferenceStore
{
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string path;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    public ConnectivityPreferenceStore(string? path = null)
    {
        this.path = path ?? DefaultPath;
        Load();
    }

    public static string DefaultPath => Path.Combine(UserConfigurationLoader.DefaultDirectory, "connectivity.json");

    public string Mode { get; private set; } = ConnectivityModes.Auto;
    public int? FixedLanPort { get; private set; }
    public int? LastLanPort { get; private set; }

    /// <summary>Raised after any persisted change; the runtime starts/stops the LAN host from it.</summary>
    public event Action? Changed;

    public Task SetModeAsync(string mode, CancellationToken cancellationToken = default)
    {
        if (!ConnectivityModes.IsValid(mode))
            throw new ArgumentException($"Unknown connectivity mode '{mode}'.", nameof(mode));
        return PersistAsync(() => Mode = mode, cancellationToken);
    }

    public Task SetFixedLanPortAsync(int? port, CancellationToken cancellationToken = default)
    {
        if (port is { } value) ValidatePort(value);
        return PersistAsync(() => FixedLanPort = port, cancellationToken);
    }

    public Task SetLastLanPortAsync(int port, CancellationToken cancellationToken = default)
    {
        ValidatePort(port);
        return PersistAsync(() => LastLanPort = port, cancellationToken, raise: false);
    }

    private static void ValidatePort(int port)
    {
        if (port is < MinPort or > MaxPort)
            throw new ArgumentOutOfRangeException(nameof(port), port, $"The LAN port must be between {MinPort} and {MaxPort}.");
    }

    private async Task PersistAsync(Action apply, CancellationToken cancellationToken, bool raise = true)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            apply();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(new Document(Mode, FixedLanPort, LastLanPort), JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }

        if (raise) Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), JsonOptions);
            if (document is null) return;
            if (ConnectivityModes.IsValid(document.Mode)) Mode = document.Mode!;
            FixedLanPort = IsValidPort(document.FixedLanPort) ? document.FixedLanPort : null;
            LastLanPort = IsValidPort(document.LastLanPort) ? document.LastLanPort : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt preferences file must never block startup; keep the defaults.
        }
    }

    private static bool IsValidPort(int? port) => port is >= MinPort and <= MaxPort;

    private sealed record Document(string? Mode, int? FixedLanPort, int? LastLanPort);
}
