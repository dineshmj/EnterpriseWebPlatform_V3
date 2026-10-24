@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Operations Journey API (NestJS)
echo ============================================================
echo.

cd /d "%~dp0"

REM The ASP.NET Core development certificate (it covers *.dev.localhost), exported ONCE to
REM your user profile - see README.md:
REM   dotnet dev-certs https -ep "%USERPROFILE%\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"
REM   dotnet dev-certs https -ep "%USERPROFILE%\.aspnet\https\ewp-v3-dev.pem" --format Pem --no-password
set "CERT_DIR=%USERPROFILE%\.aspnet\https"

echo [1/3] Checking the development certificate...
if not exist "%CERT_DIR%\ewp-v3-dev.pfx" goto :nocert
if not exist "%CERT_DIR%\ewp-v3-dev.pem" goto :nocert
echo       Found in %CERT_DIR%.
echo.

echo [2/3] Configuring (DEVELOPMENT values only)...

REM Development | Test | UAT | Production. Outside Development the API refuses to start with MFA off,
REM outside Development and Test with the simulators' switch on, and outside Development, Test
REM and UAT with the fault controls on.
set "OPERATIONS_JOURNEY_ENVIRONMENT=Development"
set "OPERATIONS_JOURNEY_PORT=46382"
set "OPERATIONS_JOURNEY_PUBLIC_ORIGIN=https://operations-journey.dev.localhost:46382"
set "OPERATIONS_JOURNEY_IDP_AUTHORITY=https://idp.dev.localhost:46392"
set "OPERATIONS_JOURNEY_AUDIENCE=operations-journey-api"
REM Must equal ClientIds:OperationsWeb in src\IDP\appsettings.json (where the IDP registers that client).
set "OPERATIONS_JOURNEY_CALLER_CLIENT_ID=Operations.Microservice.Web.ClientID"
REM This API's own client: token exchange only (parked Outbox messages of Accounts, Compliance and Partner Products).
REM Must equal ClientIds:OperationsJourneyApi in src\IDP\appsettings.json (where the IDP registers that client).
set "OPERATIONS_JOURNEY_CLIENT_ID=Operations.JourneyApi.ClientID"
set "OPERATIONS_JOURNEY_CLIENT_SECRET=5594deaa-c45c-4299-b628-f877ef42d405"
REM Development: the API addresses below go through the fault proxy (ps\toxiproxy\toxiproxy.json,
REM ports 47101-47111) so operations can cut them; elsewhere use each API's own address.
set "OPERATIONS_JOURNEY_ACCOUNTS_API=https://accounts-api.dev.localhost:47105"
set "OPERATIONS_JOURNEY_COMPLIANCE_API=https://compliance-api.dev.localhost:47104"
set "OPERATIONS_JOURNEY_PARTNER_PRODUCTS_API=https://partner-products-api.dev.localhost:47109"
REM Platform Health's list (every component's readiness probe) and the simulators' addresses:
REM deployment data. Development = the laptop's addresses; a cluster mounts its own file.
set "OPERATIONS_JOURNEY_PLATFORM_FILE=%~dp0config\platform.development.json"

REM DEVELOPMENT ONLY: the simulators' behaviour switch (never set outside Development / test).
set "OPERATIONS_SIMULATOR_CONTROLS_ENABLED=true"
set "OPERATIONS_SIMULATOR_AUTO_REVERT_MINUTES=15"
REM DEVELOPMENT / TEST / UAT ONLY: the fault proxy's routes (cut, slow or hang an API for its
REM server-side callers). The proxy: ps\toxiproxy\Start-Toxiproxy.ps1 (admin API on this machine only).
set "OPERATIONS_FAULT_CONTROLS_ENABLED=true"
set "OPERATIONS_FAULT_PROXY_ADMIN_URL=http://127.0.0.1:8474"
set "OPERATIONS_FAULT_AUTO_REVERT_MINUTES=15"
REM Keep equal to the IDP's Mfa:Enabled: a simulator change then needs a recent MFA sign-in.
set "OPERATIONS_MFA_ENABLED=false"
set "OPERATIONS_MFA_MAX_AGE_MINUTES=15"

set "OPERATIONS_JOURNEY_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "OPERATIONS_JOURNEY_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the components' probes.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       %OPERATIONS_JOURNEY_PUBLIC_ORIGIN%  (IDP %OPERATIONS_JOURNEY_IDP_AUTHORITY%)
echo       Simulator controls: %OPERATIONS_SIMULATOR_CONTROLS_ENABLED%   Fault controls: %OPERATIONS_FAULT_CONTROLS_ENABLED%
echo.

echo [3/3] Building and starting...
if not exist "node_modules" call pnpm.cmd install || goto :failed
if /i "%~1"=="dev" goto :dev
call pnpm.cmd run build || goto :failed
echo.
echo       Press Ctrl+C to stop.
echo.
pnpm.cmd run start
goto :end

:dev
REM "runnow.bat dev": watch mode with source maps. Run it in VS Code's JavaScript Debug Terminal
REM and breakpoints in the TypeScript sources are hit; a saved change restarts the API.
echo       DEVELOPMENT (watch) mode. Press Ctrl+C to stop.
echo.
pnpm.cmd run start:dev
goto :end

:nocert
echo.
echo ERROR: the development certificate was not found in %CERT_DIR%.
echo Export it once (PowerShell), then run this again:
echo   dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"
echo   dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pem" --format Pem --no-password
echo.
pause
exit /b 1

:failed
echo.
echo ERROR: install or build failed.
pause
exit /b 1

:end
echo.
echo Operations Journey API has stopped.
endlocal