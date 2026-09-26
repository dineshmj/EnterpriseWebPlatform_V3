# Documents Management Microservice API

Initial V3 implementation of the Documents Management bounded context.

## Purpose

The service owns generic document metadata and the storage boundary. It deliberately does not know about Customer Onboarding, KYC, AML, risk, or other business contexts.

## Database

Database: `EwpDocumentsManagementDb`

SQL: `DocumentMgmtDB/EwpDocumentsManagementDb.sql`

## API

- `POST /v1/documents` - multipart upload
- `GET /v1/documents` - list metadata
- `GET /v1/documents/{documentId}` - get metadata
- `GET /v1/documents/{documentId}/content` - retrieve content
- `DELETE /v1/documents/{documentId}` - delete document and storage object

## Local storage

The initial storage provider is `LocalFileSystemDocumentStorage` and stores content beneath the configured `DocumentStorage:RootPath`.

The storage abstraction is deliberately replaceable by an S3/Documentum-style implementation later.

## Security

The API validates JWT bearer tokens issued by the V3 IDP and requires the `documents-management-api` API resource scope. Endpoint policies are currently separated into read/write policy names while using the same API resource permission, matching the current V3 client/resource convention. Granular `documents-management.read` / `documents-management.write` enforcement can be introduced when the client registrations begin requesting those scopes.

## Important consistency rule

Document BLOBs are never placed in Kafka events. Other bounded contexts should persist only document IDs/references and retrieve content through this service when required.

## Solution integration

Add `API/EnterpriseWebPlatform.BSS.Microservices.DocumentsManagement.Api.csproj` to the V3 solution under the Documents Management microservice area.

The project expects the Landscape project reference at the same relative location used by the V3 `src/Microservices/*/API` projects.
