@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Audit Journey API (NestJS)
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

set "AUDIT_JOURNEY_PORT=46379"
set "AUDIT_JOURNEY_PUBLIC_ORIGIN=https://audit-journey.dev.localhost:46379"
set "AUDIT_JOURNEY_IDP_AUTHORITY=https://idp.dev.localhost:46392"
set "AUDIT_JOURNEY_AUDIENCE=audit-journey-api"
set "AUDIT_JOURNEY_CALLER_CLIENT_ID=Audit.Microservice.Web.ClientID"
set "AUDIT_JOURNEY_CLIENT_ID=Audit.JourneyApi.ClientID"
set "AUDIT_JOURNEY_CLIENT_SECRET=b749efd3-1000-477e-93df-0fa1fd8eb189"

set "AUDIT_API_BASE_URL=https://audit-api.dev.localhost:46378"
set "PAYMENTS_API_BASE_URL=https://payments-api.dev.localhost:44488"

set "AUDIT_JOURNEY_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "AUDIT_JOURNEY_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the Domain APIs.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       %AUDIT_JOURNEY_PUBLIC_ORIGIN%  (IDP %AUDIT_JOURNEY_IDP_AUTHORITY%)
echo       Audit API    %AUDIT_API_BASE_URL%
echo       Payments API %PAYMENTS_API_BASE_URL%
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
echo Audit Journey API has stopped.
endlocal