CREATE TABLE IF NOT EXISTS repositories (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    provider TEXT NOT NULL,
    clone_url TEXT NOT NULL,
    default_branch TEXT NOT NULL DEFAULT 'main',
    latest_commit_sha TEXT,
    status TEXT NOT NULL,
    cache_state TEXT,
    last_successful_scan TIMESTAMPTZ,
    last_full_scan TIMESTAMPTZ,
    active_finding_count INTEGER NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS scan_executions (
    id TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL,
    scanner TEXT NOT NULL,
    scan_mode TEXT NOT NULL,
    status TEXT NOT NULL,
    attempt INTEGER NOT NULL DEFAULT 1,
    commit_from TEXT,
    commit_to TEXT,
    started_at TIMESTAMPTZ,
    finished_at TIMESTAMPTZ,
    normalized_error TEXT,
    scanner_summary JSONB NOT NULL DEFAULT '{}'::jsonb,
    sanitized_log TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS findings (
    fingerprint TEXT PRIMARY KEY,
    repository_id TEXT NOT NULL,
    detector TEXT NOT NULL,
    verified BOOLEAN NOT NULL DEFAULT false,
    safe_path TEXT,
    commit_sha TEXT,
    first_seen TIMESTAMPTZ NOT NULL,
    last_seen TIMESTAMPTZ NOT NULL,
    state TEXT NOT NULL DEFAULT 'open'
);

CREATE INDEX IF NOT EXISTS findings_repository_state_idx ON findings (repository_id, state);
CREATE INDEX IF NOT EXISTS scan_executions_repo_scanner_created_idx ON scan_executions (repository_id, scanner, created_at DESC);
CREATE INDEX IF NOT EXISTS scan_executions_status_created_idx ON scan_executions (status, created_at DESC);

CREATE TABLE IF NOT EXISTS scan_checkpoints (
    repository_id TEXT NOT NULL,
    scanner TEXT NOT NULL,
    commit_sha TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (repository_id, scanner)
);

CREATE TABLE IF NOT EXISTS audit_events (
    id BIGSERIAL PRIMARY KEY,
    actor TEXT NOT NULL,
    role TEXT NOT NULL,
    action TEXT NOT NULL,
    target TEXT NOT NULL,
    outcome TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS runtime_settings (
    key TEXT PRIMARY KEY,
    value JSONB NOT NULL,
    source TEXT NOT NULL CHECK (source IN ('default', 'bootstrap', 'runtime')),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS schema_migrations (version BIGINT PRIMARY KEY, applied_at TIMESTAMPTZ NOT NULL DEFAULT now());
