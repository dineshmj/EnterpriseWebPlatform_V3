@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Customer KYC BFF
echo ============================================================
echo.

cd /d "%~dp0"

REM +- SEC ------------------------------------------------------------------------------------------
REM | The ASP.NET Core development certificate (it covers *.dev.localhost), exported ONCE to your
REM | user profile - the same files every Node service uses. Never a copy in the repository: a
REM | private key there is public (SonarQube Cloud found one here, 28-OCT-2026).
REM +-------------------------------------------------------------------------------------------------
set "CERT_DIR=%USERPROFILE%\.aspnet\https"

echo [1/4] Checking the development certificate...
if not exist "%CERT_DIR%\ewp-v3-dev.pfx" goto :nocert
echo       Found in %CERT_DIR%.
echo.

echo [2/4] Checking the Node.js CA file...
if not exist "%CERT_DIR%\ewp-v3-dev.pem" goto :nocert
echo       Found in %CERT_DIR%.
echo.

echo [3/4] Configuring KYC BFF, HTTPS, and Node TLS trust...

REM ============================================================
REM Customer KYC BFF - Local Development Configuration
REM ============================================================

set "KYC_BFF_PORT=33800"

set "KYC_IDP_AUTHORITY=https://idp.dev.localhost:46392"
REM Must equal ClientIds:CustomerKycWeb in src\IDP\appsettings.json (where the IDP registers that client).
set "KYC_BFF_CLIENT_ID=CustomerKYC.Microservice.BFF.ClientID"
set "KYC_BFF_CLIENT_SECRET=3ac91ab3-7ba0-4727-b4f7-36120bec10c5"

set "KYC_BFF_CALLBACK_URL=https://kyc.dev.localhost:33800/api/auth/callback"
set "KYC_BFF_POST_LOGOUT_REDIRECT_URI=https://kyc.dev.localhost:33800/signout-callback-oidc"
REM Must equal ClientBaseUrls:Shell in src\IDP\appsettings.json and ShellOrigin in the Shell.
set "KYC_BFF_SHELL_ORIGIN=https://shell.dev.localhost:46367"

REM Development: the API addresses below go through the fault proxy (ps\toxiproxy\toxiproxy.json,
REM ports 47101-47111) so operations can cut them; elsewhere use each API's own address.
set "KYC_API_BASE_URL=https://kyc-api.dev.localhost:47102"

set "KYC_BFF_SESSION_SECRET=8091a396-fdf8-4a2c-8535-18d8b3483fba"

REM Sessions in PostgreSQL (EwpBffStateDb, schema kyc_bff), encrypted at rest with this key
REM (32 random bytes, base64). Development values only: elsewhere from a secret store.
set "KYC_BFF_SESSION_KEY=GTnnrDXPQ66oL8MYk1htsvQqduJcKc1/QGGxXs2LdnA="
set "KYC_BFF_DATABASE_URL=postgres://ewp_kyc_bff:ewp-kyc-bff-dev@localhost:5432/EwpBffStateDb"

REM ============================================================
REM Documents Management (called for the officer by token exchange; no secret of its own)
REM ============================================================

set "KYC_DOCUMENTS_MANAGEMENT_API_BASE_URL=https://documents-management-api.dev.localhost:47103"

REM ============================================================
REM KYC BFF HTTPS / Node TLS
REM ============================================================

set "KYC_BFF_TLS_PFX_PATH=%CERT_DIR%\ewp-v3-dev.pfx"
set "KYC_BFF_TLS_PFX_PASSWORD=dev-password"
REM Node does not use the Windows certificate store: trust the development certificate
REM explicitly for the calls to the IDP and the APIs.
set "NODE_EXTRA_CA_CERTS=%CERT_DIR%\ewp-v3-dev.pem"

echo       KYC_BFF_PORT=%KYC_BFF_PORT%
echo       KYC_IDP_AUTHORITY=%KYC_IDP_AUTHORITY%
echo       KYC_BFF_CLIENT_ID=%KYC_BFF_CLIENT_ID%
echo       KYC_BFF_CALLBACK_URL=%KYC_BFF_CALLBACK_URL%
echo       KYC_BFF_POST_LOGOUT_REDIRECT_URI=%KYC_BFF_POST_LOGOUT_REDIRECT_URI%
echo       KYC_API_BASE_URL=%KYC_API_BASE_URL%
echo       KYC_BFF_TLS_PFX_PATH=%KYC_BFF_TLS_PFX_PATH%
echo       NODE_EXTRA_CA_CERTS=%NODE_EXTRA_CA_CERTS%
echo       KYC_DOCUMENTS_MANAGEMENT_API_BASE_URL=%KYC_DOCUMENTS_MANAGEMENT_API_BASE_URL%
echo.

echo [4/4] Starting Customer KYC BFF...
echo.
echo       URL: https://kyc.dev.localhost:33800
echo.
echo       Press Ctrl+C to stop the BFF.
echo.

pnpm.cmd run start

echo.
echo Customer KYC BFF has stopped.
echo.
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

:end
endlocal