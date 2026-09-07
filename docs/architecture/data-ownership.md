# Data ownership

Kubernetes resources describe desired repository, source, and policy state.
PostgreSQL owns execution history, findings, checkpoints, audit events, and
runtime settings. Kubernetes Jobs are disposable execution records; deleting a
Job must not delete historical results.

Scanner output is normalized before persistence. Complete discovered secret
values are never stored in findings or sanitized logs.
