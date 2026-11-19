import { User, UserManager, WebStorageStateStore, type UserProfile } from 'oidc-client-ts'

// Runtime settings (container /config.js) win over build-time VITE_* defaults.
const runtime = (window as unknown as { __AEGIS_CONFIG__?: Record<string, string> }).__AEGIS_CONFIG__ ?? {}
const authority = runtime.oidcAuthority || import.meta.env.VITE_OIDC_AUTHORITY || ''
// Compose's Keycloak pins its issuer to the docker-internal hostname (see docker-compose.yml's
// KC_HOSTNAME comment) so tokens validate against the backend's Authority regardless of which
// host/port issued them. The browser can't reach that hostname, so it talks to Keycloak via
// VITE_OIDC_AUTHORITY but must still accept tokens carrying the internal issuer string.
const issuer = runtime.oidcIssuer || import.meta.env.VITE_OIDC_ISSUER || authority
const clientId = runtime.oidcClientId || import.meta.env.VITE_OIDC_CLIENT_ID || 'aegisscan-web'
const origin = window.location.origin
// sso | password | both (default): which sign-in options the login screen offers.
const mode = runtime.loginMode || import.meta.env.VITE_LOGIN_MODE
export const loginMode: 'sso' | 'password' | 'both' = mode === 'sso' || mode === 'password' ? mode : 'both'

export const userManager = new UserManager({
  authority: issuer,
  client_id: clientId,
  redirect_uri: `${origin}/auth/callback`,
  silent_redirect_uri: `${origin}/auth/silent-renew`,
  post_logout_redirect_uri: origin,
  response_type: 'code',
  scope: 'openid',
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
  metadata: {
    issuer,
    authorization_endpoint: `${authority}/protocol/openid-connect/auth`,
    token_endpoint: `${authority}/protocol/openid-connect/token`,
    end_session_endpoint: `${authority}/protocol/openid-connect/logout`,
    jwks_uri: `${authority}/protocol/openid-connect/certs`,
    userinfo_endpoint: `${authority}/protocol/openid-connect/userinfo`,
  },
})

function decodeJwtPayload(token: string): Record<string, unknown> {
  const base64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')
  const padded = base64.padEnd(base64.length + (4 - (base64.length % 4)) % 4, '=')
  const bytes = Uint8Array.from(atob(padded), c => c.charCodeAt(0))
  return JSON.parse(new TextDecoder().decode(bytes))
}

/**
 * Resource-owner-password-credentials grant (Keycloak's client has directAccessGrantsEnabled for
 * exactly this) — an alternative to the SSO redirect for users who'd rather type credentials
 * directly into AegisScan. Builds a oidc-client-ts `User` from the raw token response and feeds it
 * through the same storage/event path signinRedirectCallback uses, so downstream code (role
 * derivation, silent renew, dataClient's bearer token) can't tell the difference.
 */
export async function loginWithPassword(username: string, password: string): Promise<void> {
  const response = await fetch(`${authority}/protocol/openid-connect/token`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ grant_type: 'password', client_id: clientId, scope: 'openid', username, password }),
  })
  if (!response.ok) throw new Error('Invalid username or password')
  const tokens = await response.json()
  const user = new User({
    id_token: tokens.id_token,
    access_token: tokens.access_token,
    refresh_token: tokens.refresh_token,
    token_type: tokens.token_type,
    scope: tokens.scope,
    profile: decodeJwtPayload(tokens.id_token) as UserProfile,
    expires_at: Math.floor(Date.now() / 1000) + tokens.expires_in,
  })
  await userManager.storeUser(user)
  await userManager.events.load(user)
}

export function groupsFromUser(user: User | null): string[] {
  const claim = (user?.profile as Record<string, unknown> | undefined)?.groups
  if (!claim) return []
  return Array.isArray(claim) ? claim.map(String) : [String(claim)]
}

export function roleFromGroups(groups: string[]): string {
  if (groups.includes('admin')) return 'admin'
  if (groups.includes('operator')) return 'operator'
  return 'viewer'
}
