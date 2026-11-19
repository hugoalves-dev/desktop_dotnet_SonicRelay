using Microsoft.Extensions.Time.Testing;
using SonicRelay.Windows.Lan.Pairing;

namespace SonicRelay.Windows.Lan.Tests;

public sealed class LanPairingServiceTests
{
    private const string Pin = "A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc-4E";
    private readonly FakeTimeProvider time = new(DateTimeOffset.Parse("2026-11-17T12:00:00Z"));

    private LanPairingService Create() => new(Pin, time);

    [Fact]
    public void Open_CreatesTenByteSecret_And120sTtl()
    {
        var window = Create().Open();

        Assert.Equal(10, window.Secret.Length);
        Assert.Equal(time.GetUtcNow().AddSeconds(120), window.ExpiresAt);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", window.ManualCode);
    }

    [Fact]
    public void Redeem_ValidSecret_AcceptsOnce()
    {
        var service = Create();
        var window = service.Open();

        Assert.Equal(LanRedeemResult.Accepted, service.Redeem(window.PairingId, window.Secret, manualCode: null));
        Assert.Equal(LanRedeemResult.Invalid, service.Redeem(window.PairingId, window.Secret, manualCode: null));
    }

    [Fact]
    public void Redeem_ManualCode_Accepts()
    {
        var service = Create();
        var window = service.Open();

        Assert.Equal(LanRedeemResult.Accepted, service.Redeem(null, null, window.ManualCode.ToLowerInvariant()));
    }

    [Fact]
    public void Redeem_ManualCodeForAnotherPin_Invalid()
    {
        var service = Create();
        var window = service.Open();
        var foreign = ManualPairingCode.Encode(SonicRelay.Windows.Lan.Identity.LanIdentity.ComputePin([9, 9, 9]), window.Secret);

        Assert.Equal(LanRedeemResult.Invalid, service.Redeem(null, null, foreign));
    }

    [Fact]
    public void Redeem_AfterTtl120s_Expired()
    {
        var service = Create();
        var window = service.Open();
        time.Advance(TimeSpan.FromSeconds(121));

        Assert.Equal(LanRedeemResult.Expired, service.Redeem(window.PairingId, window.Secret, null));
    }

    [Fact]
    public void Redeem_WrongPairingId_Invalid()
    {
        var service = Create();
        var window = service.Open();

        Assert.Equal(LanRedeemResult.Invalid, service.Redeem(Guid.NewGuid(), window.Secret, null));
    }

    [Fact]
    public void Redeem_FiveFailures_Exhausted()
    {
        var service = Create();
        var window = service.Open();
        var wrong = new byte[10];

        for (var i = 0; i < 4; i++)
            Assert.Equal(LanRedeemResult.Invalid, service.Redeem(window.PairingId, wrong, null));

        Assert.Equal(LanRedeemResult.Exhausted, service.Redeem(window.PairingId, wrong, null));
        Assert.Equal(LanRedeemResult.Exhausted, service.Redeem(window.PairingId, window.Secret, null));
    }

    [Fact]
    public void Redeem_WithoutOpenWindow_Invalid()
    {
        Assert.Equal(LanRedeemResult.Invalid, Create().Redeem(Guid.NewGuid(), new byte[10], null));
    }

    [Fact]
    public void Close_InvalidatesWindow()
    {
        var service = Create();
        var window = service.Open();
        service.Close();

        Assert.Equal(LanRedeemResult.Invalid, service.Redeem(window.PairingId, window.Secret, null));
        Assert.Null(service.Current);
    }

    [Fact]
    public async Task Redeem_IsAtomic_UnderConcurrency()
    {
        var service = Create();
        var window = service.Open();

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => service.Redeem(window.PairingId, window.Secret, null))));

        Assert.Equal(1, results.Count(result => result == LanRedeemResult.Accepted));
    }
}
