cls

$ErrorActionPreference = "Stop"

enum BoxPart {
    BOX_STARTING
    BOX_EMPTY_LINE
    BOX_TEXT
    BOX_ENDING
}

$BoxWidth   = 141
$InnerWidth = $BoxWidth - 2

function Write-Box {
    param(
        [Parameter(Mandatory)]
        [BoxPart]$BoxPart,

        [Parameter(Mandatory = $false)]
        [string]$Text = "",

        [Parameter(Mandatory = $false)]
        [ConsoleColor]$Color = [ConsoleColor]::Cyan,

        # Centre the text in the box (step banners); otherwise it is indented by four spaces.
        [switch]$Center
    )

    switch ($BoxPart) {

        ([BoxPart]::BOX_STARTING) {
            Write-Host ("╔" + ("═" * $InnerWidth) + "╗") -ForegroundColor $Color
        }

        ([BoxPart]::BOX_EMPTY_LINE) {
            Write-Host ("║" + (" " * $InnerWidth) + "║") -ForegroundColor $Color
        }

        ([BoxPart]::BOX_TEXT) {
            if ($Center) {
                $leftPadding = [Math]::Max(0, [Math]::Floor(($InnerWidth - $Text.Length) / 2))
                $Text = (" " * $leftPadding) + $Text
            }
            else {
                $Text = "    $Text"
            }

            if ($Text.Length -gt $InnerWidth) {
                $Text = $Text.Substring(0, $InnerWidth)
            }

            Write-Host ("║" + $Text.PadRight($InnerWidth) + "║") -ForegroundColor $Color
        }

        ([BoxPart]::BOX_ENDING) {
            Write-Host ("╚" + ("═" * $InnerWidth) + "╝") -ForegroundColor $Color
        }
    }
}

# A step or summary banner: a box with one centred title line.
function Write-Banner {
    param(
        [Parameter(Mandatory)]
        [string]$Title,

        [Parameter(Mandatory = $false)]
        [ConsoleColor]$Color = [ConsoleColor]::Yellow
    )

    Write-Host ""
    Write-Box BOX_STARTING -Color $Color
    Write-Box BOX_EMPTY_LINE -Color $Color
    Write-Box BOX_TEXT $Title -Color $Color -Center
    Write-Box BOX_EMPTY_LINE -Color $Color
    Write-Box BOX_ENDING -Color $Color
}

# In Windows PowerShell / PowerShell ISE, invoking `pnpm` can resolve to pnpm.ps1.
# Use pnpm.cmd explicitly. PowerShell ISE surfaces native stderr as NativeCommandError
# even when the process succeeds, so Invoke-Pnpm runs pnpm through cmd.exe with stderr
# merged into stdout, and determines success from $LASTEXITCODE.
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

    Write-Host "`r`n"

    Write-Box BOX_STARTING
    Write-Box BOX_EMPTY_LINE
    Write-Box BOX_TEXT $Description
    Write-Box BOX_TEXT "Directory: $Directory"
    Write-Box BOX_EMPTY_LINE
    Write-Box BOX_TEXT "Command: pnpm $($Arguments -join ' ')"
    Write-Box BOX_EMPTY_LINE
    Write-Box BOX_ENDING

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
            # Run through cmd.exe and merge stderr into stdout THERE ("2>&1" inside the
            # cmd command line). pnpm echoes each script it runs ("$ nest build") to
            # stderr; merged by cmd.exe, PowerShell receives plain output lines and ISE
            # no longer shows them as red NativeCommandError records. cmd.exe returns
            # pnpm's exit code, so $LASTEXITCODE still decides success.
            $quotedArguments = $Arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }
            cmd.exe /d /c "$PnpmCommand $($quotedArguments -join ' ') 2>&1" | Out-Host
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
# Compliance
#   src\Microservices\Compliance\BFF.Web
#       client-app
#
# Accounts
#   src\Microservices\Accounts\BFF.Web
#       client-app
#
# Payments
#   src\Microservices\Payments\BFF.Web
#       client-app
#
# If your checked-in V3 folder names differ, update only these path variables.
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

$complianceBffFolder =
    Join-Path $codeRootFolder "src\Microservices\Compliance\BFF.Web"
$complianceSpaAppFolder =
    Join-Path $complianceBffFolder "client-app"

$accountsBffFolder =
    Join-Path $codeRootFolder "src\Microservices\Accounts\BFF.Web"
$accountsSpaAppFolder =
    Join-Path $accountsBffFolder "client-app"

$paymentsBffFolder =
    Join-Path $codeRootFolder "src\Microservices\Payments\BFF.Web"
$paymentsSpaAppFolder =
    Join-Path $paymentsBffFolder "client-app"

# ----------------------------------------------------------------------------------------------------------------------
# Validate all expected folders before changing/building anything.
# ----------------------------------------------------------------------------------------------------------------------

$requiredFolders = @(
    $shellSpaAppFolder,
    $customerOnboardingBffFolder,
    $customerOnboardingSpaAppFolder,
    $kycBffFolder,
    $kycSpaAppFolder,
    $complianceBffFolder,
    $complianceSpaAppFolder,
    $accountsBffFolder,
    $accountsSpaAppFolder,
    $paymentsBffFolder,
    $paymentsSpaAppFolder
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

Write-Banner "Step #1: Shell Next.js client - PNPM install, build and export"

Build-NextJS-Client -Directory $shellSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 2 - Customer Onboarding Next.js SPA
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "Step #2: Customer Onboarding Next.js client - PNPM install, build and export"

Build-NextJS-Client -Directory $customerOnboardingSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 3 - KYC Next.js SPA
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "Step #3: KYC Next.js client - PNPM install, build and export"

Build-NextJS-Client -Directory $kycSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 4 - KYC NestJS BFF
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "Step #4: KYC NestJS BFF - PNPM install and build"

Build-NestJS-BFF -Directory $kycBffFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 5 - Compliance Next.js SPA (served by the ASP.NET Core Compliance BFF)
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "Step #5: Compliance Next.js client - PNPM install, build and export"

Build-NextJS-Client -Directory $complianceSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 6 - Accounts Next.js SPA (served by the ASP.NET Core Accounts BFF)
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "Step #6: Accounts Next.js client - PNPM install, build and export"

Build-NextJS-Client -Directory $accountsSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Step 7 - Payments Next.js SPA (served by the ASP.NET Core Payments BFF)
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "Step #7: Payments Next.js client - PNPM install, build and export"

Build-NextJS-Client -Directory $paymentsSpaAppFolder

# ----------------------------------------------------------------------------------------------------------------------
# Done
# ----------------------------------------------------------------------------------------------------------------------

Write-Banner "EWP V3 client/BFF compilation and export completed successfully." -Color Green

Write-Host "`r`nBuilt/exported:`r`n" -ForegroundColor Green
Write-Host "  1. Shell Next.js client"
Write-Host "  2. Customer Onboarding Next.js client"
Write-Host "  3. KYC Next.js client"
Write-Host "  4. KYC NestJS BFF"
Write-Host "  5. Compliance Next.js client"
Write-Host "  6. Accounts Next.js client"
Write-Host "  7. Payments Next.js client"
Write-Host "`r`nRestart the Shell, Customer Onboarding, KYC, Compliance, Accounts and Payments BFFs: their Content-Security-Policy hashes are computed at start-up." -ForegroundColor Yellow
Write-Host "`r`n"