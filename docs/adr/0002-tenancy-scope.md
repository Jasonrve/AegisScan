# ADR 0002: Single-team deployment scope

## Status

Accepted for the current release line.

## Decision

AegisScan remains a single-team deployment with one flat Admin/Operator/Viewer
role matrix. Repository-group and namespace-scoped authorization are explicit
non-goals until a multi-team business requirement is approved.

Teams requiring isolation should deploy separate AegisScan instances and
separate database/cache resources.
