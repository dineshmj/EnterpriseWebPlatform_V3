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

set "OPERATIONS_JOURNEY_PORT=46382"
set "OPERATIONS_JOURNEY_PUBLIC_ORIGIN=https://operations-journey.dev.localhost:46382"
set "OPERATIONS_JOURNEY_IDP_AUTHORITY=https://idp.dev.localhost:46392"
set "OPERATIONS_JOURNEY_AUDIENCE=operations-journey-api"
set "OPERATIONS_JOURNEY_CALLER_CLIENT_ID=Operations.Microservice.Web.ClientID"

REM DEVELOPMENT ONLY: the simulators' behaviour switch (never set outside Development / test).
set "OPERATIONS_SIMULATOR_CONTROLS_ENABLED=true"
set "OPERATIONS_SIMULATOR_AUTO_REVERT_MINUTES=15"
REM Keep equal to the IDP's Mfa:Enabled: a simulator change then needs a recent MFA sign-in.
set "OPERATIONS_MFA_ENABLED=false"
set "OPERATIONS_MFA_MAX_AGE_MINUTES=15"

set "OPERATIONS_JOURNEY_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "OPERATIONS_JOURNEY_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the components' probes.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       %OPERATIONS_JOURNEY_PUBLIC_ORIGIN%  (IDP %OPERATIONS_JOURNEY_IDP_AUTHORITY%)
echo       Simulator controls: %OPERATIONS_SIMULATOR_CONTROLS_ENABLED%
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