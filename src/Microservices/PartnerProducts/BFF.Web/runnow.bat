@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Partner Products web (Next.js SPA + light BFF)
echo ============================================================
echo.

cd /d "%~dp0"

REM The ASP.NET Core development certificate (it covers *.dev.localhost), exported ONCE to your
REM user profile - the same files the Partner Products Journey API uses (see its README).
set "CERT_DIR=%USERPROFILE%\.aspnet\https"

echo [1/3] Checking the development certificate...
if not exist "%CERT_DIR%\ewp-v3-dev.pfx" goto :nocert
if not exist "%CERT_DIR%\ewp-v3-dev.pem" goto :nocert
echo       Found in %CERT_DIR%.
echo.

echo [2/3] Configuring (DEVELOPMENT values only)...

set "PARTNER_PRODUCTS_WEB_PORT=46384"
set "PARTNER_PRODUCTS_WEB_PUBLIC_ORIGIN=https://ewp-partner.dev.localhost:46384"
set "PARTNER_PRODUCTS_WEB_SHELL_ORIGIN=https://shell.dev.localhost:46367"
set "PARTNER_PRODUCTS_WEB_IDP_AUTHORITY=https://idp.dev.localhost:46392"
set "PARTNER_PRODUCTS_WEB_CLIENT_ID=PartnerProducts.Microservice.Web.ClientID"
set "PARTNER_PRODUCTS_WEB_CLIENT_SECRET=b98a234e-733a-4b09-a379-932af220853e"
REM Encrypts the tokens in the session table (32 random bytes, base64). Changing it signs everybody out.
set "PARTNER_PRODUCTS_WEB_SESSION_KEY=zaaQFPq0pUTLD4PDmZaL4oQL5nBMNJRvnSTSUGw+osE="
set "PARTNER_PRODUCTS_WEB_DATABASE_URL=postgres://ewp_partner_products_web:ewp-partner-products-web-dev@localhost:5432/EwpBffStateDb"
set "PARTNER_PRODUCTS_JOURNEY_API_BASE_URL=https://partner-products-journey.dev.localhost:46385"

set "PARTNER_PRODUCTS_WEB_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "PARTNER_PRODUCTS_WEB_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the Journey API.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       %PARTNER_PRODUCTS_WEB_PUBLIC_ORIGIN%  (IDP %PARTNER_PRODUCTS_WEB_IDP_AUTHORITY%)
echo       Journey API %PARTNER_PRODUCTS_JOURNEY_API_BASE_URL%
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
REM "runnow.bat dev": Next.js development mode (no build, source maps, a saved change reloads).
REM Run it in VS Code's JavaScript Debug Terminal and breakpoints in server actions, route
REM handlers and lib/server are hit; client code is debugged in the browser's DevTools.
set "PARTNER_PRODUCTS_WEB_DEV=true"
echo       DEVELOPMENT mode. Press Ctrl+C to stop.
echo.
node server.mjs
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
echo Partner Products web has stopped.
endlocal