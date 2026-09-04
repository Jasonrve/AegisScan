# Troubleshooting

- Check `/healthz` for process health and `/readyz` for readiness.
- Check `/metrics` for the baseline Prometheus counters.
- A `repository_prepare_failed` execution indicates cache or Git access
  failure; inspect the cache PVC and provider credentials.
- A `scanner_failed` execution is an execution failure, not a security
  finding. Retry after reviewing the sanitized execution log.
- Incremental scans require a successful checkpoint. Request a full scan when
  no checkpoint exists or when the commit range is no longer trustworthy.
