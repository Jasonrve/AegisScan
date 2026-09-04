$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    docker compose up --build -d

    $deadline = (Get-Date).AddSeconds(90)
    do {
        try {
            $health = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:3000/healthz'
            if ($health.StatusCode -eq 200) { break }
        } catch {
            # Services may still be starting.
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if (-not $health -or $health.StatusCode -ne 200) {
        throw 'AegisScan web service did not become healthy within 90 seconds.'
    }

    $ready = Invoke-RestMethod -Uri 'http://127.0.0.1:3000/readyz'
    if ($ready.status -ne 'ready') { throw 'AegisScan readiness check failed.' }

    $metrics = Invoke-WebRequest -UseBasicParsing -Uri 'http://127.0.0.1:3000/metrics'
    if ($metrics.Content -notmatch 'aegisscan_up 1') { throw 'AegisScan metrics check failed.' }

    $repositoriesPayload = Invoke-RestMethod -Uri 'http://127.0.0.1:3000/api/v1/repositories'
    $repositories = if ($repositoriesPayload.items) { $repositoriesPayload.items } else { $repositoriesPayload }
    if (@($repositories).Count -lt 3) { throw 'Compose demo repositories were not available.' }

    $k3sReady = docker compose exec -T k3s kubectl get --raw=/readyz
    if ($k3sReady.Trim() -ne 'ok') { throw 'Bundled k3s readiness check failed.' }

    docker compose exec -T postgres pg_isready -U aegisscan -d aegisscan

    Push-Location web
    try {
        $env:COMPOSE_BASE_URL = 'http://127.0.0.1:3000'
        npm run test:e2e:compose
    } finally {
        Pop-Location
    }
} finally {
    Pop-Location
}
