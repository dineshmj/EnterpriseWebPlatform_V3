cls

$ErrorActionPreference = "Stop"

# In Windows PowerShell / PowerShell ISE, invoking `pnpm` can resolve to pnpm.ps1.
# Use pnpm.cmd explicitly. PowerShell ISE may also surface native stderr as a
# NativeCommandError even when the process is successful, so Invoke-Pnpm temporarily
# uses Continue while invoking PNPM and determines success from $LASTEXITCODE.
$PnpmCommand = "pnpm.cmd"

function Invoke-Pnpm {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "Directory not found: '$Directory'"
    }

    Write-Host "`r`n`t######################################################################################################################################################" -ForegroundColor Cyan
    Write-Host "`t$Description" -ForegroundColor Cyan
    Write-Host "`tDirectory: $Directory" -ForegroundColor Cyan
    Write-Host "`tCommand: pnpm $($Arguments -join ' ')" -ForegroundColor Cyan
    Write-Host "`t######################################################################################################################################################" -ForegroundColor Cyan
    Write-Host "`r`n"

    Push-Location -LiteralPath $Directory
    try {
        # PowerShell ISE can convert native-process stderr into a NativeCommandError
        # even when the native process is not actually failing. Next.js/PNPM can
        # write diagnostic/progress output to stderr, so do not let the global
        # $ErrorActionPreference = "Stop" abort the script on that stream.
        #
        # We determine success/failure exclusively from the native process exit
        # code ($LASTEXITCODE).
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"

        try {
            & $PnpmCommand @Arguments
            $pnpmExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }

        if ($pnpmExitCode -ne 0) {
            throw "pnpm command failed with exit code $pnpmExitCode in '$Directory'."
        }
    }
    finally {
        Pop-Location
    }
}

function Install-Dependencies {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    Invoke-Pnpm `
        -Directory $Directory `
        -Arguments @("install") `
        -Description "Installing PNPM dependencies..."
}

function Build-NextJS-SPA {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    Invoke-Pnpm `
        -Directory $Directory `
        -Arguments @("run", "build") `
        -Description "Building Next.js SPA..."
}

function Export-NextJS-SPA {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    Invoke-Pnpm `
        -Directory $Directory `
        -Arguments @("run", "export") `
        -Description "Exporting Next.js SPA as static files for the BFF..."
}

function Build-NextJS-Client {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    Install-Dependencies -Directory $Directory
    Build-NextJS-SPA -Directory $Directory
    Export-NextJS-SPA -Directory $Directory

    Write-Host "`r`nNext.js client build/export complete: $Directory`r`n" -ForegroundColor Green
}

function Build-NestJS-BFF {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Directory
    )

    Install-Dependencies -Directory $Directory

    Invoke-Pnpm `
        -Directory $Directory `
        -Arguments @("run", "build") `
        -Description "Building NestJS BFF..."

    Write-Host "`r`nNestJS BFF build complete: $Directory`r`n" -ForegroundColor Green
}

### TEMPORARILIY COMMENTED OUT:   # ----------------------------------------------------------------------------------------------------------------------
### TEMPORARILIY COMMENTED OUT:   # Do not run while Visual Studio or VS Code is open.
### TEMPORARILIY COMMENTED OUT:   # This preserves the behaviour of the original V2 script.
### TEMPORARILIY COMMENTED OUT:   # ----------------------------------------------------------------------------------------------------------------------
### TEMPORARILIY COMMENTED OUT:   
### TEMPORARILIY COMMENTED OUT:   $targetProcesses = @("devenv", "code")
### TEMPORARILIY COMMENTED OUT:   $runningTargets = Get-Process -Name $targetProcesses -ErrorAction SilentlyContinue
### TEMPORARILIY COMMENTED OUT:   
### TEMPORARILIY COMMENTED OUT:   if ($runningTargets) {
### TEMPORARILIY COMMENTED OUT:       $detected = ($runningTargets.ProcessName | Select-Object -Unique) -join ", "
### TEMPORARILIY COMMENTED OUT:   
### TEMPORARILIY COMMENTED OUT:       Write-Host "`r`n`r`nDetected active IDE instance(s): [$detected]." -ForegroundColor Yellow
### TEMPORARILIY COMMENTED OUT:       Write-Host "Please close Visual Studio / VS Code and run this script again." -ForegroundColor Yellow
### TEMPORARILIY COMMENTED OUT:       Write-Host "`r`n`r`n"
### TEMPORARILIY COMMENTED OUT:       exit 1
### TEMPORARILIY COMMENTED OUT:   }
### TEMPORARILIY COMMENTED OUT:   
### TEMPORARILIY COMMENTED OUT:   Write-Host "`r`nNo targeted IDEs detected. Proceeding with EWP V3 client/BFF builds..." -ForegroundColor Green

