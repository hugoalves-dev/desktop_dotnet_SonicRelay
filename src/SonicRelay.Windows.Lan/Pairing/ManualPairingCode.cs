using System.Buffers.Text;
using System.Text;

namespace SonicRelay.Windows.Lan.Pairing;

/// <summary>
/// The typed pairing code (issue #75): 16 Crockford base32 characters shown as
/// <c>XXXX-XXXX-XXXX-XXXX</c>, encoding 80 bits = the first 40 bits of the certificate pin
/// followed by the first 40 bits of the pairing secret. The pin prefix lets a phone that only
/// typed an address reject a different TLS certificate during the 120 s pairing window.
/// </summary>
public static class ManualPairingCode
{
    public const int PartLength = 5;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(string pin, ReadOnlySpan<byte> secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);
        if (secret.Length < PartLength) throw new ArgumentException("The secret is too short.", nameof(secret));

        var pinBytes = Base64Url.DecodeFromChars(pin);
        Span<byte> data = stackalloc byte[PartLength * 2];
        pinBytes.AsSpan(0, PartLength).CopyTo(data);
        secret[..PartLength].CopyTo(data[PartLength..]);

        UInt128 value = 0;
        foreach (var b in data) value = (value << 8) | b;
        var chars = new char[16];
        for (var i = 15; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        var text = new string(chars);
        return $"{text[..4]}-{text[4..8]}-{text[8..12]}-{text[12..]}";
    }

    /// <summary>Case-insensitive; ignores spaces and dashes; maps O→0 and I/L→1.</summary>
    public static bool TryDecode(string? code, out byte[] pinPrefix, out byte[] secret)
    {
        pinPrefix = [];
        secret = [];
        if (string.IsNullOrWhiteSpace(code)) return false;

        var normalized = new StringBuilder(16);
        foreach (var raw in code)
        {
            if (raw is ' ' or '-') continue;
            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (Alphabet.IndexOf(c) < 0) return false;
            normalized.Append(c);
        }

        if (normalized.Length != 16) return false;

        UInt128 value = 0;
        foreach (var c in normalized.ToString()) value = (value << 5) | (uint)Alphabet.IndexOf(c);
        var data = new byte[PartLength * 2];
        for (var i = data.Length - 1; i >= 0; i--)
        {
            data[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        pinPrefix = data[..PartLength];
        secret = data[PartLength..];
        return true;
    }
}
