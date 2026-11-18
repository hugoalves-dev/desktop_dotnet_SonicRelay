using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.Lan.Trust;

/// <summary>A phone this publisher paired with over the LAN. Only the credential's hash is kept.</summary>
public sealed record LanTrustedDevice(
    string ViewerDeviceId,
    string Name,
    string Platform,
    string CredentialSha256,
    DateTimeOffset PairedAt,
    DateTimeOffset? LastSeenAt,
    bool Revoked);

/// <summary>
/// Local, revocable LAN trust (issue #75). Each pairing issues a fresh 256-bit credential that
/// the phone keeps; this store keeps only SHA-256(credential), protected at rest by the platform
/// <see cref="ITokenProtector"/> (in memory only when secure storage is unavailable). Revocation
/// is local and immediate; a cloud-side revocation cannot reach it while the devices are offline.
/// </summary>
public sealed class LanTrustStore
{
    public const string FileName = "lan-trust.dat";
    private const int CredentialBytes = 32;

    private readonly ITokenProtector protector;
    private readonly string path;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly object gate = new();
    private Dictionary<string, LanTrustedDevice> devices;

    public LanTrustStore(ITokenProtector protector, string? path = null, TimeProvider? time = null)
    {
        this.protector = protector ?? throw new ArgumentNullException(nameof(protector));
        this.path = path ?? Path.Combine(LanStoragePaths.DefaultDirectory, FileName);
        this.time = time ?? TimeProvider.System;
        devices = Load();
    }

    public IReadOnlyList<LanTrustedDevice> Devices
    {
        get
        {
            lock (gate) return devices.Values.OrderBy(device => device.PairedAt).ToArray();
        }
    }

    /// <summary>Raised with the viewer id when a device is revoked; the hub closes its socket.</summary>
    public event Action<string>? Revoked;

    public event Action? Changed;

    /// <summary>Trusts a device and returns its new credential (replacing any previous one).</summary>
    public async Task<string> AddAsync(string viewerDeviceId, string name, string platform, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewerDeviceId);
        var credential = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(CredentialBytes));
        var device = new LanTrustedDevice(
            viewerDeviceId,
            string.IsNullOrWhiteSpace(name) ? "Unnamed device" : name.Trim(),
            string.IsNullOrWhiteSpace(platform) ? "unknown" : platform.Trim(),
            Hash(credential),
            time.GetUtcNow(),
            LastSeenAt: null,
            Revoked: false);
        lock (gate) devices[viewerDeviceId] = device;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return credential;
    }

    /// <summary>The trusted device for this credential, or null when unknown, wrong or revoked.</summary>
    public LanTrustedDevice? Authenticate(string viewerDeviceId, string credential)
    {
        if (string.IsNullOrEmpty(viewerDeviceId) || string.IsNullOrEmpty(credential)) return null;
        LanTrustedDevice? device;
        lock (gate) devices.TryGetValue(viewerDeviceId, out device);
        if (device is null || device.Revoked) return null;

        var expected = Encoding.ASCII.GetBytes(device.CredentialSha256);
        var actual = Encoding.ASCII.GetBytes(Hash(credential));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? device : null;
    }

    public async Task RevokeAsync(string viewerDeviceId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(viewerDeviceId, out var device)) return;
            devices[viewerDeviceId] = device with { Revoked = true };
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        Revoked?.Invoke(viewerDeviceId);
    }

    public async Task TouchAsync(string viewerDeviceId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(viewerDeviceId, out var device)) return;
            devices[viewerDeviceId] = device with { LastSeenAt = time.GetUtcNow() };
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Hash(string credential) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private Dictionary<string, LanTrustedDevice> Load()
    {
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var json = protector.Unprotect(File.ReadAllBytes(path));
            var list = JsonSerializer.Deserialize<List<LanTrustedDevice>>(json) ?? [];
            return list.ToDictionary(device => device.ViewerDeviceId, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or JsonException or CryptographicException
            or SecureStorageUnavailableException or UnauthorizedAccessException)
        {
            // Unreadable trust means nobody is trusted: fail closed, phones re-pair.
            return new(StringComparer.Ordinal);
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] json;
            lock (gate) json = JsonSerializer.SerializeToUtf8Bytes(devices.Values.ToList());
            try
            {
                var protectedBytes = protector.Protect(json);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
            }
            catch (SecureStorageUnavailableException)
            {
                // No secure storage on this platform: trust lives for this run only.
            }
        }
        finally
        {
            writeLock.Release();
        }

        Changed?.Invoke();
    }
}
