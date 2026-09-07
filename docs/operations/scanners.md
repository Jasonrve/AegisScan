# Scanner catalog and shared workspaces

AegisScan ships with two enabled scanner definitions: `trufflehog` and
`renovate`. Administrators can change their container image and startup
parameters from Settings or through the scanner catalog API:

- `GET /api/v1/scanners` — view enabled definitions
- `POST /api/v1/scanners` — add a custom definition
- `PUT /api/v1/scanners/{id}` — update a definition
- `DELETE /api/v1/scanners/{id}` — remove a custom definition

Scanner arguments support these placeholders:

- `{{workspace}}` — the prepared repository path inside the scanner container
- `{{repository}}` — the managed Aegis repository ID
- `{{branch}}` — the prepared repository branch
- `{{mode}}` — `incremental` or `full`

Kubernetes jobs use an init container to prepare `/workspace/repo`. When
`AEGIS_GIT_CACHE_PVC` is configured, the init container keeps one bare mirror
per repository on the shared Git-cache PVC, refreshes it once per preparation,
and materializes isolated scan workspaces with `git clone --shared`. Scanner
containers never clone repositories themselves.

The Helm chart sets `AEGIS_GIT_CACHE_PVC` automatically when `gitCache.enabled`
is true. The cache PVC must be available in the same namespace as scanner Jobs.
