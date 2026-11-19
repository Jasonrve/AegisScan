# Scanner plugins

Scanners are containers. AegisScan runs each one as a Kubernetes Job with the repository already checked out from the [shared Git cache](/architecture/git-cache), then interprets what the container prints to stdout.

- To add a scanner (no code for SARIF or Aegis JSON output), see [Adding a scanner container](/guide/adding-a-scanner).
- Output contract and ready-made definitions: [Custom scanners](/operations/custom-scanners).
- Built-in scanners (TruffleHog, Renovate, SBOM) and Job security: [Scanner catalog](/operations/scanners).

A scanner with a bespoke output format needs a parser in `src/AegisScan.Operator/Scanning/` (see `TruffleHogResultParser.cs`). Parsers must never persist raw secret material; they record a stable fingerprint plus safe path, detector and verification metadata only.
