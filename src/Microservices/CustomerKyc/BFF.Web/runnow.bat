@echo off
setlocal

echo.
echo ============================================================
echo   Enterprise Web Platform V3 - Customer KYC BFF
echo ============================================================
echo.

cd /d "%~dp0"

echo [1/4] Checking KYC BFF certificate...

if not exist "%~dp0certs\kyc.dev.localhost.pfx" (
    echo.
    echo ERROR: KYC BFF certificate was not found:
    echo   %~dp0certs\kyc.dev.localhost.pfx
    echo.
    pause
    exit /b 1
)

echo       Certificate found.
echo.

echo [2/4] Checking Node.js CA bundle...

if not exist "%~dp0certs\extra-ca-bundle.pem" (
    echo.
    echo ERROR: Node.js CA bundle was not found:
    echo   %~dp0certs\extra-ca-bundle.pem
    echo.
    pause
    exit /b 1
)

echo       CA bundle found.
echo.

echo [3/4] Configuring KYC BFF, HTTPS, and Node TLS trust...

REM ============================================================
REM Customer KYC BFF - Local Development Configuration
REM ============================================================

set "KYC_BFF_PORT=33800"

set "KYC_IDP_AUTHORITY=https://idp.dev.localhost:46392"
set "KYC_BFF_CLIENT_ID=CustomerKYC.Microservice.BFF.ClientID"
set "KYC_BFF_CLIENT_SECRET=3ac91ab3-7ba0-4727-b4f7-36120bec10c5"

set "KYC_BFF_CALLBACK_URL=https://kyc.dev.localhost:33800/api/auth/callback"
set "KYC_BFF_POST_LOGOUT_REDIRECT_URI=https://kyc.dev.localhost:33800/signout-callback-oidc"

set "KYC_API_BASE_URL=https://kyc-api.dev.localhost:46305"

set "KYC_BFF_SESSION_SECRET=8091a396-fdf8-4a2c-8535-18d8b3483fba"

REM Sessions in PostgreSQL (EwpBffStateDb, schema kyc_bff), encrypted at rest with this key
REM (32 random bytes, base64). Development values only: elsewhere from a secret store.
set "KYC_BFF_SESSION_KEY=GTnnrDXPQ66oL8MYk1htsvQqduJcKc1/QGGxXs2LdnA="
set "KYC_BFF_DATABASE_URL=postgres://ewp_kyc_bff:ewp-kyc-bff-dev@localhost:5432/EwpBffStateDb"

REM ============================================================
REM Documents Management (called for the officer by token exchange; no secret of its own)
REM ============================================================

set "KYC_DOCUMENTS_MANAGEMENT_API_BASE_URL=https://documents-management-api.dev.localhost:49486"

REM ============================================================
REM KYC BFF HTTPS / Node TLS
REM ============================================================

set "KYC_BFF_TLS_PFX_PATH=%~dp0certs\kyc.dev.localhost.pfx"
set "KYC_BFF_TLS_PFX_PASSWORD=kyc-mfe-dev-password"
set "NODE_EXTRA_CA_CERTS=%~dp0certs\extra-ca-bundle.pem"

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

endlocal