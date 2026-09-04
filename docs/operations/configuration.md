# Configuration

Chart defaults are documented in `deploy/helm/aegisscan/values.yaml`.
Runtime settings are validated by the API and include concurrency, default
scan mode, retention, and cache limits. Use Kubernetes Secret references for
Git provider, database, scanner, and OIDC credentials.

Full scans require explicit operator/admin intent and should be scheduled with
the retention and concurrency limits appropriate for the repository set.

For production OIDC, set `AEGIS_OIDC_ISSUER`, `AEGIS_OIDC_AUDIENCE`,
`AEGIS_OIDC_ROLE_CLAIM`, and `AEGIS_OIDC_JWKS_URL` together. The API then
requires bearer authentication and validates issuer, audience, signature, and
mapped role claims. `AEGIS_ALLOW_ROLE_HEADER=true` is a local-development-only
compatibility switch and should not be enabled in production.
`AEGIS_ALLOW_DEMO_DATA=true` is limited to the Compose/demo profile and seeds
three non-sensitive sample repositories for smoke testing.
