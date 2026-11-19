# Releases

Releases are tag-driven. A tag matching `v*` runs the release job in `.github/workflows/ci.yml` after the verification job succeeds.

## What a release publishes

- Multi-architecture operator (`ghcr.io/jasonrve/aegisscan`) and web (`ghcr.io/jasonrve/aegisscan-web`) images for `linux/amd64` and `linux/arm64` to GHCR.
- An SPDX SBOM attached as a workflow artifact.
- Cosign signatures for both images.
- The Helm chart pushed to `oci://ghcr.io/jasonrve/charts/aegisscan`.

The image and chart use the tag version without the leading `v` as their application version. Keep the tag semver-compatible because Helm receives it as `--version`.

## Release checklist

1. Confirm the .NET and KubeOps versions in `global.json` and `src/AegisScan.Operator/AegisScan.Operator.csproj`.
2. Run `dotnet test AegisScan.sln` and the frontend tests locally.
3. Run the Compose k3s test plan, including the browser suite.
4. Create and push a tag such as `v0.2.0`.
5. Verify the GHCR image signature, SBOM artifact, and Helm OCI package.
6. Deploy the chart from the OCI registry and check the operator health/readiness probes.

## Image provenance

The workflow requires `packages: write` and `id-token: write`. Keep those permissions scoped to the release job. Do not place registry credentials in repository files or Helm values.

## Rollback

Roll back by selecting the previous immutable image tag and chart version. Do not reuse a mutable `latest` tag for production deployments. Restore CRDs independently and follow [CRD upgrades](/operations/crd-upgrades) when a release changes the schema.
