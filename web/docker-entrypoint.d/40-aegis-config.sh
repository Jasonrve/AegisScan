#!/bin/sh
# Renders runtime OIDC settings so one published image works in any deployment.
# Empty values fall back to the build-time VITE_* defaults (used by docker compose).
set -eu
esc() { printf '%s' "$1" | sed 's/[\\"]/\\&/g'; }
cat > /tmp/config.js <<CFG
window.__AEGIS_CONFIG__ = {
  oidcAuthority: "$(esc "${OIDC_AUTHORITY:-}")",
  oidcIssuer: "$(esc "${OIDC_ISSUER:-}")",
  loginMode: "$(esc "${OIDC_LOGIN_MODE:-}")",
  oidcClientId: "$(esc "${OIDC_CLIENT_ID:-}")"
};
CFG
