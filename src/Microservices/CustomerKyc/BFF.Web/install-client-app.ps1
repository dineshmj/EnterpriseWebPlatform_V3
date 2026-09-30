$ErrorActionPreference = "Stop"

$clientApp = Join-Path $PSScriptRoot "client-app"

if (-not (Test-Path (Join-Path $clientApp "package.json"))) {
    throw "Customer KYC client-app package.json was not found."
}

Push-Location $clientApp
try {
    pnpm install --force --config.lockfile=false
    if ($LASTEXITCODE -ne 0) {
        throw "pnpm install failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
