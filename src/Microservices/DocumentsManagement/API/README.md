# Documents Management API

Technical guide to the Documents Management API. Purpose, boundary, rules and gaps: [DocumentsManagement-Requirements.md](../../../../doc/Requirements/DocumentsManagement-Requirements.md).

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

## BIAN contract (`/v2`)

Next to its own `/v1`, the API offers BIAN's **DocumentDirectory** service domain (semantic APIs
release 14.0.0), read-only, on the same query, policy and object-level check; EWP's own callers
keep `/v1`.

| Endpoint | Policy | Purpose |
|---|---|---|
| `GET /v2/DocumentDirectory/{id}/Retrieve` | as `GET /v1/documents/{id}` (`DocumentRead`, own branch) | The directory entry: document ID, uploaded at (`OpenDate`), status (`AVAILABLE`, `ATTACHED: evidence of …`, `INVALIDATED: …`) and since when. |
| `GET /v2/DocumentDirectory/{id}/DocumentReferenceProperties/primary/Retrieve` | the same | BIAN's reference properties: `VersionNumber` (the document's version) and the document type. |

Not offered: BIAN's `Register` and `Update`. BIAN's directory entry carries no file (no content,
name, type or hash), and EWP stores nothing it has not checked; a document's status changes only
through the saga's internal steps. The content stays on `/v1`. Mapping:
[doc/bian/README.md](../../../../doc/bian/README.md#documents-documentdirectory).

## Security controls

What this project does to stay secure: each control, what would go wrong without it, the threat it stops, and where to find it in the code. The platform-wide picture: [Architectural and security features §2](../../../../doc/Architectural-And-Security-Features-Demoable-EWP-V3.md#2-security-features).

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Strict access-token validation: issuer, audience, lifetime, signature against the IDP's keys, and only `typ: at+jwt` access tokens (RFC 9068); original claim names kept (`MapInboundClaims = false`) | A token issued for another API, an ID token or an expired token would be accepted | Token confusion and replay across APIs (OWASP API2 Broken Authentication) | `AddJwtBearer` in [Program.cs](Program.cs) |
| 2 | Deny by default, the same in every domain API: every controller needs an authenticated caller on top of its own policy, and any other endpoint without a policy needs one too (fallback policy); only health and metrics are explicitly anonymous | A new endpoint added without an attribute would be public | Broken function-level authorization (OWASP API5) | `MapEwpApiControllers`, `DenyAnonymousByDefault` in [Program.cs](Program.cs); [ApiEndpointAuthorization.cs](../../../Common/WebUtilities/Security/ApiEndpointAuthorization.cs) |
| 3 | Internal endpoints accept only the one machine client meant to call them (`client_id` pinned, not just the scope) | Any service holding the scope could post fake workflow steps (e.g. a forged "invalidate documents" message) | Service impersonation and spoofed workflow events | Policy `DocumentInvalidationSubscriberWrite` in [Program.cs](Program.cs); [InternalDocumentInvalidationsController.cs](API/Controllers/InternalDocumentInvalidationsController.cs), [InternalDocumentAttachmentsController.cs](API/Controllers/InternalDocumentAttachmentsController.cs) |
| 4 | Scope per operation (`DocumentRead` / `DocumentWrite`) | A read-only caller could upload or delete | Privilege escalation | [Program.cs](Program.cs) |
| 5 | Branch scope on every operation, from the person's own exchanged token (RFC 8693) with the Customer Onboarding or KYC BFF, or the Partner Products Journey API (scans of a customer's signed Form VII / transfer slip), as the acting client (`act`); only Customer Onboarding and the Partner Products Journey API may delete, and only documents not yet retained | A caller could read or delete another branch's documents | BOLA / IDOR (OWASP API1) | [DocumentResourceAuthorization.cs](Authorization/DocumentResourceAuthorization.cs) |
| 6 | File type decided by the file's signature (magic bytes) against an allow-list (PDF, PNG, JPEG); the verified type is stored and served | An HTML or SVG file renamed `.pdf` would be stored and later served as active content | Malicious upload, stored XSS (CWE-434) | [DocumentContentPolicy.cs](Domain/Policies/DocumentContentPolicy.cs) |
| 7 | Storage paths resolved and checked to stay under the storage root | A crafted name could read or write outside the store | Path traversal (CWE-22) | [LocalFileSystemDocumentStorage.cs](Infrastructure/Storage/LocalFileSystemDocumentStorage.cs) |
| 8 | Problem responses without internals (a `traceId` only; details are logged on the server) | Stack traces, SQL or exception messages would reach the caller | Information disclosure that helps an attacker (CWE-209, OWASP A05) | [ApiExceptionHandler.cs](API/ErrorHandling/ApiExceptionHandler.cs) |
| 9 | Per-caller rate limits (person, machine client, IP), 429 with `Retry-After`; the subscribers' internal endpoints too (machine-client budget); counted per instance (R-14) | One caller, or a stolen token, could flood the API for everybody | Unrestricted resource consumption (OWASP API4) | [RateLimiting.cs](../../../Common/WebUtilities/Security/RateLimiting.cs), `AddEwpRateLimiting` in [Program.cs](Program.cs) |
| 10 | Own least-privilege database user `ewp_documents_api`: its own database only, no DDL | A compromised API or leaked connection string would expose other contexts' data or let the schema be changed | Lateral movement and blast radius of a breach | [EwpServiceDbUsers.sql](../../../../db/EwpServiceDbUsers.sql) |
| 11 | HTTPS only (HSTS outside Development) and host filtering (`AllowedHosts`) | Tokens could travel over plain HTTP; a forged `Host` header could poison generated links | Interception (OWASP A02) and host-header attacks | `UseHsts` in [Program.cs](Program.cs); `AllowedHosts` in [appsettings.Development.json](appsettings.Development.json) |
| 12 | Documents encrypted at rest (envelope encryption): each file sealed with its own random AES-256-GCM key, that key wrapped by a key-encryption key named in the file (rotation without re-encrypting); the document ID is bound in, so a file swapped onto another document, or changed by one byte, does not open; no valid key ring = no start-up | A disk copy, backup or container escape would yield readable driver licences and tax notices; a file could be silently replaced | Exposure of sensitive PII at rest (OWASP A02 Cryptographic Failures); tampering with evidence | [EncryptedDocumentStorage.cs](Infrastructure/Storage/EncryptedDocumentStorage.cs), `DocumentStorage:Encryption` in [Program.cs](Program.cs) |
| 13 | Container image: built only by CI from a commit, chiseled non-root runtime (no shell, files read-only to the process), base images pinned by tag and digest, scanned before it is published (HIGH / CRITICAL with a fix, or any secret, fails); settings and secrets come from the environment; the database connection string is no longer in `appsettings.json` - none of them is in the image. The encrypted documents go to a mounted volume (`DocumentStorage:RootPath`); the local document store never enters the build. | A developer-built or tampered image could run in an environment; a credential in a layer stays in the registry for good; a shell in the image helps an attacker who gets in | Supply-chain tampering (OWASP A08); vulnerable components (A06); hard-coded credentials (CWE-798) | [Dockerfile](Dockerfile), [.dockerignore](../../../../.dockerignore), [container-images.yml](../../../../.github/workflows/container-images.yml), [doc/Containers.md](../../../../doc/Containers.md) |
| 14 | The BIAN contract (`/v2`) is a second door to the SAME rules: the same query, `DocumentRead` policy and branch check as `GET /v1/documents/{id}` (404 for another branch's document, so none can be probed); read-only - BIAN's `Register` and `Update` are not offered, so no file enters without the signature check, hash and encryption, and no caller edits a document's status | A second API generation that drifts from the first - a looser check on the new route, or a way to store or change evidence around `/v1`'s controls | Authorization bypass through an alternative endpoint (OWASP A01, API5); BOLA (API1) | [DocumentDirectoryController.cs](API/Controllers/DocumentDirectoryController.cs), [DocumentDirectoryMapper.cs](API/Bian/DocumentDirectoryMapper.cs), [DocumentDirectory.cs](API/Bian/DocumentDirectory.cs) |
| 15 | The BIAN contract (`/v2`) is behind the release toggle `bian-v2-endpoints` (Common/WebUtilities/FeatureFlags; default on): switched off, every `/v2` request answers 404 with no body before authentication - as if the route did not exist; `/v1` is never gated | A new API surface could only be withdrawn by a redeploy; a withdrawn one answering 401 / 403 would still show it exists | Exposure of an unreleased or withdrawn endpoint (OWASP A05, API9 improper inventory) | [DocumentDirectoryController.cs](API/Controllers/DocumentDirectoryController.cs) (`[FeatureGate]`), [Program.cs](Program.cs) (`UseEwpFeatureGates` before authentication) |
| 16 | Under-posting refused: every number, date, flag and ID a request must carry is `[JsonRequired]`, so a missing one is a 400 - not a silent `0`, `false`, `0001-01-01` or `Guid.Empty` that looks like a real value | A request with a field left out would be processed as if the caller had sent the default (an amount of 0, an empty message ID that breaks idempotency, "not checked") | Tampered or broken requests accepted (OWASP A04 Insecure Design, A08 Data Integrity) | [InternalDocumentAttachmentsController.cs](API/Controllers/InternalDocumentAttachmentsController.cs), [InternalDocumentInvalidationsController.cs](API/Controllers/InternalDocumentInvalidationsController.cs) |

**Not yet:** malware scanning and encryption at rest.