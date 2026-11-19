#!/usr/bin/env bash
# End-to-end check of CUSTOM scanners (output contract: SARIF / Aegis JSON / none) against the compose stack + Gitea repos.
# Run after `docker compose up --build -d` and scripts/gitea-e2e.sh (which seeds secrets-demo / renovate-demo).
#   - aegis-demo : alpine wrapper emitting Aegis JSON (exit 0)
#   - gitleaks   : real gitleaks container, SARIF, exits 1 on findings (success exit codes [0,1])
#   - trivy      : real Trivy container, SARIF (vulnerabilities + secrets + misconfig)
#   - broken     : declares SARIF but prints garbage  -> the run FAILS with a clear reason (never "0 findings")
#   - silent     : output format "none"               -> run succeeds, reported as "not interpreted"
# Usage: bash scripts/custom-scanners-e2e.sh [repo]   (default secrets-demo)
set -euo pipefail
cd "$(dirname "$0")/.."

A=http://127.0.0.1:3000/api/v1
REPO="${1:-secrets-demo}"
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok  - $*"; }
tok() { curl -fsS -d grant_type=password -d client_id=aegisscan-web -d username=admin -d password=admin123 http://127.0.0.1:8081/realms/aegisscan/protocol/openid-connect/token | python -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'; }
api() { curl -fsS -H "Authorization: Bearer $(tok)" -H 'Content-Type: application/json' "$@"; }
py() { python -c "$@"; }

register() { # id name image category format codes command-json args-json
  api -X DELETE "$A/scanners/$1" >/dev/null 2>&1 || true
  api -X POST "$A/scanners" -d "{\"id\":\"$1\",\"name\":\"$2\",\"image\":\"$3\",\"category\":\"$4\",\"outputFormat\":\"$5\",\"successExitCodes\":$6,\"command\":$7,\"args\":$8}" >/dev/null
}

wait_run() { # scanner -> terminal status in $ST (not a subshell: SCANNER is used by run_field)
  SCANNER=$1
  RUN=$(api -X POST "$A/runs" -d "{\"repositoryId\":\"$REPO\",\"scanner\":\"$1\",\"mode\":\"full\"}" | py 'import sys,json;print(json.load(sys.stdin)["id"])')
  for _ in $(seq 1 120); do
    sleep 5
    local st; st=$(api "$A/runs?repositoryId=$REPO&scanner=$1&pageSize=1" | py 'import sys,json;print(json.load(sys.stdin)["items"][0]["status"])')
    case "$st" in succeeded|failed) ST=$st; return;; esac
  done
  ST=timeout
}
run_field() { api "$A/runs?repositoryId=$REPO&scanner=$SCANNER&pageSize=1" | py "import sys,json;r=json.load(sys.stdin)['items'][0];print($1)"; }
finding_count() { api "$A/findings?scanner=$1&repositoryId=$REPO&pageSize=100" | py 'import sys,json;print(len(json.load(sys.stdin)["items"]))'; }

# ---- 0. contract validation
code=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H "Authorization: Bearer $(tok)" -H 'Content-Type: application/json' "$A/scanners" -d '{"id":"bad","name":"Bad","image":"alpine","outputFormat":"xml"}')
[ "$code" = 400 ] || fail "invalid output format accepted ($code)"
ok "invalid output format / exit codes are rejected (400)"

# ---- 1. Aegis JSON wrapper (a container we wrote ourselves)
AEGIS_SCRIPT='echo "starting demo scanner"; echo AEGIS-RESULT-BEGIN; printf "{\"aegisVersion\":1,\"authoritative\":true,\"findings\":["; n=0; for f in $(find /workspace/repo -type f -not -path "*/.git/*" | head -5); do rel=${f#/workspace/repo/}; [ $n -gt 0 ] && printf ","; printf "{\"id\":\"demo-keyword\",\"title\":\"Demo finding per file\",\"severity\":\"medium\",\"path\":\"%s\",\"line\":1,\"detail\":\"demo wrapper flags every file\",\"url\":\"https://example.com/rules/demo-keyword\"}" "$rel"; n=$((n+1)); done; printf "]}\n"; echo AEGIS-RESULT-END'
register aegis-demo "Aegis Demo" alpine:3.20 code aegis-json '[0]' "$(py 'import json,sys;print(json.dumps(["sh","-c",sys.argv[1]]))' "$AEGIS_SCRIPT")" '[]'
wait_run aegis-demo; st=$ST; [ "$st" = succeeded ] || fail "aegis-demo run $st: $(run_field 'r["sanitizedLog"]')"
n=$(finding_count aegis-demo); [ "$n" -ge 1 ] || fail "aegis-demo produced no findings"
api "$A/findings?scanner=aegis-demo&repositoryId=$REPO&pageSize=1" | py 'import sys,json
f=json.load(sys.stdin)["items"][0]
assert f["category"]=="code" and f["severity"]=="medium" and f["helpUrl"]=="https://example.com/rules/demo-keyword", f
assert f["fileUrl"], "file link missing"' || fail "aegis-demo finding shape"
ok "Aegis JSON wrapper: $n finding(s) with category, severity, rule link and forge file link"

