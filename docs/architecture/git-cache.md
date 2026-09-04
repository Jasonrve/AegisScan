# Shared Git cache

Aegis owns repository acquisition. `internal/database.GitCache` keeps one
canonical bare mirror per repository under the configured cache root and
serializes fetch/clone work with a per-repository lock. Scanner plugins receive
a disposable workspace and must not clone or mutate the mirror.

The first preparation creates a mirror; later preparations update it with
`git remote update --prune` and use the mirror as a reference for the isolated
workspace. Workspace cleanup removes only the temporary checkout. A mirror
without a valid `HEAD` is treated as incomplete and rebuilt on the next
preparation.

The current lock is process-local. Multi-replica deployments must add a
database/Kubernetes lease around cache preparation before enabling concurrent
operator replicas; the Helm chart currently defaults to one replica.
