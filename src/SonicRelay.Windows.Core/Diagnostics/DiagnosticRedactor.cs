using System.Text.RegularExpressions;

namespace SonicRelay.Windows.Core.Diagnostics;

public static partial class DiagnosticRedactor
{
    private const string Redacted = "[REDACTED]";

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;

        var result = SensitiveAssignment().Replace(value, match => $"{match.Groups[1].Value}={Redacted}");
        result = BearerToken().Replace(result, $"Bearer {Redacted}");
        // LAN pairing (issue #75): the device credential header, JSON secrets and typed codes.
        result = LanAuthorization().Replace(result, $"SonicRelay-Lan {Redacted}");
        result = JsonSecret().Replace(result, match => $"\"{match.Groups[1].Value}\":\"{Redacted}\"");
        result = ManualPairingCode().Replace(result, Redacted);
        result = Jwt().Replace(result, Redacted);
        result = Email().Replace(result, Redacted);
        result = SdpPayload().Replace(result, match => $"{match.Groups[1].Value}={Redacted}");
        result = IceCandidate().Replace(result, Redacted);
        return result;
    }

    public static string MaskIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "[not set]";
        var compact = value.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        return compact.Length <= 8 ? Redacted : $"{compact[..4]}…{compact[^4..]}";
    }

    public static string BackendHost(Uri? backend)
    {
        if (backend is null || !backend.IsAbsoluteUri) return "[not configured]";
        return $"{backend.Scheme}://{backend.Host}{(backend.IsDefaultPort ? string.Empty : $":{backend.Port}")}";
    }

    public static bool IsSensitiveKey(string key) => SensitiveKey().IsMatch(key);

    [GeneratedRegex(@"(?i)(?<![a-z0-9_-])(password|access[_-]?token|refresh[_-]?token|token|credential[_-]?secret|qr[_-]?payload|pairing[_-]?code|session[_-]?code|code|credential|sec)\s*=\s*[^\s&]+")]
    private static partial Regex SensitiveAssignment();

    [GeneratedRegex(@"(?i)^(password|access[_-]?token|refresh[_-]?token|token|authorization|credential[_-]?secret|qr[_-]?payload|pairing[_-]?code|session[_-]?code|code|credential|sec|sdp|ice[_-]?candidate)$")]
    private static partial Regex SensitiveKey();

    [GeneratedRegex(@"(?i)\bSonicRelay-Lan\s+[^\s,;]+")]
    private static partial Regex LanAuthorization();

    [GeneratedRegex(@"(?i)""(credential|sec|code)""\s*:\s*""[^""]*""")]
    private static partial Regex JsonSecret();

    [GeneratedRegex(@"(?i)\b[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}\b")]
    private static partial Regex ManualPairingCode();

    [GeneratedRegex(@"(?i)\bbearer\s+[^\s,;]+")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?i)\b(sdp)\s*=\s*.*?(?=\s+(?:candidate:|[a-z_]+\s*=)|$)")]
    private static partial Regex SdpPayload();

    [GeneratedRegex(@"(?i)candidate:[^\r\n]+")]
    private static partial Regex IceCandidate();
}
