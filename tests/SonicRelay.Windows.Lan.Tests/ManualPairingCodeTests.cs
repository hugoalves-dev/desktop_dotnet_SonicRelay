using SonicRelay.Windows.Lan.Pairing;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class ManualPairingCodeTests
{
    // Shared vector with the Flutter client (test/features/lan/manual_pairing_code_test.dart).
    private const string Pin = "A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc-4E";
    private static readonly byte[] Secret = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0];
    private const string Vector = "0E85-HHQJ-20G3-0G2G";

    [Fact]
    public void Encode_MatchesSharedVector()
    {
        Assert.Equal(Vector, ManualPairingCode.Encode(Pin, Secret));
    }

    [Fact]
    public void ManualCode_RoundTrip()
    {
        Assert.True(ManualPairingCode.TryDecode(Vector, out var pinPrefix, out var secret));
        Assert.Equal(new byte[] { 0x03, 0x90, 0x58, 0xC6, 0xF2 }, pinPrefix);
        Assert.Equal(Secret[..5], secret);
    }

    [Theory]
    [InlineData("0e85-hhqj-20g3-0g2g")]
    [InlineData("0E85HHQJ20G30G2G")]
    [InlineData("OE85-HHQJ-2OG3-OG2G")]
    [InlineData(" 0E85 HHQJ 20G3 0G2G ")]
    public void ManualCode_Decoding_IsLenient(string typed)
    {
        Assert.True(ManualPairingCode.TryDecode(typed, out var pinPrefix, out _));
        Assert.Equal(new byte[] { 0x03, 0x90, 0x58, 0xC6, 0xF2 }, pinPrefix);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0E85-HHQJ-20G3")]
    [InlineData("0E85-HHQJ-20G3-0G2U")]
    [InlineData("0E85-HHQJ-20G3-0G2G-0000")]
    public void ManualCode_RejectsMalformed(string typed)
    {
        Assert.False(ManualPairingCode.TryDecode(typed, out _, out _));
    }
}
