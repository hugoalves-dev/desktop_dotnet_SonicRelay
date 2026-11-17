using SonicRelay.Windows.Core.Storage;

namespace SonicRelay.Windows.Lan.Tests;

internal sealed class PassThroughProtector : ITokenProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext.ToArray();
    public byte[] Unprotect(byte[] ciphertext) => ciphertext.ToArray();
}

/// <summary>Not security — just proves the bytes on disk are what the protector produced.</summary>
internal sealed class XorProtector : ITokenProtector
{
    public int ProtectCalls { get; private set; }
    public int UnprotectCalls { get; private set; }

    public byte[] Protect(byte[] plaintext)
    {
        ProtectCalls++;
        return plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        UnprotectCalls++;
        return ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
