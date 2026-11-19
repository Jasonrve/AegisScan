#!/usr/bin/env bash
# Seeds the compose Gitea (a local GitHub stand-in) so AegisScan can be tested end to end against a real forge:
#   - aegis/secrets-demo : TruffleHog's public fixture secrets (verified AWS key, private key, ...)
#   - aegis/renovate-demo: a Node project with outdated dependencies and a Renovate config
# It also lets scan pods resolve `gitea` (CoreDNS), stores the Gitea token for Renovate PR mode, registers both repositories
# in AegisScan, and maps the internal Gitea URL to the browser-reachable one for UI links.
# Dev only: plain HTTP, throwaway credentials. Requires: docker compose stack up, curl, git, python.
set -euo pipefail
cd "$(dirname "$0")/.."

GITEA_LOCAL=http://127.0.0.1:3300
GITEA_USER=aegis
GITEA_PASS='Aegis-Admin-1'
AEGIS=http://127.0.0.1:8080
KC=http://127.0.0.1:8081
K="docker compose exec -T k3s kubectl"

echo "==> Gitea admin user"
docker compose exec -T --user git gitea gitea admin user create --admin --username "$GITEA_USER" --password "$GITEA_PASS" \
  --email aegis@example.test --must-change-password=false >/dev/null 2>&1 || true

echo "==> Gitea API token"
curl -s -u "$GITEA_USER:$GITEA_PASS" -X DELETE "$GITEA_LOCAL/api/v1/users/$GITEA_USER/tokens/aegisscan" >/dev/null || true
TOKEN=$(curl -fsS -u "$GITEA_USER:$GITEA_PASS" -H 'Content-Type: application/json' -X POST "$GITEA_LOCAL/api/v1/users/$GITEA_USER/tokens" \
  -d '{"name":"aegisscan","scopes":["all"]}' | python -c 'import sys,json;print(json.load(sys.stdin)["sha1"])')

create_repo() {
  curl -s -o /dev/null -H "Authorization: token $TOKEN" -H 'Content-Type: application/json' -X POST "$GITEA_LOCAL/api/v1/user/repos" \
    -d "{\"name\":\"$1\",\"default_branch\":\"main\",\"private\":false,\"auto_init\":false,\"description\":\"$2\"}"
}
push_dir() { # dir repo
  (cd "$1" && git init -q -b main && git add -A && git -c user.name=Aegis -c user.email=aegis@example.test commit -q -m "Initial commit" \
    && git push -q --force "http://$GITEA_USER:$GITEA_PASS@127.0.0.1:3300/$GITEA_USER/$2.git" main)
}

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

echo "==> secrets-demo (TruffleHog fixtures)"
create_repo secrets-demo "Fixture secrets for AegisScan/TruffleHog tests"
git clone -q --depth 1 https://github.com/trufflesecurity/test_keys.git "$WORK/secrets"
rm -rf "$WORK/secrets/.git"
push_dir "$WORK/secrets" secrets-demo

echo "==> renovate-demo (outdated dependencies)"
create_repo renovate-demo "Outdated dependencies for AegisScan/Renovate tests"
mkdir -p "$WORK/renovate"
cat > "$WORK/renovate/package.json" <<'JSON'
{
  "name": "renovate-demo",
  "version": "1.0.0",
  "private": true,
  "dependencies": { "express": "4.17.1", "lodash": "4.17.20", "minimist": "1.2.5" },
  "devDependencies": { "eslint": "8.0.0", "mocha": "9.0.0" }
}
JSON
echo '{ "extends": ["config:recommended"], "prHourlyLimit": 0, "prConcurrentLimit": 0, "dependencyDashboard": true }' > "$WORK/renovate/renovate.json"
echo "# renovate-demo" > "$WORK/renovate/README.md"
push_dir "$WORK/renovate" renovate-demo

echo "==> Cluster DNS + credentials"
GITEA_IP=$(docker inspect aegisscan-gitea-1 --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}')
$K -n kube-system apply -f - >/dev/null <<YAML
apiVersion: v1
kind: ConfigMap
metadata: {name: coredns-custom, namespace: kube-system}
data:
  gitea.server: |
    gitea:53 {
        hosts {
            $GITEA_IP gitea
        }
    }
YAML
$K -n kube-system rollout restart deploy coredns >/dev/null
$K -n kube-system rollout status deploy coredns --timeout=90s >/dev/null
$K delete secret aegisscan-scanner-credentials --ignore-not-found >/dev/null
$K create secret generic aegisscan-scanner-credentials --from-literal=renovate-token-gitea="$TOKEN" >/dev/null

echo "==> Shared git cache PVC"
$K apply -f - < deploy/dev/git-cache.yaml >/dev/null

echo "==> Register in AegisScan"
AT=$(curl -fsS -d grant_type=password -d client_id=aegisscan-web -d username=admin -d password=admin123 "$KC/realms/aegisscan/protocol/openid-connect/token" \
  | python -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
H=(-H "Authorization: Bearer $AT" -H 'Content-Type: application/json')
for r in secrets-demo renovate-demo; do
  curl -fsS "${H[@]}" -X POST "$AEGIS/api/v1/repositories" \
    -d "{\"id\":\"$r\",\"name\":\"$r\",\"provider\":\"gitea\",\"cloneUrl\":\"http://gitea:3000/$GITEA_USER/$r.git\",\"defaultBranch\":\"main\"}" >/dev/null
done
curl -fsS "${H[@]}" -X PUT "$AEGIS/api/v1/settings" -d '{"forgeUrlOverrides":{"http://gitea:3000":"http://localhost:3300"}}' >/dev/null
echo "Done. Gitea UI: http://localhost:3300 ($GITEA_USER / $GITEA_PASS). Repos registered in AegisScan: secrets-demo, renovate-demo."
