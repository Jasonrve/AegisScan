# AegisScan production runbooks

## Execution stuck in `running`

Inspect the execution and its Job. If the Job is gone or the pod restarted,
restart the API so startup reconciliation marks the execution `failed` with
`pod_restarted`. Preserve the execution ID and request ID in the incident.

## Database unreachable

`/readyz` returns 503 while liveness remains healthy. Verify the configured
Secret, DNS, network policy, credentials, and PostgreSQL capacity. The API
fails fast on startup and reruns migrations safely after connectivity returns.

## IdP unreachable

Existing API pods continue serving only according to cached JWKS behavior;
new token verification may fail. Verify issuer/JWKS DNS and TLS, then inspect
authentication-denial audit events after recovery.

## Bad release rollback

Use `helm history` and `helm rollback`. Apply any required CRD changes first,
then confirm `/healthz`, `/readyz`, and a read-only API request before resuming
scan submissions.

## Backup and restore

PostgreSQL is the only stateful component — repositories, executions,
findings, scanners, audit events, and role mappings all live there. Kubernetes
CRDs are desired-state and are cheap to reapply from source control; they
don't need to be part of a backup.

**Backup**: run `pg_dump` against the database on whatever schedule your
retention/compliance needs require (daily is a reasonable default for most
deployments); store the dump somewhere durable and outside the cluster (object
storage, a managed backup service). Compose/local dev doesn't need this — the
`postgres-data` volume is disposable dev state.

**Restore**: provision a fresh PostgreSQL instance (or point at an existing
empty one), `pg_restore` the dump, then point `ConnectionStrings__Postgres`
(or the Helm `postgresql.existingSecret`) at it and roll the operator pods.
`Database.MigrateAsync()` runs on startup and is a no-op if the restored
database is already at the latest migration; if the dump predates a schema
change, the operator applies the missing migrations automatically on boot.

**Retention**: the `retentionDays` setting (`PUT /api/v1/settings`, default 90)
governs a periodic sweep (`SchedulingWorker.EnforceRetentionAsync`, runs on the
same ~30s tick as scheduling) that deletes execution history older than that
window. Findings and repositories are current-state, not history, and are
never deleted by retention — only explicit `DELETE` calls remove them.
