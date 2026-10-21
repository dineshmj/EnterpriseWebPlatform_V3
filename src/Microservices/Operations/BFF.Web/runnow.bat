@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Operations web (Next.js SPA + light BFF)
echo ============================================================
echo.

cd /d "%~dp0"

REM The ASP.NET Core development certificate (it covers *.dev.localhost), exported ONCE to your
REM user profile - the same files the Operations Journey API uses (see its README).
set "CERT_DIR=%USERPROFILE%\.aspnet\https"

echo [1/3] Checking the development certificate...
if not exist "%CERT_DIR%\ewp-v3-dev.pfx" goto :nocert
if not exist "%CERT_DIR%\ewp-v3-dev.pem" goto :nocert
echo       Found in %CERT_DIR%.
echo.

echo [2/3] Configuring (DEVELOPMENT values only)...

set "OPERATIONS_WEB_PORT=46381"
set "OPERATIONS_WEB_PUBLIC_ORIGIN=https://operations.dev.localhost:46381"
REM Must equal ClientBaseUrls:Shell in src\IDP\appsettings.json and ShellOrigin in the Shell.
set "OPERATIONS_WEB_SHELL_ORIGIN=https://shell.dev.localhost:46367"
set "OPERATIONS_WEB_IDP_AUTHORITY=https://idp.dev.localhost:46392"
REM Must equal ClientIds:OperationsWeb in src\IDP\appsettings.json (where the IDP registers that client).
set "OPERATIONS_WEB_CLIENT_ID=Operations.Microservice.Web.ClientID"
set "OPERATIONS_WEB_CLIENT_SECRET=8c743ece-8834-4906-a324-6f9cdc6a8492"
REM Encrypts the tokens in the session table (32 random bytes, base64). Changing it signs everybody out.
set "OPERATIONS_WEB_SESSION_KEY=Dz6g08V7I2ESb3t6UIfJhTfd19OUPwDcNNsTr4Wyitw="
set "OPERATIONS_WEB_DATABASE_URL=postgres://ewp_operations_web:ewp-operations-web-dev@localhost:5432/EwpBffStateDb"
set "OPERATIONS_JOURNEY_API_BASE_URL=https://operations-journey.dev.localhost:46382"

set "OPERATIONS_WEB_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "OPERATIONS_WEB_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the Journey API.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       %OPERATIONS_WEB_PUBLIC_ORIGIN%  (IDP %OPERATIONS_WEB_IDP_AUTHORITY%)
echo       Journey API %OPERATIONS_JOURNEY_API_BASE_URL%
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
set "OPERATIONS_WEB_DEV=true"
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
echo Operations web has stopped.
endlocal