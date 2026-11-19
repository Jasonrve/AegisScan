<DocsHero />

<div class="home-intro">
  <h2>One operator. A clear path from repository to finding.</h2>
  <p>AegisScan is a Kubernetes-native security control plane. These docs cover the codebase, the KubeOps operator, local development, deployment, scanner boundaries, and the operational decisions behind the system.</p>
</div>

<div class="home-grid">
  <a href="./guide/getting-started"><strong>Try it in 5 minutes</strong><span>Docker Compose stack or a Helm install, with demo users and data.</span><em>Getting started →</em></a>
  <a href="./guide/adding-a-scanner"><strong>Add your own scanner</strong><span>Any container that prints SARIF or Aegis JSON becomes a scanner, no code.</span><em>Scanner guide →</em></a>
  <a href="./architecture/overview"><strong>Understand the system</strong><span>Follow ownership across the API, operator, cache, scanners, and web surface.</span><em>Architecture →</em></a>
  <a href="./operations/installation"><strong>Install with Helm</strong><span>Apply CRDs, configure identity, and deploy into a namespace with predictable defaults.</span><em>Operations →</em></a>
  <a href="./development/kubernetes-test-plan"><strong>Verify the whole path</strong><span>Use the Compose k3s stack to test reconciliation, health, PostgreSQL, and browser workflows.</span><em>Test plan →</em></a>
</div>

## Documentation map

Use the sidebar to move from the shortest path to deeper operational detail. If you are changing the operator, start with [Developer setup](/development/getting-started), then read the [KubeOps migration](/development/kubeops-migration) and [Kubernetes test plan](/development/kubernetes-test-plan). If you are deploying it, begin with [Installation](/operations/installation) and continue through [Configuration](/operations/configuration), [Identity](/operations/identity), and [Troubleshooting](/operations/troubleshooting).
