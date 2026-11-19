using System.Buffers.Text;
using System.Security.Cryptography;

namespace SonicRelay.Windows.Lan.Pairing;

/// <summary>An open pairing window: one single-use 80-bit secret valid for <see cref="LanPairingService.Ttl"/>.</summary>
public sealed record LanPairingWindow(Guid PairingId, byte[] Secret, DateTimeOffset ExpiresAt, string ManualCode);

public enum LanRedeemResult
{
    Accepted,
    Invalid,
    Expired,
    Exhausted,
}

/// <summary>
/// Issues and redeems offline pairing windows (issue #75). At most one window is open at a time;
/// a redeem consumes it atomically, so a replayed or concurrently double-submitted secret gets
/// exactly one <see cref="LanRedeemResult.Accepted"/>. Five failed attempts close the window.
/// Secrets are compared in constant time.
/// </summary>
public sealed class LanPairingService(string pin, TimeProvider time)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);
    public const int SecretLength = 10;
    public const int MaxFailedAttempts = 5;

    private readonly byte[] pinPrefix = Base64Url.DecodeFromChars(pin)[..ManualPairingCode.PartLength];
    private readonly object gate = new();
    private LanPairingWindow? window;
    private int failures;
    private bool exhausted;

    public LanPairingWindow? Current
    {
        get
        {
            lock (gate) return window;
        }
    }

    public event Action? WindowChanged;

    public LanPairingWindow Open()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        var opened = new LanPairingWindow(
            Guid.NewGuid(),
            secret,
            time.GetUtcNow().Add(Ttl),
            ManualPairingCode.Encode(pin, secret));
        lock (gate)
        {
            window = opened;
            failures = 0;
            exhausted = false;
        }

        WindowChanged?.Invoke();
        return opened;
    }

    public void Close()
    {
        lock (gate)
        {
            window = null;
            exhausted = false;
        }

        WindowChanged?.Invoke();
    }

    /// <summary>Redeems either the QR pair (<paramref name="pairingId"/> + full <paramref name="secret"/>)
    /// or a typed <paramref name="manualCode"/>.</summary>
    public LanRedeemResult Redeem(Guid? pairingId, byte[]? secret, string? manualCode)
    {
        LanRedeemResult result;
        lock (gate)
        {
            result = RedeemLocked(pairingId, secret, manualCode);
        }

        if (result is LanRedeemResult.Accepted or LanRedeemResult.Exhausted) WindowChanged?.Invoke();
        return result;
    }

    private LanRedeemResult RedeemLocked(Guid? pairingId, byte[]? secret, string? manualCode)
    {
        if (exhausted) return LanRedeemResult.Exhausted;
        if (window is null) return LanRedeemResult.Invalid;
        if (time.GetUtcNow() > window.ExpiresAt)
        {
            window = null;
            return LanRedeemResult.Expired;
        }

        if (Matches(window, pairingId, secret, manualCode))
        {
            window = null;
            return LanRedeemResult.Accepted;
        }

        if (++failures < MaxFailedAttempts) return LanRedeemResult.Invalid;
        window = null;
        exhausted = true;
        return LanRedeemResult.Exhausted;
    }

    private bool Matches(LanPairingWindow open, Guid? pairingId, byte[]? secret, string? manualCode)
    {
        if (secret is not null && pairingId is { } id)
        {
            return id == open.PairingId
                && CryptographicOperations.FixedTimeEquals(secret, open.Secret);
        }

        if (manualCode is not null && ManualPairingCode.TryDecode(manualCode, out var typedPin, out var typedSecret))
        {
            // Evaluate both comparisons so timing does not reveal which half was wrong.
            var pinOk = CryptographicOperations.FixedTimeEquals(typedPin, pinPrefix);
            var secretOk = CryptographicOperations.FixedTimeEquals(typedSecret, open.Secret.AsSpan(0, ManualPairingCode.PartLength));
            return pinOk & secretOk;
        }

        return false;
    }
}
