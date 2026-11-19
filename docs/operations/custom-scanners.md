# Custom scanners

A custom scanner is any container image. Aegis clones the repository (from the shared git cache) into `{{workspace}}` (`/workspace/repo`, also the working directory), runs your container as a non-root user with a read-only root filesystem (`HOME=/tmp`, writable `/tmp`), and interprets what it prints to **stdout**. Scanners never clone anything themselves.

## Output contract

Set per scanner (Add scanner / the scanner's Settings tab, or `outputFormat`, `category`, `successExitCodes` on `POST /api/v1/scanners`):

| Field | Values | Meaning |
|---|---|---|
| `outputFormat` | `sarif`, `aegis-json`, `none` | How stdout is read. `none`: the run succeeds but shows **Not interpreted** (never "0 findings"). |
| `category` | `secrets`, `dependencies`, `vulnerabilities`, `code`, `iac`, `other` | Wording, and auto-resolve semantics: findings in `secrets` are only auto-resolved by *full* scans (a deleted secret still lives in git history). |
| `successExitCodes` | e.g. `[0,1]` | Exit codes that mean "the scan ran". Many tools exit 1 when they find something (gitleaks, `trivy --exit-code 1`, `semgrep --error`). Anything else, or no exit code (clone failure), is a failed run and is retried. |

Output that cannot be interpreted fails the run (`error: could not interpret scanner output: ...`) without retries. The JSON document may be surrounded by log noise: Aegis uses the whole log, else text between `AEGIS-RESULT-BEGIN` / `AEGIS-RESULT-END` lines, else the first line-start `{` that parses and contains `runs` (SARIF) / `aegisVersion`.

**Preview parsing** (scanner editor) or `POST /api/v1/scanners/preview-parse {format, output, category}` shows the findings Aegis would create from a pasted sample, nothing is stored.

### SARIF 2.1.0
Mapped per result: `ruleId` → detector/title, severity from `properties.security-severity` (≥9 critical, ≥7 high, ≥4 medium, else low) or `level` (error → high, warning → medium, note → low; `secrets` is never below high), `physicalLocation` → `path:line` (with a forge permalink at the scanned commit), `message` → detail, rule `helpUri` (http/https only) → documentation link. Fingerprint = `partialFingerprints` or a hash of rule + path + message (stable across rescans). Raw `snippet` text is **never stored**, only a masked one-line preview. Limits: 5 000 findings and 20 MB of log per run.

### Aegis JSON v1 (for your own wrapper scripts)
```json
{"aegisVersion":1,"authoritative":true,
 "findings":[{"id":"rule-id","title":"Readable title","severity":"critical|high|medium|low",
              "path":"src/app.py","line":7,"detail":"what is wrong","url":"https://docs/rule",
              "verified":false,"preview":"masked excerpt"}]}
```
`authoritative: true` means "this run saw everything": findings no longer reported are auto-resolved. `verified: true` participates in repository blocking like TruffleHog verified secrets.

## Copy-paste definitions
| Scanner | image | command | args | format / codes |
|---|---|---|---|---|
| Gitleaks | `zricethezav/gitleaks:v8.21.2` | `gitleaks` | `dir`, `{{workspace}}`, `--report-format`, `sarif`, `--report-path`, `/dev/stdout`, `--no-banner`, `--exit-code`, `1` | sarif, `[0,1]`, secrets |
| Trivy | `aquasec/trivy:0.58.1` | `trivy` | `fs`, `--format`, `sarif`, `--scanners`, `vuln,secret,misconfig`, `--quiet`, `{{workspace}}` | sarif, `[0]`, vulnerabilities |
| Semgrep | `semgrep/semgrep:1.100.0` | `semgrep` | `scan`, `--config`, `p/default`, `--sarif`, `--metrics`, `off`, `--disable-version-check`, `{{workspace}}` | sarif, `[0]` (or `[0,1]` with `--error`), code |
| Wrapper | `alpine:3.20` | `sh`, `-c`, `<script that prints Aegis JSON>` | | aegis-json, `[0]` |

Tools that only write a file: wrap them, `sh -c 'tool --out /tmp/r.sarif; cat /tmp/r.sarif'` (needs a shell in the image). Each custom scanner appears in the sidebar, the findings filter and the run history automatically.

Verified end-to-end by `scripts/custom-scanners-e2e.sh` (real gitleaks, Trivy and Semgrep containers in k3s against the Gitea repos) and `web/e2e/custom-scanners.spec.ts`.
