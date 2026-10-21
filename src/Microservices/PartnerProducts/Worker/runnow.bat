@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Partner Products Worker (Temporal)
echo ============================================================
echo.

cd /d "%~dp0"

REM The ASP.NET Core development certificate (the health probe's https, and trust for the calls
REM to the IDP and the APIs) - exported ONCE, see the Journey API's README.
set "CERT_DIR=%USERPROFILE%\.aspnet\https"

echo [1/3] Checking the development certificate...
if not exist "%CERT_DIR%\ewp-v3-dev.pfx" goto :nocert
if not exist "%CERT_DIR%\ewp-v3-dev.pem" goto :nocert
echo       Found in %CERT_DIR%.
echo.

echo [2/3] Configuring (DEVELOPMENT values only)...

REM Temporal (development server: "temporal server start-dev"; UI http://localhost:8233).
set "TEMPORAL_ADDRESS=localhost:7233"
set "TEMPORAL_NAMESPACE=default"
set "PARTNER_PRODUCTS_TEMPORAL_TASK_QUEUE=partner-products"

REM +- SEC --------------------------------------------------------------------------------
REM | This worker's own MACHINE client (client credentials): only the internal endpoints that
REM | pin its client ID accept it. The secret below is a DEVELOPMENT value; elsewhere it
REM | comes from the environment or a secret store (Threat-Model R-10).
REM +--------------------------------------------------------------------------------------
set "PARTNER_PRODUCTS_WORKER_IDP_AUTHORITY=https://idp.dev.localhost:46392"
REM Must equal ClientIds:PartnerProductsWorker in src\IDP\appsettings.json (where the IDP registers that client).
set "PARTNER_PRODUCTS_WORKER_CLIENT_ID=PartnerProducts.Worker.M2M.ClientID"
set "PARTNER_PRODUCTS_WORKER_CLIENT_SECRET=e6b2c9d4-3f71-4a85-9c0e-81d5a7f3b246"

REM Development: the API addresses below go through the fault proxy (ps\toxiproxy\toxiproxy.json,
REM ports 47101-47111) so operations can cut them; elsewhere use each API's own address.
set "PARTNER_PRODUCTS_API_BASE_URL=https://partner-products-api.dev.localhost:47109"
set "PAYMENTS_API_BASE_URL=https://payments-api.dev.localhost:47106"

REM +- SEC --------------------------------------------------------------------------------
REM | The payment-event listener reads payments.payment.events over TLS (SASL_SSL; the
REM | broker's certificate is checked against the EWP local trust root) as the worker's OWN
REM | Kafka user (read on that topic and group partner-products.worker only). DEVELOPMENT password;
REM | elsewhere from the environment or a secret store. The settlement account must match the
REM | Partner Products API's PartnerBank:Settlement - only its payments are looked up.
REM +--------------------------------------------------------------------------------------
set "KAFKA_BOOTSTRAP_SERVERS=localhost:9092"
set "KAFKA_SSL_CA_LOCATION=%USERPROFILE%\.ewp-pki\ewp-trust-root.pem"
set "PARTNER_PRODUCTS_WORKER_KAFKA_USERNAME=ewp-partner-products-worker"
set "PARTNER_PRODUCTS_WORKER_KAFKA_PASSWORD=ewp-partner-products-worker-kafka-dev"
set "PARTNER_PRODUCTS_SETTLEMENT_BSB=182-512"
set "PARTNER_PRODUCTS_SETTLEMENT_ACCOUNT_NUMBER=961234567"

set "PARTNER_PRODUCTS_WORKER_HEALTH_PORT=46383"
set "PARTNER_PRODUCTS_WORKER_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "PARTNER_PRODUCTS_WORKER_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate explicitly.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       Temporal %TEMPORAL_ADDRESS% (%TEMPORAL_NAMESPACE%), task queue %PARTNER_PRODUCTS_TEMPORAL_TASK_QUEUE%
echo       Health   https://localhost:%PARTNER_PRODUCTS_WORKER_HEALTH_PORT%/health
echo.

echo [3/3] Building and starting...
if not exist "node_modules" call pnpm.cmd install || goto :failed
call pnpm.cmd run build || goto :failed
echo.
echo       Press Ctrl+C to stop.
echo.
pnpm.cmd run start
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
echo Partner Products Worker has stopped.
endlocal