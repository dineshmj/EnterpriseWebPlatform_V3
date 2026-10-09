# Documents Management API

Technical guide to the Documents Management API. Purpose, boundary, rules and gaps: [DocumentsManagement-Requirements.md](../doc/DocumentsManagement-Requirements.md).

## Run

ASP.NET Core 10 on Kestrel, launch profile `https`: `https://documents-management-api.dev.localhost:49486`.

## Database

- Database: `EwpDocumentsManagementDb`
- Script: `DocumentMgmtDB/EwpDocumentsManagementDb.sql`. Connect to the database first; the script recreates its table.

## Storage

`LocalFileSystemDocumentStorage` stores content under the configured `DocumentStorage:RootPath`. Storage references are resolved under that root only (path-traversal safe). The implementation sits behind `IDocumentStorage` and can be replaced by object storage.

## API

| Method and route | Policy | Purpose |
|---|---|---|
| `POST /v1/documents` (multipart field `File`; optional headers `X-Document-Type`, `X-Business-Reference`) | `DocumentWrite` | Upload |
| `GET /v1/documents?businessReference=&documentType=` | `DocumentRead` | List metadata, optionally filtered |
| `GET /v1/documents/{id}` | `DocumentRead` | Metadata |
| `GET /v1/documents/{id}/content` | `DocumentRead` | Content |
| `DELETE /v1/documents/{id}` | `DocumentWrite` | Delete the document and its stored content |

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Strict access-token validation: issuer, audience, lifetime, signature against the IDP's keys, and only `typ: at+jwt` access tokens (RFC 9068); original claim names kept (`MapInboundClaims = false`) | A token issued for another API, an ID token or an expired token would be accepted | Token confusion and replay across APIs (OWASP API2 Broken Authentication) | `AddJwtBearer` in [Program.cs](Program.cs) |
| 2 | Deny by default: every endpoint needs an authorization policy | A new endpoint added without an attribute would be public | Broken function-level authorization (OWASP API5) | `RequireAuthorization` in [Program.cs](Program.cs) |
| 3 | Internal endpoints accept only the one machine client meant to call them (`client_id` pinned, not just the scope) | Any service holding the scope could post fake workflow steps (e.g. a forged "invalidate documents" message) | Service impersonation and spoofed workflow events | Policy `DocumentInvalidationSubscriberWrite` in [Program.cs](Program.cs); [InternalDocumentInvalidationsController.cs](API/Controllers/InternalDocumentInvalidationsController.cs), [InternalDocumentAttachmentsController.cs](API/Controllers/InternalDocumentAttachmentsController.cs) |
| 4 | Scope per operation (`DocumentRead` / `DocumentWrite`) | A read-only caller could upload or delete | Privilege escalation | [Program.cs](Program.cs) |
| 5 | Branch scope on every operation; the branch is accepted only from the Customer Onboarding and KYC BFF clients, and only Customer Onboarding may delete | A caller could read or delete another branch's documents | BOLA / IDOR (OWASP API1) | [DocumentResourceAuthorization.cs](Authorization/DocumentResourceAuthorization.cs) |
| 6 | File type decided by the file's signature (magic bytes) against an allow-list (PDF, PNG, JPEG); the verified type is stored and served | An HTML or SVG file renamed `.pdf` would be stored and later served as active content | Malicious upload, stored XSS (CWE-434) | [DocumentContentPolicy.cs](Domain/Policies/DocumentContentPolicy.cs) |
| 7 | Storage paths resolved and checked to stay under the storage root | A crafted name could read or write outside the store | Path traversal (CWE-22) | [LocalFileSystemDocumentStorage.cs](Infrastructure/Storage/LocalFileSystemDocumentStorage.cs) |
| 8 | Problem responses without internals (a `traceId` only; details are logged on the server) | Stack traces, SQL or exception messages would reach the caller | Information disclosure that helps an attacker (CWE-209, OWASP A05) | [ApiExceptionHandler.cs](API/ErrorHandling/ApiExceptionHandler.cs) |
| 9 | Per-caller rate limits (person, machine client, IP), 429 with `Retry-After` | One caller, or a stolen token, could flood the API for everybody | Unrestricted resource consumption (OWASP API4) | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs), `AddEwpRateLimiting` in [Program.cs](Program.cs) |
| 10 | Own least-privilege database user `ewp_documents_api`: its own database only, no DDL | A compromised API or leaked connection string would expose other contexts' data or let the schema be changed | Lateral movement and blast radius of a breach | [EwpServiceDbUsers.sql](../../../../db/EwpServiceDbUsers.sql) |
| 11 | HTTPS only (HSTS outside Development) and host filtering (`AllowedHosts`) | Tokens could travel over plain HTTP; a forged `Host` header could poison generated links | Interception (OWASP A02) and host-header attacks | `UseHsts` in [Program.cs](Program.cs); `AllowedHosts` in [appsettings.Development.json](appsettings.Development.json) |

**Not yet:** the acting user's branch arrives as an asserted header from the BFFs' machine clients, not by token exchange (planned: M5); malware scanning and encryption at rest.