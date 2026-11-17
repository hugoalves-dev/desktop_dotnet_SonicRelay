using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.Lan.Identity;

/// <summary>
/// Loads or creates the <see cref="LanIdentity"/>, persisting the PKCS#12 bytes through the
/// platform <see cref="ITokenProtector"/>. When secure storage is unavailable (non-Windows
/// today) the identity lives for this run only — pairings then need repeating after a restart,
/// which is safer than writing a private key in plaintext.
/// </summary>
public sealed class LanIdentityStore(ITokenProtector protector, string? directory = null)
{
    public const string FileName = "lan-identity.dat";
    private static readonly TimeSpan Validity = TimeSpan.FromDays(365 * 5);

    private readonly ITokenProtector protector = protector ?? throw new ArgumentNullException(nameof(protector));
    private readonly string path = Path.Combine(directory ?? LanStoragePaths.DefaultDirectory, FileName);
    private readonly SemaphoreSlim gate = new(1, 1);
    private LanIdentity? current;

    /// <summary>False when the identity could not be persisted (it will change on restart).</summary>
    public bool IsPersistent { get; private set; } = true;

    public async Task<LanIdentity> LoadOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (current is not null) return current;
            current = await TryLoadAsync(cancellationToken).ConfigureAwait(false)
                ?? await CreateAndSaveAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Generates a new identity, invalidating every existing pairing by design.</summary>
    public async Task<LanIdentity> ResetAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            current = await CreateAndSaveAsync(cancellationToken).ConfigureAwait(false);
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<LanIdentity?> TryLoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = protector.Unprotect(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            var document = JsonSerializer.Deserialize<Document>(bytes);
            if (document is null) return null;
            return new LanIdentity(LoadPkcs12(Convert.FromBase64String(document.Pkcs12)), document.PublisherDeviceId);
        }
        catch (Exception exception) when (exception is IOException or JsonException or FormatException
            or CryptographicException or SecureStorageUnavailableException or UnauthorizedAccessException)
        {
            // An unreadable identity is replaced; the user re-pairs, which is the documented
            // consequence of losing the key.
            return null;
        }
    }

    private async Task<LanIdentity> CreateAndSaveAsync(CancellationToken cancellationToken)
    {
        var deviceId = Guid.NewGuid();
        var pkcs12 = CreatePkcs12(deviceId);
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new Document(deviceId, Convert.ToBase64String(pkcs12)));
            var protectedBytes = protector.Protect(payload);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            IsPersistent = true;
        }
        catch (SecureStorageUnavailableException)
        {
            IsPersistent = false;
        }

        return new LanIdentity(LoadPkcs12(pkcs12), deviceId);
    }

    private static byte[] CreatePkcs12(Guid deviceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN=SonicRelay {LanIdentity.ComputeHint(deviceId.ToString("D"))}",
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        using var certificate = request.CreateSelfSigned(notBefore, notBefore.Add(Validity));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    // Re-importing from PKCS#12 (rather than using CreateSelfSigned's ephemeral key directly)
    // is what lets SChannel on Windows use the key for a TLS server handshake.
    private static X509Certificate2 LoadPkcs12(byte[] pkcs12) =>
        X509CertificateLoader.LoadPkcs12(pkcs12, password: null, X509KeyStorageFlags.Exportable);

    private sealed record Document(Guid PublisherDeviceId, string Pkcs12);
}
