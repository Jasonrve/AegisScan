# SBOMs and container images

The built-in **SBOM (Trivy)** scanner (`sbom`) produces a [CycloneDX](https://cyclonedx.org) SBOM for the repository checkout and one for every container image linked to the repository, and turns the vulnerabilities in them into normal findings (category `vulnerabilities`, scanner `sbom`, triage/SLA/auto-resolve like any other). Run it from the repository view (**Scan SBOMs**), the Scans page, or add `sbom` to a ScanPolicy's `scanners`.

The repository view shows an **Artifacts & SBOMs** panel: one row per artifact (repository files, each image) with package count, vulnerability counts, where the SBOM came from, a drill-down of packages grouped by lock file (or OS) with their vulnerabilities, and a download of the stored CycloneDX JSON.

## Which images are linked
- **Manual:** type them in the panel (one per line, at most 25) or `PUT /api/v1/repositories/{id}/images {"images":[...]}` (operator).
- **Detected:** every scan looks in the checkout for Dockerfile `FROM` lines (build-stage aliases, `scratch` and `${ARG}` references are skipped) and `image:` values in `*.yaml`/`*.yml` (compose, Kubernetes manifests; Helm templates with `{{ }}` are skipped). A Dockerfile gives the *base* image, not the image the repository ships: add the images you publish by hand. There is no per-image exclusion yet; an undetectable or unwanted image shows as a failed row and never blocks the others.

## SBOMs in the registry (ECR, GHCR, ...)
For each image Trivy first looks for an SBOM published next to it in the registry (`--sbom-sources oci`, the OCI referrers API) and uses it instead of pulling and unpacking the image; the panel shows **from registry** or **generated**. Notes:
- Trivy re-emits the SBOM in its own CycloneDX shape and re-matches vulnerabilities against its current database; the stored document is not byte-identical to the one in the registry and carries no signature.
- If no SBOM is found Trivy falls back to analysing the image layers (needs pull access and more memory/time).
- Whether your ECR SBOMs are discoverable this way depends on how they are attached (OCI referrers: cosign/oras/buildx attestations; Inspector exports are not). Check with `trivy image --sbom-sources oci <image>` and look for `Found SBOM`.

## Registry credentials
Add optional keys to the existing Secret `aegisscan-scanner-credentials`; they are passed to the `sbom` scanner only: `aws-access-key-id`, `aws-secret-access-key`, `aws-session-token`, `aws-region` (ECR), or `registry-username` / `registry-password` (other registries). Scan Jobs do not mount a service account token, so IRSA/workload identity is not available to them.

## Operating notes
- The Job downloads Trivy's vulnerability database on every run (egress to `mirror.gcr.io`/`ghcr.io` on 443) and pulls images from their registries (443). Jobs have a 1 GiB memory limit and a 30 minute deadline: large images or many images may need both raised.
- The script prints each SBOM gzip+base64 encoded (container logs rotate; a large raw document can lose its beginning). A scan log over 20 MB fails the run.
- If some artifacts fail, the others are stored and the run succeeds, but nothing is auto-resolved that run (an unscanned image is unknown, not clean); the failed row keeps its previous SBOM and shows the error.
- Findings for an image are keyed by the reference you configured, not its digest, so a rebuilt image keeps its triage history.
- Not included yet: SBOM history, SPDX, a cross-repository "where is package X used" search, license policy.