$codeRootFolder = $PSScriptRoot

# ----------------------------------------------------------------------------------------------------------------------
# EWP V3 folder structure
#
# Shell
#   src\Shell\client-app
#
# Customer Onboarding
#   src\Microservices\CustomerOnboarding\BFF.Web
#       client-app
#
# KYC
#   src\Microservices\CustomerKyc\BFF.Web
#       client-app
#       (NestJS BFF itself is the BFF.Web folder)
#
# If your checked-in V3 folder names differ, update only these three variables.
# ----------------------------------------------------------------------------------------------------------------------

$shellSpaAppFolder = Join-Path $codeRootFolder "src\Shell\client-app"

$customerOnboardingBffFolder =
    Join-Path $codeRootFolder "src\Microservices\CustomerOnboarding\BFF.Web"
$customerOnboardingSpaAppFolder =
    Join-Path $customerOnboardingBffFolder "client-app"

$kycBffFolder =
    Join-Path $codeRootFolder "src\Microservices\CustomerKyc\BFF.Web"
$kycSpaAppFolder =
    Join-Path $kycBffFolder "client-app"

# ----------------------------------------------------------------------------------------------------------------------
# Validate all expected folders before changing/building anything.
# ----------------------------------------------------------------------------------------------------------------------

$requiredFolders = @(
    $shellSpaAppFolder,
    $customerOnboardingBffFolder,
    $customerOnboardingSpaAppFolder,
    $kycBffFolder,
    $kycSpaAppFolder
)

foreach ($folder in $requiredFolders) {
    if (-not (Test-Path -LiteralPath $folder -PathType Container)) {
        Write-Host "`r`nRequired EWP V3 folder was not found:" -ForegroundColor Red
        Write-Host "    $folder" -ForegroundColor Red
        Write-Host "`r`nNo build was started. Check the folder structure above and update the path variables if required.`r`n" -ForegroundColor Yellow
        exit 1
    }
}

# ----------------------------------------------------------------------------------------------------------------------
# Step 1 - Shell Next.js SPA
# ----------------------------------------------------------------------------------------------------------------------

Write-Host "`r`n==================================================================================================================" -ForegroundColor Yellow
Write-Host "==  Step #1: Shell Next.js client - PNPM install, build and export                                              ==" -ForegroundColor Yellow
Write-Host "==================================================================================================================" -ForegroundColor Yellow

Build-NextJS-Client -Directory $shellSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 2 - Customer Onboarding Next.js SPA
# ----------------------------------------------------------------------------------------------------------------------

Write-Host "`r`n==================================================================================================================" -ForegroundColor Yellow
Write-Host "==  Step #2: Customer Onboarding Next.js client - PNPM install, build and export                                ==" -ForegroundColor Yellow
Write-Host "==================================================================================================================" -ForegroundColor Yellow

Build-NextJS-Client -Directory $customerOnboardingSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 3 - KYC Next.js SPA
# ----------------------------------------------------------------------------------------------------------------------

Write-Host "`r`n==================================================================================================================" -ForegroundColor Yellow
Write-Host "==  Step #3: KYC Next.js client - PNPM install, build and export                                                ==" -ForegroundColor Yellow
Write-Host "==================================================================================================================" -ForegroundColor Yellow

Build-NextJS-Client -Directory $kycSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 4 - KYC NestJS BFF
# ----------------------------------------------------------------------------------------------------------------------

Write-Host "`r`n==================================================================================================================" -ForegroundColor Yellow
Write-Host "==  Step #4: KYC NestJS BFF - PNPM install and build                                                            ==" -ForegroundColor Yellow
Write-Host "==================================================================================================================" -ForegroundColor Yellow

Build-NestJS-BFF -Directory $kycBffFolder

# ----------------------------------------------------------------------------------------------------------------------
# Done
# ----------------------------------------------------------------------------------------------------------------------

Write-Host "`r`n==================================================================================================================" -ForegroundColor Green
Write-Host "==  EWP V3 client/BFF compilation and export completed successfully.                                            ==" -ForegroundColor Green
Write-Host "==================================================================================================================" -ForegroundColor Green
Write-Host "`r`nBuilt/exported:" -ForegroundColor Green
Write-Host "  1. Shell Next.js client"
Write-Host "  2. Customer Onboarding Next.js client"
Write-Host "  3. KYC Next.js client"
Write-Host "  4. KYC NestJS BFF"
Write-Host "`r`n"
