using System.Buffers.Text;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SonicRelay.Windows.Lan.Identity;

/// <summary>
/// The publisher's LAN identity (issue #75): a self-signed ECDSA P-256 certificate whose
/// <see cref="Pin"/> phones store at pairing time and verify on every TLS handshake, plus a
/// stable <see cref="PublisherDeviceId"/>. Independent of the cloud device identity.
/// </summary>
public sealed class LanIdentity(X509Certificate2 certificate, Guid publisherDeviceId)
{
    public X509Certificate2 Certificate { get; } = certificate ?? throw new ArgumentNullException(nameof(certificate));
    public Guid PublisherDeviceId { get; } = publisherDeviceId;

    /// <summary>base64url(SHA-256(certificate DER)), no padding. The whole certificate is pinned
    /// (not the SPKI) so a Dart client can verify it without ASN.1 parsing.</summary>
    public string Pin { get; } = ComputePin(certificate.RawData);

    /// <summary>Non-identifying 6-hex hint announced in DNS-SD TXT records.</summary>
    public string Hint { get; } = ComputeHint(publisherDeviceId.ToString("D"));

    public static string ComputePin(byte[] der) => Base64Url.EncodeToString(SHA256.HashData(der));

    public static string ComputeHint(string deviceId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..6];

    /// <summary>Truncated pin for logs; the full value never needs to appear in diagnostics.</summary>
    public static string ShortPin(string pin) => pin.Length <= 8 ? pin : pin[..8];
}
