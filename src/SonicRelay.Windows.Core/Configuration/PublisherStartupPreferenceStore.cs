using System.Text.Json;

namespace SonicRelay.Windows.Core.Configuration;

/// <summary>
/// Persists what the publisher does by itself when it opens. Mirrors
/// <see cref="TrayBackgroundPreferenceStore"/>: a small user-scoped JSON file, read once and
/// cached, falling back to safe defaults (everything off) when missing or corrupt.
/// </summary>
public sealed class PublisherStartupPreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string DefaultPath => Path.Combine(UserConfigurationLoader.DefaultDirectory, "startup.json");

    private readonly string _path;

    public PublisherStartupPreferenceStore(string? path = null)
    {
        _path = path ?? DefaultPath;
        StartSessionOnLaunch = Load().StartSessionOnLaunch;
    }

    /// <summary>
    /// Create a broadcast session and start audio capture as soon as the device identity is
    /// ready, so a paired viewer with auto-connect can join without anyone at the PC.
    /// </summary>
    public bool StartSessionOnLaunch { get; private set; }

    public async Task SetStartSessionOnLaunchAsync(bool value, CancellationToken cancellationToken = default)
    {
        StartSessionOnLaunch = value;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(
            _path,
            JsonSerializer.Serialize(new PreferencesDocument(value), JsonOptions),
            cancellationToken);
    }

    private PreferencesDocument Load()
    {
        try
        {
            if (!File.Exists(_path)) return PreferencesDocument.Default;
            return JsonSerializer.Deserialize<PreferencesDocument>(File.ReadAllText(_path), JsonOptions)
                ?? PreferencesDocument.Default;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return PreferencesDocument.Default;
        }
    }

    private sealed record PreferencesDocument(bool StartSessionOnLaunch)
    {
        public static PreferencesDocument Default { get; } = new(StartSessionOnLaunch: false);
    }
}
