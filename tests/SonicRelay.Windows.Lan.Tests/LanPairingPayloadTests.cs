using System.Text.Json;
using SonicRelay.Windows.Lan.Pairing;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanPairingPayloadTests
{
    private static readonly Guid PublisherId = Guid.Parse("8f14e45f-ceea-467e-9b4a-1f3a2b6c7d8e");
    private static readonly LanPairingWindow Window = new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0],
        DateTimeOffset.Parse("2026-11-17T12:02:00Z"),
        "0E85-HHQJ-20G3-0G2G");

    [Fact]
    public void Qr_MatchesSpecShape()
    {
        var json = LanPairingPayload.BuildQrJson(
            new CloudPairingBlock(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), "123456"),
            new LanPairingBlock(Window, "PINVALUE", PublisherId, "DESKTOP-01", ["192.168.1.20:47321", "desk.local:47321"]));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("v").GetInt32());
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", root.GetProperty("cloud").GetProperty("challengeId").GetString());
        Assert.Equal("123456", root.GetProperty("cloud").GetProperty("code").GetString());
        var lan = root.GetProperty("lan");
        Assert.Equal("11111111-2222-3333-4444-555555555555", lan.GetProperty("pid").GetString());
        Assert.Equal("ECAwQFBgcICQoA", lan.GetProperty("sec").GetString());
        Assert.Equal("PINVALUE", lan.GetProperty("pin").GetString());
        Assert.Equal("8f14e45f-ceea-467e-9b4a-1f3a2b6c7d8e", lan.GetProperty("did").GetString());
        Assert.Equal("DESKTOP-01", lan.GetProperty("name").GetString());
        Assert.Equal(["192.168.1.20:47321", "desk.local:47321"],
            lan.GetProperty("ep").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public void Qr_OmitsNullBlocks()
    {
        var json = LanPairingPayload.BuildQrJson(
            cloud: null,
            new LanPairingBlock(Window, "PIN", PublisherId, "D", ["desk.local:1"]));

        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("cloud", out _));
        Assert.True(document.RootElement.TryGetProperty("lan", out _));
    }

    [Fact]
    public void Qr_RequiresAtLeastOneBlock()
    {
        Assert.Throws<ArgumentException>(() => LanPairingPayload.BuildQrJson(null, null));
    }
}