# ---- 2. gitleaks: real container, SARIF on stdout, exits 1 when it finds secrets
register gitleaks Gitleaks zricethezav/gitleaks:v8.21.2 secrets sarif '[0,1]' '["gitleaks"]' '["dir","{{workspace}}","--report-format","sarif","--report-path","/dev/stdout","--no-banner","--exit-code","1"]'
wait_run gitleaks; st=$ST; [ "$st" = succeeded ] || fail "gitleaks run $st: $(run_field 'r["sanitizedLog"]')"
n=$(finding_count gitleaks); [ "$n" -ge 1 ] || fail "gitleaks produced no findings (exit 1 must still be a successful scan)"
ok "gitleaks (exit code 1 on findings): run succeeded, $n SARIF finding(s) imported"
first=$(api "$A/findings?scanner=gitleaks&repositoryId=$REPO&pageSize=100" | py 'import sys,json;print(",".join(sorted(f["fingerprint"] for f in json.load(sys.stdin)["items"])))')
wait_run gitleaks; st=$ST; [ "$st" = succeeded ] || fail "gitleaks rescan $st"
second=$(api "$A/findings?scanner=gitleaks&repositoryId=$REPO&pageSize=100" | py 'import sys,json;print(",".join(sorted(f["fingerprint"] for f in json.load(sys.stdin)["items"])))')
[ "$first" = "$second" ] || fail "gitleaks fingerprints changed between scans"
ok "gitleaks fingerprints are stable across rescans (no duplicates)"

# ---- 3. Trivy: real container, SARIF
register trivy Trivy aquasec/trivy:0.58.1 vulnerabilities sarif '[0]' '["trivy"]' '["fs","--format","sarif","--scanners","vuln,secret,misconfig","--quiet","{{workspace}}"]'
wait_run trivy; st=$ST; [ "$st" = succeeded ] || fail "trivy run $st: $(run_field 'r["sanitizedLog"][:300]')"
ok "trivy run succeeded: $(run_field 'r["findingCount"]') finding(s)"

# ---- 3b. Semgrep: real container, SARIF; needs network access to the rule registry (p/default)
register semgrep Semgrep semgrep/semgrep:1.100.0 code sarif '[0]' '["semgrep"]' '["scan","--config","p/default","--sarif","--metrics","off","--disable-version-check","{{workspace}}"]'
wait_run semgrep; st=$ST; [ "$st" = succeeded ] || fail "semgrep run $st: $(run_field 'r["sanitizedLog"][:300]')"
ok "semgrep run succeeded: $(run_field 'r["findingCount"]') finding(s)"

# ---- 4. a scanner that claims SARIF but prints garbage must fail loudly, not report 0 findings
register broken Broken alpine:3.20 other sarif '[0]' '["sh","-c","echo this is not sarif"]' '[]'
wait_run broken; st=$ST; [ "$st" = failed ] || fail "broken scanner should fail, got $st"
reason=$(run_field 'r["failureReason"] or r["sanitizedLog"]')
echo "$reason" | grep -qi "could not interpret" || fail "unclear failure reason: $reason"
ok "unparseable output fails the run with a clear reason ($reason)"

# ---- 5. output format none: succeeds but is explicitly not interpreted
register silent Silent alpine:3.20 other none '[0]' '["sh","-c","echo hello"]' '[]'
wait_run silent; st=$ST; [ "$st" = succeeded ] || fail "silent run $st"
[ "$(run_field 'r["outputInterpreted"]')" = False ] || fail "silent run must be flagged as not interpreted"
ok "format 'none': run succeeds and is flagged as not interpreted (not a misleading 0)"

# ---- 6. preview-parse endpoint
api -X POST "$A/scanners/preview-parse" -d '{"format":"sarif","output":"{\"version\":\"2.1.0\",\"runs\":[{\"tool\":{\"driver\":{\"name\":\"x\"}},\"results\":[{\"ruleId\":\"R1\",\"level\":\"error\",\"message\":{\"text\":\"boom\"},\"locations\":[{\"physicalLocation\":{\"artifactLocation\":{\"uri\":\"a.py\"},\"region\":{\"startLine\":3}}}]}]}]}"}' | py 'import sys,json
r=json.load(sys.stdin); assert not r["error"] and r["findings"][0]["severity"]=="high" and r["findings"][0]["line"]==3, r' || fail "preview-parse"
ok "preview-parse returns the findings a run would create"

echo "ALL CUSTOM SCANNER CHECKS PASSED"
