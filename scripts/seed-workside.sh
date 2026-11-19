#!/usr/bin/env bash
# Starts the compose stack with the GitHub organisation Workside-Security configured as a repository source (about 100 public
# forks, see scripts/workside/), links container images to a few repositories and queues scans on a showcase subset, so every
# view (findings, SBOMs, runs, alerts, scorecards) has data.
#   bash scripts/seed-workside.sh [--no-build] [--scans N] [--all]
#     --no-build  do not rebuild images (default: docker compose up --build -d)
#     --scans N   queue trufflehog + renovate + sbom on the first N showcase repositories (default 8)
#     --all       queue the three scanners on every discovered repository (hundreds of Jobs; a vulnerability DB download per SBOM run)
#   AEGIS_API_PORT / AEGIS_WEB_PORT override the host ports (defaults 8080 / 3000; the web port must be one the Keycloak realm allows: 3000 or 3100).
#   GITHUB_TOKEN=<token> optional: stored as `github-token` so Renovate can look up GitHub-hosted dependencies without hitting rate limits.
# Dev only: throwaway credentials from deploy/dev/keycloak. Requires: docker compose, curl, python.
set -euo pipefail
cd "$(dirname "$0")/.."

BUILD=--build; SCANS=8; ALL=0
while [ $# -gt 0 ]; do
  case "$1" in --no-build) BUILD="";; --scans) SCANS="$2"; shift;; --all) ALL=1;; *) echo "unknown option $1" >&2; exit 2;; esac; shift
done
AEGIS="http://127.0.0.1:${AEGIS_API_PORT:-8080}"; KC=http://127.0.0.1:8081; WEB="http://localhost:${AEGIS_WEB_PORT:-3000}"
K="docker compose exec -T k3s kubectl"
EXPECTED=$(wc -l < scripts/workside/repos.txt | tr -d ' ')

echo "==> Starting the stack"
docker compose up $BUILD -d
for _ in $(seq 1 90); do curl -fsS "$AEGIS/readyz" >/dev/null 2>&1 && break; sleep 2; done
curl -fsS "$AEGIS/readyz" >/dev/null || { echo "AegisScan did not become ready" >&2; exit 1; }

echo "==> Shared git cache + organisation source"
$K apply -f - < deploy/dev/git-cache.yaml >/dev/null
$K apply -f - < deploy/dev/workside.yaml >/dev/null
if [ -n "${GITHUB_TOKEN:-}" ]; then
  $K delete secret aegisscan-scanner-credentials --ignore-not-found >/dev/null
  $K create secret generic aegisscan-scanner-credentials --from-literal=github-token="$GITHUB_TOKEN" >/dev/null
fi

AT=$(curl -fsS -d grant_type=password -d client_id=aegisscan-web -d username=admin -d password=admin123 "$KC/realms/aegisscan/protocol/openid-connect/token" \
  | python -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
H=(-H "Authorization: Bearer $AT" -H 'Content-Type: application/json')
api() { curl -fsS "${H[@]}" "$@"; }

echo "==> Waiting for discovery (expecting $EXPECTED repositories)"
for _ in $(seq 1 60); do
  TOTAL=$(api "$AEGIS/api/v1/repositories?pageSize=1" | python -c 'import sys,json;print(json.load(sys.stdin)["total"])')
  [ "$TOTAL" -ge "$EXPECTED" ] && break; sleep 5
done
echo "    $TOTAL repositories discovered"

echo "==> Linking container images"
link() { api -X PUT "$AEGIS/api/v1/repositories/$1/images" -d "{\"images\":[$2]}" >/dev/null || echo "    (repository $1 not found, skipped)"; }
link adeyosemanputra-pygoat '"python:3.8-slim"'
link owasp-nodegoat '"node:14-alpine"'
link appsecco-dvna '"node:12-alpine"'
link digininja-dvwa '"php:7.4-apache"'
link owasp-railsgoat '"ruby:2.7-alpine"'
link dockersamples-example-voting-app '"redis:5-alpine","postgres:12-alpine"'

echo "==> Queueing scans"
if [ "$ALL" = 1 ]; then
  IDS=$(api "$AEGIS/api/v1/repositories?pageSize=200" | python -c 'import sys,json;print("\n".join(r["id"] for r in json.load(sys.stdin)["items"]))')
else
  IDS=$(printf '%s\n' trufflesecurity-test-keys appsecco-dvna owasp-nodegoat adeyosemanputra-pygoat digininja-dvwa bridgecrewio-terragoat owasp-railsgoat \
    dockersamples-example-voting-app we45-vulnerable-flask-app gothinkster-node-express-realworld-example-app | head -n "$SCANS")
fi
for id in $IDS; do
  for scanner in trufflehog renovate sbom; do
    api -X POST "$AEGIS/api/v1/runs" -d "{\"repositoryId\":\"$id\",\"scanner\":\"$scanner\",\"mode\":\"full\"}" >/dev/null || echo "    (could not queue $scanner for $id)"
  done
done

echo "Done. Open $WEB and sign in as admin / admin123 (operator / operator123, viewer / viewer123 for the other roles)."
echo "Scans run in the background (max 4 at a time): watch the Scans page; findings and SBOMs appear as runs finish."
