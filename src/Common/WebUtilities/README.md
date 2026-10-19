# Common.WebUtilities

Shared web security building blocks for the BFFs and APIs.

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Content-Security-Policy builder: scripts and styles by SHA-256 hash of the exported inline blocks, computed at start-up; no `'unsafe-inline'` | Each BFF would hand-write its policy, and inline code would need `'unsafe-inline'` | Cross-site scripting and CSS injection (OWASP A03) | [ContentSecurityPolicy.cs](Security/ContentSecurityPolicy.cs) |
| 2 | Data Protection keys in PostgreSQL, encrypted at rest (certificate, or DPAPI in Development); no protection configured = no start | Keys in plain files or memory: readable by anyone with the database, lost on restart | Cookie forgery; sign-out of everyone on restart | [PersistentDataProtection.cs](Security/PersistentDataProtection.cs) |
| 3 | Per-caller rate limiting (person by `sub`, machine by `client_id`, anonymous by IP), 429 with `Retry-After` | One shared bucket, or none: a single caller could exhaust a service | Unrestricted resource consumption (OWASP API4) | [RateLimiting.cs](Security/RateLimiting.cs) |
| 4 | MFA step-up requirement (`amr` must contain `mfa` and `auth_time` must be recent), 403 `mfa_required` with a clear reason (the screens then ask for a fresh code in the Shell's in-page overlay) | Risky decisions would need only a password | Account takeover leading to fraudulent decisions | [MfaStepUp.cs](Security/MfaStepUp.cs) |
| 5 | Deny by default for the domain APIs: `MapEwpApiControllers` (an authenticated caller on every controller, plus the given policies) and `DenyAnonymousByDefault` (fallback policy for any endpoint without its own) | Each API would protect its endpoints its own way; one forgotten line would leave a new controller public | Broken function-level authorization (OWASP API5) | [ApiEndpointAuthorization.cs](Security/ApiEndpointAuthorization.cs) |
| 6 | External-system addresses checked at start-up (absolute, no credentials; outside Development https, `PermittedHosts`, no loopback / private / link-local address) and `WithoutRedirects` for their clients | A wrong or tampered address would receive API keys and customer data | Server-side request forgery by configuration (CWE-918) | [ExternalEndpoint.cs](Security/ExternalEndpoint.cs) |