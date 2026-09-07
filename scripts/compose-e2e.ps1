$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$webPort = if ($env:AEGIS_WEB_PORT) { $env:AEGIS_WEB_PORT } else { '3000' }
$baseUrl = "http://127.0.0.1:$webPort"
Push-Location $repoRoot
try {
    docker compose up --build --remove-orphans -d

    $deadline = (Get-Date).AddSeconds(90)
    do {
        try {
            $health = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/healthz"
            if ($health.StatusCode -eq 200) { break }
        } catch {
            # Services may still be starting.
        }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if (-not $health -or $health.StatusCode -ne 200) {
        throw 'AegisScan web service did not become healthy within 90 seconds.'
    }

    $ready = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/readyz"
    if ($ready.Content.Trim() -ne 'Healthy') { throw 'AegisScan readiness check failed.' }

    $metrics = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/metrics"
    if ($metrics.Content -notmatch 'aegisscan_up 1') { throw 'AegisScan metrics check failed.' }

    $tokenResponse = Invoke-RestMethod -Uri 'http://127.0.0.1:8081/realms/aegisscan/protocol/openid-connect/token' -Method Post -Body @{
        grant_type = 'password'
        client_id  = 'aegisscan-web'
        username   = 'admin'
        password   = 'admin123'
    }
    $authHeaders = @{ Authorization = "Bearer $($tokenResponse.access_token)" }

    $repositoriesPayload = Invoke-RestMethod -Uri "$baseUrl/api/v1/repositories" -Headers $authHeaders
    $repositories = if ($repositoriesPayload.items) { $repositoriesPayload.items } else { $repositoriesPayload }
    if (@($repositories).Count -lt 3) { throw 'Compose demo repositories were not available.' }

    $k3sReady = docker compose exec -T k3s kubectl get --raw=/readyz
    if ($k3sReady.Trim() -ne 'ok') { throw 'Bundled k3s readiness check failed.' }

    docker compose exec -T k3s kubectl apply -f /opt/aegisscan/generated/repositories_aegisscan_io.yaml -f /opt/aegisscan/generated/repositorysources_aegisscan_io.yaml -f /opt/aegisscan/generated/scanpolicies_aegisscan_io.yaml

    docker compose exec -T postgres pg_isready -U aegisscan -d aegisscan

    Push-Location web
    try {
        $env:COMPOSE_BASE_URL = $baseUrl
        npm run test:e2e:compose
    } finally {
        Pop-Location
    }
} finally {
    Pop-Location
}
