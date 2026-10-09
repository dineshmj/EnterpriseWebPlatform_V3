# Identity Provider (Duende IdentityServer)

Signs people and services in for the whole platform and issues their tokens. Requirements and the client catalogue: [IDP-Requirements.md](doc/IDP-Requirements.md).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Account lockout (5 failures → 15 minutes) and per-IP sign-in throttling | Passwords could be guessed endlessly | Brute force and credential stuffing (OWASP A07) | [UserRepository.cs](Repositories/UserRepository.cs), `AddRateLimiter` in [Program.cs](Program.cs) |
| 2 | Same time and message for an unknown user as for a wrong password (a dummy hash is verified) | An attacker could learn which usernames exist | Username enumeration | [UserRepository.cs](Repositories/UserRepository.cs), [PasswordManager.cs](Security/PasswordManager.cs) |
| 3 | Two-step sign-in (TOTP, RFC 6238) with encrypted secrets, hashed single-use recovery codes, no code accepted twice; `amr` in the tokens (off by default: `Mfa:Enabled`) | A stolen password alone would open the account | Account takeover | [Totp.cs](Security/Totp.cs), [MfaService.cs](Security/MfaService.cs), [Mfa.cshtml.cs](Pages/Account/Mfa.cshtml.cs), [MfaSetup.cshtml.cs](Pages/Account/MfaSetup.cshtml.cs) |
| 4 | Authorization code + PKCE only; no implicit or password (ROPC) grant; one-time refresh tokens (rotation) | Tokens in URLs; passwords handled by clients; a stolen refresh token usable forever | Token leakage and replay (RFC 9700) | [MfePayments.cs](ConfigRegistration/Clients/MFEs/MfePayments.cs) (and the other clients) |
| 5 | Token exchange (RFC 8693) by allow-list: who may exchange which token for which audience; the person is re-checked at every exchange | Any client could turn any token into a token for any API | Privilege escalation through delegation | [TokenExchangeGrantValidator.cs](Security/TokenExchangeGrantValidator.cs), `act` in [CustomProfileService.cs](Services/CustomProfileService.cs) |
| 6 | Client secrets from configuration, never compiled in; the development signing key only in Development | Secrets in the repository; a development key signing production tokens | Leaked secrets; forged tokens | [ClientSecretStore.cs](Security/ClientSecretStore.cs), [SigningCredentialExtensions.cs](Security/SigningCredentialExtensions.cs) |
| 7 | Operational store in PostgreSQL (refresh tokens hashed, details encrypted); key ring encrypted at rest, fail closed | A restart would sign everybody out; a database reader could use refresh tokens or read keys | Token theft from storage | `AddOperationalStore` in [Program.cs](Program.cs), [PersistentDataProtection.cs](../Common/WebUtilities/Security/PersistentDataProtection.cs) |
| 8 | Security headers on every page: CSP without `'unsafe-inline'`, `frame-ancestors 'none'`, `X-Frame-Options: DENY`, `nosniff` | The sign-in page could be framed or carry injected script | Clickjacking and XSS on the sign-in page | [SecurityHeadersAttribute.cs](SecurityHeadersAttribute.cs) |
| 9 | Logout by POST; front-channel and back-channel logout to every client | A hostile link could sign people out; sessions would survive elsewhere | Logout CSRF; session reuse | [Logout.cshtml.cs](Pages/Account/Logout.cshtml.cs), [LoggedOut.cshtml](Pages/Account/LoggedOut.cshtml) |
| 10 | "Keep me signed in" lasts 8 hours, not 30 days | A sign-in would live for weeks on a shared or lost device | Session hijacking on unattended devices | `RememberLoginLifetime` in [PendingSignIn.cs](Security/PendingSignIn.cs) |
| 11 | Host filtering (`AllowedHosts`) and HSTS | A forged `Host` header could poison links in e-mails or discovery; HTTP downgrade | Host-header attacks; interception | [appsettings.Development.json](appsettings.Development.json), [Program.cs](Program.cs) |

**Not yet:** passkeys / FIDO2 (phishing-resistant sign-in).