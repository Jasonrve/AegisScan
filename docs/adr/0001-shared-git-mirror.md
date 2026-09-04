# ADR 0001: shared bare mirror and disposable workspaces

## Decision

Use one persistent bare Git mirror per repository and derive isolated scanner
workspaces from it. Aegis, not scanner plugins, owns network acquisition.

## Rationale

This prevents duplicate network clones, protects the canonical cache from
scanner mutation, and gives incremental scanners a stable commit history.
The cache adapter serializes repository operations and cleans temporary
workspaces after execution.

## Follow-up

Cluster-wide cache leases are required before scaling the operator/API beyond
the chart's single-replica default.
