# Contributing to AegisScan

Thanks for helping. Small, focused pull requests are easiest to review.

## Setup

Prerequisites: .NET 10 SDK, Node.js 22, a container engine (Docker / Rancher Desktop) and Helm.
See the [getting started guide](https://jasonrve.github.io/AegisScan/guide/getting-started) and
[local development](docs/operations/local-development.md).

## Before you open a PR

```bash
dotnet test AegisScan.sln                      # operator + API
(cd web && npm ci && npm run build && npm test) # console
(cd docs && npm ci && npm run build)            # docs (must build without dead links)
helm lint deploy/helm/aegisscan --set authentication.authority=https://idp.example.com/realms/x
```

- Add or update tests for behaviour changes; update docs for user-visible changes.
- CRD changes must pass `python scripts/check_crd_compat.py` and follow [CRD upgrades](docs/operations/crd-upgrades.md).
- Never commit secrets, kubeconfigs or tokens. Report vulnerabilities privately per [SECURITY.md](SECURITY.md).

## Adding a scanner

Most scanners need no code at all: see [Adding a scanner](docs/guide/adding-a-scanner.md).
Only scanners with a bespoke output format need a parser in `src/AegisScan.Operator/Scanning/`.

## Conduct

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).
