$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'out'
$target = Join-Path $PSScriptRoot '..\wwwroot'

if (-not (Test-Path $source)) {
    throw "Next.js export output '$source' was not found. Run 'pnpm run build' first."
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Get-ChildItem -Path $target -Force | Remove-Item -Recurse -Force
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force

Write-Host "Accounts MFE exported to $target" -ForegroundColor Green