using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AegisScan.KubeOps.Data;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// Parses TruffleHog's `--json` output (one JSON object per line). The actual secret value
/// (`Raw`/`RawV2`) is never persisted — only a SHA-256 fingerprint, so findings are stable
/// across rescans without ever storing the discovered secret. The persisted run output is rebuilt from
/// parsed fields for the same reason: raw finding lines are never copied into it.
/// </summary>
public static class TruffleHogResultParser
{
    private const string WorkspacePrefix = ScanJobFactory.WorkspacePath + "/";

    public static ScanParseResult Parse(string jsonLines, string repositoryId)
    {
        var findings = new List<FindingEntity>();
        var output = new StringBuilder();
        var finished = false;
        foreach (var line in jsonLines.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.TrimStart().StartsWith('{')) { output.AppendLine(line); continue; } // non-JSON log noise
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { output.AppendLine(line); continue; }
            using (doc)
            {
                var root = doc.RootElement;
                // TruffleHog also emits non-finding status/summary lines with every field null; keep only their message.
                if (!root.TryGetProperty("DetectorName", out var detectorProperty) || detectorProperty.ValueKind != JsonValueKind.String)
                {
                    if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.String)
                    {
                        output.AppendLine(msg.GetString());
                        // TruffleHog logs this only after the whole source was scanned; an empty/truncated log (pod already
                        // reaped, rotated logs) must never be read as "no findings, everything was fixed".
                        if (msg.GetString() == "finished scanning") finished = true;
                    }
                    continue;
                }

                var detector = detectorProperty.GetString() ?? "Unknown";
                var verified = root.TryGetProperty("Verified", out var v) && v.ValueKind == JsonValueKind.True;
                var raw = root.TryGetProperty("Raw", out var r) ? r.GetString() ?? "" : "";
                var path = ExtractFilePath(root);
                var lineNumber = ExtractLine(root);
                // "Redacted" is TruffleHog's own masked rendering of the match; the raw value (Raw/RawV2) is never kept.
                var preview = MaskPreview(root.TryGetProperty("Redacted", out var red) && red.ValueKind == JsonValueKind.String ? red.GetString() ?? "" : "");

                // Fingerprint keeps the original workspace-qualified path so findings recorded before
                // paths were shortened for display keep the same identity on rescan.
                var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{repositoryId}:{detector}:{path}:{raw}")));
                var safePath = path.StartsWith(WorkspacePrefix, StringComparison.Ordinal) ? path[WorkspacePrefix.Length..] : path;

                findings.Add(new FindingEntity
                {
                    Fingerprint = fingerprint,
                    RepositoryId = repositoryId,
                    Scanner = "trufflehog",
                    Detector = detector,
                    Verified = verified,
                    SafePath = safePath,
                    Detail = lineNumber is null ? "" : $"line {lineNumber}",
                    Preview = preview,
                    IntroducedCommit = GitCommit(root),
                    State = "open"
                });
                output.AppendLine($"{detector} ({(verified ? "verified" : "unverified")}) in {safePath}{(lineNumber is null ? "" : $":{lineNumber}")}");
            }
        }
        output.AppendLine($"{findings.Count} finding(s) reported.");
        return new ScanParseResult(findings, Authoritative: finished, ScanOutputSanitizer.Sanitize(output.ToString()));
    }

    /// <summary>
    /// TruffleHog's "Redacted" field is not reliably redacted (a private key comes through as its first lines, an AWS key id in full), so it is
    /// reduced to one line and masked: PEM headers are kept, already-masked values (containing ****) are kept, anything else shows only a short
    /// prefix and its length.
    /// </summary>
    public static string MaskPreview(string redacted)
    {
        var line = redacted.Split('\n', 2)[0].Trim();
        if (line.Length == 0) return "";
        if (line.StartsWith("-----BEGIN", StringComparison.Ordinal) || line.Contains("****", StringComparison.Ordinal))
            return line.Length <= 120 ? line : line[..120] + "…";
        var keep = Math.Min(6, line.Length / 3);
        return $"{line[..keep]}…  ({line.Length} chars)";
    }

    /// <summary>`filesystem` scans report SourceMetadata.Data.Filesystem; `git` (history) scans report SourceMetadata.Data.Git with the introducing commit.</summary>
    private static JsonElement? Source(JsonElement root, string kind) =>
        root.TryGetProperty("SourceMetadata", out var meta)
        && meta.TryGetProperty("Data", out var data)
        && data.TryGetProperty(kind, out var source) ? source : null;

    // Git findings carry a repo-relative path; it is rebuilt as the workspace path so the fingerprint equals the one a filesystem scan of the same file produces.
    private static string ExtractFilePath(JsonElement root) =>
        Source(root, "Filesystem") is { } fs && fs.TryGetProperty("file", out var file) ? file.GetString() ?? ""
        : Source(root, "Git") is { } git && git.TryGetProperty("file", out var gitFile) ? WorkspacePrefix + (gitFile.GetString() ?? "") : "";

    private static long? ExtractLine(JsonElement root) =>
        (Source(root, "Filesystem") ?? Source(root, "Git")) is { } source && source.TryGetProperty("line", out var l) && l.TryGetInt64(out var n) && n > 0 ? n : null;

    private static string GitCommit(JsonElement root) =>
        Source(root, "Git") is { } git && git.TryGetProperty("commit", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
}
