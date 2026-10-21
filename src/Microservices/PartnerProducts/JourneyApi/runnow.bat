@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Partner Products Journey API (NestJS)
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

set "PARTNER_PRODUCTS_JOURNEY_ENVIRONMENT=Development"
set "PARTNER_PRODUCTS_JOURNEY_PORT=46385"
set "PARTNER_PRODUCTS_JOURNEY_PUBLIC_ORIGIN=https://partner-products-journey.dev.localhost:46385"
set "PARTNER_PRODUCTS_JOURNEY_IDP_AUTHORITY=https://idp.dev.localhost:46392"
set "PARTNER_PRODUCTS_JOURNEY_AUDIENCE=partner-products-journey-api"
REM Must equal ClientIds:PartnerProductsWeb in src\IDP\appsettings.json (where the IDP registers that client).
set "PARTNER_PRODUCTS_JOURNEY_CALLER_CLIENT_ID=PartnerProducts.Microservice.Web.ClientID"
REM This API's own client: token exchange only (the Domain APIs below, always for the staff member).
REM Must equal ClientIds:PartnerProductsJourneyApi in src\IDP\appsettings.json (where the IDP registers that client).
set "PARTNER_PRODUCTS_JOURNEY_CLIENT_ID=PartnerProducts.JourneyApi.ClientID"
set "PARTNER_PRODUCTS_JOURNEY_CLIENT_SECRET=c13649b2-6a6e-41a3-b7f7-6cd20e5e0dec"

REM Development: the API addresses below go through the fault proxy (ps\toxiproxy\toxiproxy.json,
REM ports 47101-47111) so operations can cut them; elsewhere use each API's own address.
set "PARTNER_PRODUCTS_API_BASE_URL=https://partner-products-api.dev.localhost:47109"
set "CUSTOMER_ONBOARDING_API_BASE_URL=https://customer-api.dev.localhost:47101"
set "ACCOUNTS_API_BASE_URL=https://accounts-api.dev.localhost:47105"
set "DOCUMENTS_MANAGEMENT_API_BASE_URL=https://documents-management-api.dev.localhost:47103"
set "PAYMENTS_API_BASE_URL=https://payments-api.dev.localhost:47106"

REM +- ARCH -------------------------------------------------------------------------------
REM | Temporal (development server: ps\temporal\Start-Temporal.ps1; UI http://localhost:8233):
REM | this API only STARTS the purchase workflow; the Partner Products Worker runs it on this
REM | task queue. If Temporal is down, the screen offers "Continue" - nothing is lost.
REM +--------------------------------------------------------------------------------------
set "TEMPORAL_ADDRESS=localhost:7233"
set "TEMPORAL_NAMESPACE=default"
set "PARTNER_PRODUCTS_TEMPORAL_TASK_QUEUE=partner-products"

set "PARTNER_PRODUCTS_JOURNEY_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "PARTNER_PRODUCTS_JOURNEY_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the Domain APIs.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       %PARTNER_PRODUCTS_JOURNEY_PUBLIC_ORIGIN%  (IDP %PARTNER_PRODUCTS_JOURNEY_IDP_AUTHORITY%)
echo       Partner Products API  %PARTNER_PRODUCTS_API_BASE_URL%
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
echo Partner Products Journey API has stopped.
endlocal