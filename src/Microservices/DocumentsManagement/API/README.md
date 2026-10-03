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

## Security configuration

- JWT bearer tokens from the V3 IDP, with audience and issuer validated.
- `DocumentRead` requires the scope `documents-management.read`; `DocumentWrite` requires `documents-management.write`.
- Object-level authorization (`Authorization/DocumentResourceAuthorization.cs`): every operation is confined to the actor's branch. M2M callers state it in the `X-Actor-Branch` header, which is accepted only from the CO BFF and KYC BFF clients. Only the CO BFF client may delete.
- Uploads must be PDF, PNG or JPEG; the type is verified from the file signature (`DocumentContentPolicy`). Content is returned as an attachment with `nosniff`.