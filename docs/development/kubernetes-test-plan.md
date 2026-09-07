# Kubernetes test plan

## Automated gates

1. Restore/build the operator with warnings as errors.
2. Run the xUnit resource identity and behavior tests.
3. Build the TypeScript UI and run its unit/E2E tests.
4. Build the operator container and scan it with Trivy.
5. Lint Helm and verify generated CRDs/RBAC/deployment are valid YAML.
6. Run the AOT probe; it is informative until KubeOps publishes NativeAOT support.

## k3s integration scenario

The Compose smoke script starts k3s, waits for `/readyz`, applies the generated KubeOps manifests, and the integration workflow creates valid, disabled, and invalid resources. It verifies `Repository=Ready`, `Repository=Disabled`, `RepositorySource=Ready`, and `ScanPolicy=Error`. It then checks the operator health/metrics endpoints, API repository compatibility endpoint, PostgreSQL readiness, and the browser E2E suite.

## Failure and lifecycle cases

- invalid CRD payload rejected by schema validation;
- disabled Repository becomes `Disabled`;
- RepositorySource without provider credentials remains safely `Ready` with an explanatory status message;
- operator restart re-watches existing objects and reconciles them again;
- deletion does not leave owned resources or stuck finalizers;
- API server unavailability causes retries and a non-ready pod;
- least-privilege Role permits CRDs, Jobs, and referenced Secrets only;
- two replicas exercise KubeOps leader election without duplicate side effects.

The first six automated gates and the local Compose smoke scenario are executed by CI. The failure/lifecycle cases remain a release-certification checklist where an external provider or failure injection is required.

## Current implementation evidence

- Passed locally: `dotnet test AegisScan.sln` (6 tests), frontend typecheck/unit/build (9 tests), Helm lint/template, Linux NativeAOT publish probe, Docker image build, k3s CRD installation, live reconciliation, PostgreSQL readiness, operator/API health, and the full Compose Playwright suite (7 tests).
- The local Compose run used `AEGIS_WEB_PORT=3001` because another process owned port 3000. The default remains port 3000, and the configurable port is supported by `docker-compose.yml` and `scripts/compose-e2e.ps1`. The reproducible CI test is implemented in `.github/workflows/kubernetes-integration.yml`.
