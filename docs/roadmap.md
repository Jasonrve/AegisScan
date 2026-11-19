# Status and roadmap

AegisScan is an **MVP**. It works end to end (repository registration, scheduled and manual scans, findings, SBOMs, RBAC, audit, Helm install), but it is early software. Know the limits before adopting it.

## Works today

- Repository registration, with `Repository`, `RepositorySource` and `ScanPolicy` CRDs reconciled by a KubeOps operator
- Built-in TruffleHog (secrets), Renovate (dependency updates and PRs) and SBOM scanners
- **Bring-your-own scanner** containers via SARIF or Aegis JSON ([guide](/guide/adding-a-scanner))
- Shared Git cache so every tool scans one clone
- Findings triage, alerts, webhooks, audit log, Prometheus metrics
- OIDC authentication with Admin / Operator / Viewer roles
- Hardened pods (non-root, read-only FS, dropped capabilities, NetworkPolicy), signed images, SBOM on release

## Known gaps

| Area | Gap |
|---|---|
| Identity | Web login targets **Keycloak-style** OIDC endpoints; generic OIDC discovery is not implemented. No SAML/SCIM (use an identity gateway). |
| Bundled services | Optional PostgreSQL and Keycloak subcharts exist; the bundled Keycloak is single-replica with an embedded database. |
| Tenancy | Single namespace, single tenant ([ADR 0002](/adr/0002-tenancy-scope)). |
| Scanners | Scanner images are admin-defined; no per-scanner resource limits in the UI and no per-scanner image pull secret. |
| Forges | Pull-request automation and permalinks are tested against GitHub and Gitea only. |
| Testing | Browser e2e and Kubernetes integration run in a Compose/k3s stack, not across multiple Kubernetes versions. |
| Upgrades | Helm does not upgrade CRDs ([CRD upgrades](/operations/crd-upgrades)). No automated database downgrade. |
| Packaging | Charts are published to GHCR (OCI) only; no Helm repository index or ArtifactHub listing yet. |

## Extensibility points

| Extend | How |
|---|---|
| New scanner | Container plus SARIF or Aegis JSON, no code ([guide](/guide/adding-a-scanner)) |
| New repository source | `RepositorySource` CRD |
| Scan scheduling and scope | `ScanPolicy` CRD |
| Notifications | Webhooks |
| Bespoke parser | Add a parser in `Scanning/` and a catalog entry |

Contributions are welcome: see [CONTRIBUTING](https://github.com/Jasonrve/AegisScan/blob/main/CONTRIBUTING.md).
