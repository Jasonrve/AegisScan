#!/usr/bin/env bash
# End-to-end check against the compose Gitea (a local GitHub stand-in). Run after `docker compose up --build -d`.
#   1. seeds Gitea + registers the repos (scripts/seed-gitea.sh)
#   2. TruffleHog on secrets-demo  -> findings with masked previews and file:line permalinks into Gitea
#   3. Renovate (read-only)        -> dependency findings, no pull requests
#   4. Renovate (PR mode)          -> pull requests + Dependency Dashboard issue really exist in Gitea and are linked from Aegis
# Exits non-zero on the first failed assertion.
set -euo pipefail
cd "$(dirname "$0")/.."

A=http://127.0.0.1:3000/api/v1
GITEA=http://127.0.0.1:3300
GU=aegis; GP='Aegis-Admin-1'
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok  - $*"; }
tok() { curl -fsS -d grant_type=password -d client_id=aegisscan-web -d username=admin -d password=admin123 http://127.0.0.1:8081/realms/aegisscan/protocol/openid-connect/token | python -c 'import sys,json;print(json.load(sys.stdin)["access_token"])'; }
api() { curl -fsS -H "Authorization: Bearer $(tok)" -H 'Content-Type: application/json' "$@"; }
py() { python -c "$@"; }

run_scan() { # repo scanner [mode] -> waits for a terminal state, prints the run id
  local id mode="${3:-incremental}"
  id=$(api -X POST "$A/runs" -d "{\"repositoryId\":\"$1\",\"scanner\":\"$2\",\"mode\":\"$mode\"}" | py 'import sys,json;print(json.load(sys.stdin)["id"])')
  for _ in $(seq 1 60); do
    sleep 5
    st=$(api "$A/runs?repositoryId=$1&scanner=$2&pageSize=1" | py 'import sys,json;print(json.load(sys.stdin)["items"][0]["status"])')
    [ "$st" = succeeded ] && { echo "$id"; return; }
    [ "$st" = failed ] && fail "$2 run on $1 failed: $(api "$A/runs/$id/logs" | py 'import sys,json;print(json.load(sys.stdin)["logs"][:400])')"
  done
  fail "$2 run on $1 timed out"
}

# Clean slate in AegisScan (also exercises the cascade delete of findings/runs); the seed re-registers the repositories.
for r in secrets-demo renovate-demo; do api -X DELETE "$A/repositories/$r" >/dev/null 2>&1 || true; done
# Renovate treats a closed PR as "ignore this update" and never recreates it, so start from freshly created Gitea repositories.
for r in secrets-demo renovate-demo; do curl -s -o /dev/null -u "$GU:$GP" -X DELETE "$GITEA/api/v1/repos/$GU/$r" || true; done
bash scripts/seed-gitea.sh >/dev/null
ok "Gitea seeded and repositories registered"

api -X PUT "$A/settings" -d '{"renovateCreatePullRequests":false}' >/dev/null

# ---- TruffleHog
R=$(run_scan secrets-demo trufflehog)
api "$A/findings?repositoryId=secrets-demo&state=open&pageSize=50" | py 'import json,sys
d=json.load(sys.stdin)["items"]
assert len(d)==3, f"expected 3 secret findings, got {len(d)}"
aws=[f for f in d if f["detector"]=="AWS"][0]
assert aws["severity"]=="critical" and aws["verified"], aws
assert aws["fileUrl"].startswith("http://localhost:3300/aegis/secrets-demo/src/commit/") and aws["fileUrl"].endswith("/new_key#L2"), aws["fileUrl"]
assert aws["commitUrl"].startswith("http://localhost:3300/aegis/secrets-demo/commit/"), aws["commitUrl"]
for f in d:
    p=f["preview"]; assert "\n" not in p and len(p)<130, p
    assert "AKIAQYLPMN5HHHFPZAM2" not in p, "full key leaked into preview"
    assert "b3BlbnNzaC1rZXktdjE" not in p, "key material leaked into preview"
print("trufflehog findings ok")' || fail "TruffleHog assertions"
ok "TruffleHog: 3 findings, masked previews, file:line + commit links into Gitea"
curl -fsS -u "$GU:$GP" "$GITEA/api/v1/repos/$GU/secrets-demo/raw/new_key" >/dev/null && ok "linked file exists in Gitea"
api "$A/runs/$R/logs" | grep -q "AKIAQYLPMN5HHHFPZAM2" && fail "secret leaked into run output" || ok "run output contains no secret"

# ---- Renovate read-only
run_scan renovate-demo renovate >/dev/null
[ "$(curl -fsS -u "$GU:$GP" "$GITEA/api/v1/repos/$GU/renovate-demo/pulls?state=open" | py 'import sys,json;print(len(json.load(sys.stdin)))')" = 0 ] || fail "read-only mode opened pull requests"
api "$A/findings?repositoryId=renovate-demo&state=open&pageSize=50" | py 'import sys,json
d=json.load(sys.stdin)["items"]; assert len(d)==5, len(d)
assert all(f["detector"]=="RenovateDependency" and not f["pullRequestUrl"] for f in d)' || fail "read-only renovate assertions"
ok "Renovate read-only: 5 dependency findings, no pull requests opened"

# ---- enrichment: known vulnerabilities (OSV) and the manifest line of each dependency, checked against the real file in Gitea
api "$A/findings?repositoryId=renovate-demo&state=open&pageSize=50" > /dev/null
api "$A/findings?repositoryId=renovate-demo&state=open&pageSize=50" | py 'import sys,json,urllib.request
d=json.load(sys.stdin)["items"]
vuln=[f for f in d if f["advisories"]]
assert vuln, "no advisories found (OSV unreachable from the API container?)"
m=[f for f in d if f["detail"].startswith("minimist")][0]
assert any(a["id"] for a in m["advisories"]) and all(a["url"].startswith("https://osv.dev/vulnerability/") for a in m["advisories"]), m["advisories"]
assert m["severity"]=="high", m["severity"]
raw=urllib.request.urlopen("http://127.0.0.1:3300/aegis/renovate-demo/raw/branch/main/package.json").read().decode().split(chr(10))
for f in d:
    n=f["sourceLine"]; assert n and f["fileUrl"].endswith("#L%d"%n), (f["detail"], n, f["fileUrl"])
    assert f["detail"].split()[0] in raw[n-1], (f["detail"], n, raw[n-1])
print("enrichment ok:", len(vuln), "vulnerable of", len(d))' || fail "advisories / line anchors"
ok "Renovate findings carry known vulnerabilities (OSV, severity high) and a line anchor that points at the dependency in the real manifest"

# ---- Renovate PR mode
api -X PUT "$A/settings" -d '{"renovateCreatePullRequests":true}' >/dev/null
R=$(run_scan renovate-demo renovate)
PRS=$(curl -fsS -u "$GU:$GP" "$GITEA/api/v1/repos/$GU/renovate-demo/pulls?state=open&limit=50" | py 'import sys,json;print(len(json.load(sys.stdin)))')
[ "$PRS" -ge 5 ] || fail "expected >=5 open pull requests in Gitea, got $PRS"
curl -fsS -u "$GU:$GP" "$GITEA/api/v1/repos/$GU/renovate-demo/issues?state=open&type=issues&q=Dependency%20Dashboard" | grep -q "Dependency Dashboard" || fail "no Dependency Dashboard issue in Gitea"
ok "Gitea has $PRS open Renovate pull requests and a Dependency Dashboard issue"
api "$A/runs?repositoryId=renovate-demo&scanner=renovate&pageSize=1" | py 'import sys,json,urllib.request
r=json.load(sys.stdin)["items"][0]
kinds=[l["kind"] for l in r["links"]]
assert "issue" in kinds and kinds.count("pull_request")>=5, kinds
for l in r["links"][:3]:
    assert l["url"].startswith("http://localhost:3300/aegis/renovate-demo/"), l
    assert urllib.request.urlopen(l["url"]).status==200, l
print("run links ok")' || fail "run links assertions"
ok "Aegis run links (issue + pull requests) resolve in Gitea"
api "$A/findings?repositoryId=renovate-demo&state=open&pageSize=50" | py 'import sys,json
d=json.load(sys.stdin)["items"]
assert all(f["pullRequestNumber"] and f["pullRequestUrl"] for f in d), [(f["detail"],f["pullRequestNumber"]) for f in d]
assert not [f for f in d if f["detector"]=="RenovateConfig"], "environment noise became a finding"' || fail "finding<->PR assertions"
ok "every dependency finding links to its pull request; no noise findings"

# ---- idempotency: a second PR-mode run must not create duplicates
run_scan renovate-demo renovate >/dev/null
PRS2=$(curl -fsS -u "$GU:$GP" "$GITEA/api/v1/repos/$GU/renovate-demo/pulls?state=open&limit=50" | py 'import sys,json;print(len(json.load(sys.stdin)))')
[ "$PRS2" = "$PRS" ] || fail "second run changed the pull request count ($PRS -> $PRS2)"
ok "second Renovate run is idempotent ($PRS2 pull requests)"

# ---- shared git cache (ADR 0001): the mirror for each repository lives on the PVC and the repository is marked warm
cat <<'YAML' | docker compose exec -T k3s kubectl apply -f - >/dev/null
apiVersion: v1
kind: Pod
metadata: {name: cache-peek, namespace: default}
spec:
  restartPolicy: Never
  containers:
  - {name: peek, image: busybox:1.36, command: ["sh","-c","ls /cache"], volumeMounts: [{name: c, mountPath: /cache}]}
  volumes: [{name: c, persistentVolumeClaim: {claimName: aegisscan-git-cache}}]
YAML
for _ in $(seq 1 30); do ph=$(docker compose exec -T k3s kubectl get pod cache-peek -o jsonpath='{.status.phase}'); [ "$ph" = Succeeded ] && break; sleep 3; done
CACHE=$(docker compose exec -T k3s kubectl logs cache-peek); docker compose exec -T k3s kubectl delete pod cache-peek --now >/dev/null
echo "$CACHE" | grep -q "secrets-demo.git" && echo "$CACHE" | grep -q "renovate-demo.git" || fail "git cache PVC has no mirrors: $CACHE"
api "$A/repositories?pageSize=100" | py 'import sys,json
r={x["id"]:x for x in json.load(sys.stdin)["items"]}
assert r["secrets-demo"]["cacheState"]=="warm", r["secrets-demo"]["cacheState"]' || fail "repository not marked warm"
ok "shared git cache: bare mirrors for both repositories on the PVC, repositories marked warm"

# ---- git-history scanning: a secret deleted at HEAD is still found, with the commit that introduced it
W=$(mktemp -d)
git clone -q "http://$GU:$GP@127.0.0.1:3300/$GU/secrets-demo.git" "$W/c" && (cd "$W/c" && git rm -q new_key && git -c user.name=Aegis -c user.email=aegis@example.test commit -q -m "remove key" && git push -q origin main)
FIRST=$(git -C "$W/c" rev-list --max-parents=0 HEAD); rm -rf "$W"
run_scan secrets-demo trufflehog >/dev/null    # incremental = filesystem scan: the key is gone from the files, but a deleted secret is NOT a fixed secret
api "$A/findings?repositoryId=secrets-demo&pageSize=50" | py 'import sys,json
d=[f for f in json.load(sys.stdin)["items"] if f["detector"]=="AWS"][0]; assert d["state"]=="open", d["state"]' || fail "an incremental scan resolved a secret that merely left the latest files"
run_scan secrets-demo trufflehog full >/dev/null
api "$A/findings?repositoryId=secrets-demo&pageSize=50" | FIRST="$FIRST" py 'import sys,json,os,urllib.request
d=[f for f in json.load(sys.stdin)["items"] if f["detector"]=="AWS"][0]
assert d["state"]=="open", d["state"]
assert d["introducedCommit"]==os.environ["FIRST"], (d["introducedCommit"], os.environ["FIRST"])
assert d["introducedCommit"]!=d["commitSHA"], "introducing commit must differ from the scanned HEAD"
assert d["fileUrl"].startswith("http://localhost:3300/aegis/secrets-demo/src/commit/"+os.environ["FIRST"]), d["fileUrl"]
assert urllib.request.urlopen(d["fileUrl"].split("#")[0]).status==200
print("git history ok")' || fail "git-history assertions"
ok "git-history scan: deleted secret found again, introducing commit recorded, file link resolves at that commit"

# ---- an incremental scan must not resolve a secret that only history can vouch for
run_scan secrets-demo trufflehog >/dev/null
api "$A/findings?repositoryId=secrets-demo&pageSize=50" | py 'import sys,json
d=[f for f in json.load(sys.stdin)["items"] if f["detector"]=="AWS"][0]; assert d["state"]=="open", d["state"]' || fail "incremental scan resolved a history finding"
ok "incremental scans keep history-only findings open (no flip-flop)"

# ---- an operator re-posting a blocked repository must not clear the block (unblock is admin-only)
OT=$(curl -fsS -d grant_type=password -d client_id=aegisscan-web -d username=operator -d password=operator123 http://127.0.0.1:8081/realms/aegisscan/protocol/openid-connect/token | py 'import sys,json;print(json.load(sys.stdin)["access_token"])')
api "$A/repositories?pageSize=100" | py 'import sys,json
assert [r for r in json.load(sys.stdin)["items"] if r["id"]=="secrets-demo"][0]["status"]=="Blocked"' || fail "precondition: secrets-demo should be blocked"
curl -fsS -H "Authorization: Bearer $OT" -H 'Content-Type: application/json' -X POST "$A/repositories" -d '{"id":"secrets-demo","name":"secrets-demo","provider":"gitea","cloneUrl":"http://gitea:3000/aegis/secrets-demo.git","defaultBranch":"main"}' >/dev/null
api "$A/repositories?pageSize=100" | py 'import sys,json
assert [r for r in json.load(sys.stdin)["items"] if r["id"]=="secrets-demo"][0]["status"]=="Blocked"' || fail "operator cleared the block"
[ "$(curl -s -o /dev/null -w '%{http_code}' -H "Authorization: Bearer $OT" -X POST "$A/repositories/secrets-demo/unblock" -d '')" = 403 ] || fail "operator could unblock"
ok "blocked status survives an operator re-post; unblock stays admin-only (403 for operators)"

# ---- CRD reconcile must not lift a block, and an operator cannot delete (launder) a blocked repository
cat <<'YAML' | docker compose exec -T k3s kubectl apply -f - >/dev/null
apiVersion: aegisscan.io/v1alpha1
kind: Repository
metadata: {name: e2e-crd-block, namespace: default}
spec: {provider: github, cloneURL: "https://github.com/trufflesecurity/test_keys.git"}
YAML
for _ in $(seq 1 30); do api "$A/repositories?pageSize=100" | grep -q e2e-crd-block && break; sleep 2; done
docker compose exec -T postgres psql -U aegisscan -d aegisscan -qc "UPDATE \"Repositories\" SET \"Status\" = 'Blocked' WHERE \"Id\" = 'e2e-crd-block';" >/dev/null
docker compose exec -T k3s kubectl annotate repositories.aegisscan.io e2e-crd-block "touched=$(date +%s)" --overwrite >/dev/null
sleep 10   # the annotation change triggers a reconcile, which previously reset the row to Ready
api "$A/repositories?pageSize=100" | py 'import sys,json
r=[x for x in json.load(sys.stdin)["items"] if x["id"]=="e2e-crd-block"][0]; assert r["status"]=="Blocked", r["status"]' || fail "CRD reconcile lifted the block"
[ "$(curl -s -o /dev/null -w '%{http_code}' -X DELETE -H "Authorization: Bearer $OT" "$A/repositories/e2e-crd-block")" = 403 ] || fail "operator could delete a blocked repository"
docker compose exec -T k3s kubectl delete repositories.aegisscan.io e2e-crd-block >/dev/null
ok "a CRD reconcile keeps Blocked; operators cannot delete a blocked repository (403)"

# ---- settings audit records before/after values (the signing secret only as "changed")
api -X PUT "$A/settings" -d '{"maxConcurrency":5,"webhookSecret":"audit-check"}' >/dev/null
api "$A/audit?pageSize=5" | py 'import sys,json
e=[x for x in json.load(sys.stdin)["items"] if x["action"]=="settings.update"][0]
m=json.loads(e["metadata"]); assert m["maxConcurrency"]["to"]==5 and "from" in m["maxConcurrency"], m
assert m["webhookSecret"]=="changed" and "audit-check" not in e["metadata"], m' || fail "settings audit"
api -X PUT "$A/settings" -d '{"maxConcurrency":4,"webhookSecret":""}' >/dev/null
ok "settings changes are audited with before/after values; the secret is never written to the audit log"

# ---- assignee, SLA (overdue) and MTTR
FP=$(api "$A/findings?repositoryId=secrets-demo&state=open&pageSize=20" | py 'import sys,json;print([f for f in json.load(sys.stdin)["items"] if f["detector"]=="AWS"][0]["fingerprint"])')
api -X PUT "$A/findings/$FP/assignee" -d '{"assignee":"alice@example.test"}' | py 'import sys,json;assert json.load(sys.stdin)["assignee"]=="alice@example.test"' || fail "assign"
docker compose exec -T postgres psql -U aegisscan -d aegisscan -qc "UPDATE \"Findings\" SET \"FirstSeenAt\" = now() - interval '10 days' WHERE \"Fingerprint\" = '$FP';" >/dev/null
api "$A/findings?repositoryId=secrets-demo&state=open&pageSize=20" | FP="$FP" py 'import sys,json,os
f=[x for x in json.load(sys.stdin)["items"] if x["fingerprint"]==os.environ["FP"]][0]
assert f["overdue"] is True and f["severity"]=="critical", (f["overdue"], f["severity"])' || fail "SLA overdue"
api "$A/findings/stats" | py 'import sys,json
s=json.load(sys.stdin); assert s["overdue"]>=1, s; assert s["mttrDays"] is not None, s' || fail "stats/MTTR"
ok "assignee saved, 10-day-old finding is overdue against the 7/30/90/180-day SLA, MTTR computed from resolved findings"

# ---- alert acknowledge / snooze lifecycle
api -X PUT "$A/alerts/acks" -d '{"key":"run:e2e-test"}' >/dev/null
api -X PUT "$A/alerts/acks" -d '{"key":"run:e2e-snooze","snoozeHours":1}' >/dev/null
api "$A/alerts/acks" | py 'import sys,json
k={a["key"]:a for a in json.load(sys.stdin)["items"]}
assert "run:e2e-test" in k and k["run:e2e-snooze"]["snoozeUntil"], k' || fail "alert acks"
api -X DELETE "$A/alerts/acks?key=run:e2e-test" >/dev/null; api -X DELETE "$A/alerts/acks?key=run:e2e-snooze" >/dev/null
[ "$(api "$A/alerts/acks" | py 'import sys,json;print(len([a for a in json.load(sys.stdin)["items"] if a["key"].startswith("run:e2e")]))')" = 0 ] || fail "ack delete"
ok "alerts can be acknowledged, snoozed and reopened (stored server-side)"

# ---- policy governance: ScanPolicy resources are listed
cat <<'YAML' | docker compose exec -T k3s kubectl apply -f - >/dev/null
apiVersion: aegisscan.io/v1alpha1
kind: ScanPolicy
metadata: {name: e2e-policy, namespace: default}
spec: {scanners: [trufflehog, renovate], schedule: "0 2 * * *", fullScanSchedule: "0 3 * * 0"}
YAML
for _ in $(seq 1 20); do api "$A/policies" | grep -q e2e-policy && break; sleep 2; done
api "$A/policies" | py 'import sys,json
p=[x for x in json.load(sys.stdin)["items"] if x["name"]=="e2e-policy"][0]
assert p["scanners"]==["trufflehog","renovate"] and p["fullScanSchedule"]=="0 3 * * 0" and p["phase"]=="Ready", p' || fail "policies"
docker compose exec -T k3s kubectl delete scanpolicy e2e-policy >/dev/null
ok "ScanPolicy resources listed with schedules and status"

# ---- signed webhook: a receiver container on the compose network records the request; the signature must verify
docker rm -f aegis-hook >/dev/null 2>&1 || true
HOOK_PY='
import http.server, json
class H(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers["Content-Length"]))
        open("/tmp/hook.json", "w").write(json.dumps({"sig": self.headers.get("X-Aegis-Signature"), "body": body.decode()}))
        self.send_response(200); self.end_headers()
    def log_message(self, *a): pass
http.server.HTTPServer(("", 9000), H).serve_forever()'
docker run -d --rm --name aegis-hook --network aegisscan_default python:3.12-alpine python -c "$HOOK_PY" >/dev/null
sleep 3
api -X PUT "$A/settings" -d '{"webhookUrl":"http://aegis-hook:9000/hook","webhookSecret":"s3cret-e2e"}' >/dev/null
api "$A/settings" | py 'import sys,json;assert json.load(sys.stdin)["webhookSecret"]=="********"' || fail "webhook secret must be masked on read"
api -X POST "$A/webhook/test" -d "" | py 'import sys,json;assert json.load(sys.stdin)["ok"] is True' || fail "webhook test delivery"
docker exec aegis-hook sh -c 'cat /tmp/hook.json' | py 'import sys,json,hmac,hashlib
d=json.load(sys.stdin); want="sha256="+hmac.new(b"s3cret-e2e", d["body"].encode(), hashlib.sha256).hexdigest()
assert d["sig"]==want, (d["sig"], want); assert json.loads(d["body"])["event"]=="webhook.test"' || fail "webhook signature"
api -X PUT "$A/settings" -d '{"webhookUrl":"","webhookSecret":""}' >/dev/null; docker rm -f aegis-hook >/dev/null
ok "webhook delivered with a verifiable HMAC-SHA256 signature; the secret is write-only"

# ---- per-provider forge tokens: Gitea uses renovate-token-gitea; a GitHub repo without its own token fails with a clear message
api -X PUT "$A/settings" -d '{"renovateCreatePullRequests":true}' >/dev/null
api -X POST "$A/repositories" -d '{"id":"e2e-github","name":"e2e-github","provider":"github","cloneUrl":"https://github.com/aegis-e2e/none.git","defaultBranch":"main"}' >/dev/null
api -X POST "$A/runs" -d '{"repositoryId":"e2e-github","scanner":"renovate"}' >/dev/null
for _ in $(seq 1 20); do sleep 3; st=$(api "$A/runs?repositoryId=e2e-github&pageSize=1" | py 'import sys,json;print(json.load(sys.stdin)["items"][0]["status"])'); [ "$st" = failed ] && break; done
api "$A/runs?repositoryId=e2e-github&pageSize=1" | py 'import sys,json
r=json.load(sys.stdin)["items"][0]; assert r["status"]=="failed" and "renovate-token-github" in r["sanitizedLog"], r["sanitizedLog"]' || fail "missing per-provider token message"
api -X DELETE "$A/repositories/e2e-github" >/dev/null
run_scan renovate-demo renovate >/dev/null
ok "per-provider token: Gitea run works with renovate-token-gitea; a GitHub repo without a token fails clearly"
api -X PUT "$A/settings" -d '{"renovateCreatePullRequests":false}' >/dev/null
echo "ALL GITEA E2E CHECKS PASSED"
