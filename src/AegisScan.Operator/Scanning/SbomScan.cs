using System.Text.RegularExpressions;

namespace AegisScan.KubeOps.Scanning;

/// <summary>
/// The built-in "sbom" scanner (Trivy): one CycloneDX SBOM for the repository checkout plus one per container image linked to it
/// (manually entered, or detected in Dockerfiles / compose / Kubernetes manifests). An SBOM already published next to the image in its registry
/// (ECR, GHCR, ... via the OCI referrers API) is used (`--sbom-sources oci`); otherwise Trivy generates one from the image layers.
/// </summary>
public static partial class SbomScan
{
    public const string ScannerId = "sbom";
    public const int MaxImages = 25;
    /// <summary>Keys of the optional Secret <see cref="ScanJobFactory.CredentialsSecretName"/> handed to this scanner only, as registry credentials.</summary>
    public static readonly (string Key, string Env)[] RegistryCredentials =
    [
        ("aws-access-key-id", "AWS_ACCESS_KEY_ID"), ("aws-secret-access-key", "AWS_SECRET_ACCESS_KEY"), ("aws-session-token", "AWS_SESSION_TOKEN"), ("aws-region", "AWS_REGION"),
        ("registry-username", "TRIVY_USERNAME"), ("registry-password", "TRIVY_PASSWORD")
    ];

    /// <summary>Image references are user input and end up in a shell loop: only plain reference characters, never a leading '-'. Mirrors valid() in <see cref="Script"/>.</summary>
    public static bool IsValidImageRef(string value) => ImageRef().IsMatch(value);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@-]{0,254}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRef();

    // Output protocol (parsed by SbomResultParser): per artifact either
    //   AEGIS-SBOM-BEGIN <kind> <origin> <source> <ref>   + gzip|base64 CycloneDX lines +   AEGIS-SBOM-END
    //   AEGIS-SBOM-FAILED <kind> <origin> <ref> <reason>
    // Compressed because Kubernetes rotates container logs: a multi-MB document printed raw can lose its beginning without any error.
    public const string Script = """
        set -u
        W=/workspace/repo; T=/tmp/sbom; mkdir -p "$T"
        valid() { case "$1" in ''|-*|*[!A-Za-z0-9._:/@-]*|scratch) return 1;; esac; [ "${#1}" -le 255 ]; }
        scan() { # kind origin ref command...
          kind=$1; origin=$2; ref=$3; shift 3
          if "$@" </dev/null >"$T/bom.json" 2>"$T/err" && [ -s "$T/bom.json" ]; then
            if grep -q 'Found SBOM' "$T/err"; then src=registry; else src=generated; fi
            echo "AEGIS-SBOM-BEGIN $kind $origin $src $ref"; gzip -c "$T/bom.json" | base64; echo "AEGIS-SBOM-END"
          else
            echo "AEGIS-SBOM-FAILED $kind $origin $ref $(tail -n 1 "$T/err" | cut -c1-200 | tr -d '\r\n')"
          fi
        }
        scan repository - repository trivy fs --no-progress --cache-dir /tmp/trivy --scanners vuln --format cyclonedx "$W"
        {
          find "$W" -path '*/.git' -prune -o -type f \( -name 'Dockerfile*' -o -name '*.Dockerfile' -o -name Containerfile \) -print | while IFS= read -r f; do
            awk 'tolower($1)=="from"{i=2; if ($i ~ /^--/) i++; imgs[++n]=$i; if (tolower($(i+1))=="as") al[$(i+2)]=1} END{for (k=1;k<=n;k++) if (!(imgs[k] in al)) print imgs[k]}' "$f"
          done
          find "$W" -path '*/.git' -prune -o -type f \( -name '*.yaml' -o -name '*.yml' \) -print | while IFS= read -r f; do
            sed -n 's/^[[:space:]-]*image:[[:space:]]*//p' "$f" | sed 's/[[:space:]#].*//' | tr -d "\"'"
          done
        } | sort -u | sed 's/^/detected /' > "$T/list"
        { printf '%s\n' "${AEGIS_IMAGES:-}" | sed '/^$/d; s/^/manual /'; cat "$T/list"; } | awk '!seen[$2]++' | head -n 40 | while read -r origin img; do
          valid "$img" || continue
          scan image "$origin" "$img" trivy image --no-progress --cache-dir /tmp/trivy --sbom-sources oci --scanners vuln --format cyclonedx "$img"
        done
        exit 0
        """;
}
