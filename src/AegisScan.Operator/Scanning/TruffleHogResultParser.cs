using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Parses TruffleHog's `--json` output (one JSON object per line). The actual secret value
/// (`Raw`/`RawV2`) is never persisted — only a SHA-256 fingerprint, so findings are stable
/// across rescans without ever storing the discovered secret.
/// </summary>
public static class TruffleHogResultParser
{
    public static List<FindingEntity> Parse(string jsonLines, string repositoryId)
    {
        var findings = new List<FindingEntity>();
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) continue; // skip non-JSON log noise on the same stream
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                // TruffleHog also emits non-finding status/summary lines with every field null; skip those.
                if (!root.TryGetProperty("DetectorName", out var detectorProperty) || detectorProperty.ValueKind != JsonValueKind.String)
                    continue;

                var detector = detectorProperty.GetString() ?? "Unknown";
                var verified = root.TryGetProperty("Verified", out var v) && v.ValueKind == JsonValueKind.True;
                var raw = root.TryGetProperty("Raw", out var r) ? r.GetString() ?? "" : "";
                var path = ExtractFilePath(root);

                var fingerprintSource = $"{repositoryId}:{detector}:{path}:{raw}";
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource)));

                findings.Add(new FindingEntity
                {
                    Fingerprint = fingerprint,
                    RepositoryId = repositoryId,
                    Detector = detector,
                    Verified = verified,
                    SafePath = path,
                    CommitSHA = "",
                    State = "open"
                });
            }
        }
        return findings;
    }

    private static string ExtractFilePath(JsonElement root)
    {
        if (root.TryGetProperty("SourceMetadata", out var meta)
            && meta.TryGetProperty("Data", out var data)
            && data.TryGetProperty("Filesystem", out var fs)
            && fs.TryGetProperty("file", out var file))
        {
            return file.GetString() ?? "";
        }
        return "";
    }
}
