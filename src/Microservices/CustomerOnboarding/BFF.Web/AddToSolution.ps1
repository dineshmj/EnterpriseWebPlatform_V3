$ErrorActionPreference = 'Stop'

$solution = Join-Path $PSScriptRoot '..\..\..\..\EnterpriseWebPlatform.BSS.sln'
$project = Join-Path $PSScriptRoot 'EnterpriseWebPlatform.BSS.Microservices.CustomerOnboarding.Bff.Web.csproj'

dotnet sln $solution add $project
Write-Host "Customer Onboarding BFF project added to the solution." -ForegroundColor Green
