using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonicRelay.Windows.Lan.Pairing;

/// <summary>The cloud half of the QR: the RelayControl pairing challenge, unchanged.</summary>
public sealed record CloudPairingBlock(Guid ChallengeId, string Code);

/// <summary>The LAN half of the QR: everything a phone needs to pair offline.</summary>
public sealed record LanPairingBlock(
    LanPairingWindow Window,
    string Pin,
    Guid PublisherDeviceId,
    string Name,
    IReadOnlyList<string> Endpoints);

/// <summary>
/// Builds the version-2 pairing QR payload (issue #75): one code that pairs over the cloud,
/// the LAN, or both. Older phones only understand the bare cloud payload, which is why the
/// cloud block keeps the exact <c>challengeId</c>/<c>code</c> names.
/// </summary>
public static class LanPairingPayload
{
    public const int Version = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string BuildQrJson(CloudPairingBlock? cloud, LanPairingBlock? lan)
    {
        if (cloud is null && lan is null)
            throw new ArgumentException("A pairing payload needs a cloud or a LAN block.");

        return JsonSerializer.Serialize(new Payload(
            Version,
            cloud is null ? null : new CloudJson(cloud.ChallengeId.ToString("D"), cloud.Code),
            lan is null
                ? null
                : new LanJson(
                    lan.Window.PairingId.ToString("D"),
                    Base64Url.EncodeToString(lan.Window.Secret),
                    lan.Pin,
                    lan.PublisherDeviceId.ToString("D"),
                    lan.Name,
                    lan.Endpoints)), JsonOptions);
    }

    private sealed record Payload(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("cloud")] CloudJson? Cloud,
        [property: JsonPropertyName("lan")] LanJson? Lan);

    private sealed record CloudJson(
        [property: JsonPropertyName("challengeId")] string ChallengeId,
        [property: JsonPropertyName("code")] string Code);

    private sealed record LanJson(
        [property: JsonPropertyName("pid")] string Pid,
        [property: JsonPropertyName("sec")] string Sec,
        [property: JsonPropertyName("pin")] string Pin,
        [property: JsonPropertyName("did")] string Did,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("ep")] IReadOnlyList<string> Ep);
}
