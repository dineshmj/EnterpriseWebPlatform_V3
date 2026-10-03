# Documents Management — Bounded Context Requirements

**Bounded context:** Documents Management (DM)  
**Subdomain type:** Generic / supporting  
**Status:** Present (API only)

Platform-wide rules are not repeated here. See [doc/](../../../../doc/).

---

## 1. Purpose and Boundary

Documents Management stores documents and their metadata **on behalf of other bounded contexts**. It knows nothing about customers, KYC or any other business meaning. To DM, a document is a file with a type, a business reference, a hash and a lifecycle.

| Owns | Does not own |
|---|---|
| Document metadata (file name, content type, size, SHA hash, version) | The business meaning of `business_reference` or `document_type` |
| Opaque storage references and the storage abstraction | Customer, KYC or onboarding data |
| Document lifecycle, validation, scanning and retention (target) | Decisions about whether a document is *acceptable evidence* (that is KYC's decision) |

**DM has no MFE of its own (deliberate).** It is used only through other contexts' BFFs. (The unused IDP client that once existed for a DM MFE has been removed.)

### Deployable components

| Component | Location | Technology |
|---|---|---|
| DM API | `API` | ASP.NET Core 10, EF Core, PostgreSQL |
| Storage | `API/DocumentStorage` (local file system, `LocalFileSystemDocumentStorage`) | Replaceable behind `IDocumentStorage` (object storage or a DMS in a cloud deployment) |
| Database | `EwpDocumentsManagementDb` (`API/DocumentMgmtDB/EwpDocumentsManagementDb.sql`) | PostgreSQL |

---

## 2. Consumers of This Context

DM is called only by services, never directly by browsers:

| Caller | Identity | Scope | Purpose |
|---|---|---|---|
| Customer Onboarding BFF | M2M `CustomerOnboarding.BFF.To.DocumentsManagement.M2M.ClientID` | `documents-management.write` | Upload onboarding evidence; remove documents of a failed submission |
| Customer KYC BFF | M2M `Kyc.BFF.To.DocumentsManagement.M2M.ClientID` | `documents-management.read` | Read evidence for KYC review |

No human persona works in DM directly. Human authorization for document access is decided by the calling context, and in the target also re-checked by DM using delegated user context (§5).

---

## 3. Domain Model

**`Document`** (aggregate root)

- `Id` (GUID), `FileName`, `ContentType`, `Size`, `ContentHash`, `StorageReference`, optional `DocumentType`, optional `BusinessReference`, `ResourceBranch` (branch scope of the uploading actor), `CreatedAt`, `UpdatedAt`, `Version`.
- Invariants: non-empty ID, file name, content type, content hash and storage reference; size ≥ 0; a resource branch is required for every new document.

Target lifecycle:

```text
UPLOADED ──scan/validate──► AVAILABLE ──► INVALIDATED (business compensation; retained, not deleted)
         └──suspicious────► QUARANTINED
AVAILABLE / INVALIDATED ──retention period ends──► DISPOSED
```

| Term | Meaning |
|---|---|
| Document Type | A free-form classification supplied by the caller, e.g. `KYCProof`, `TaxProof` |
| Business Reference | An opaque string the caller uses to find its documents again, e.g. a customer number. DM never interprets it. |
| Resource Branch | The organizational scope of a document: the branch of the actor who uploaded it. Only actors of the same branch may access it. |
| Storage Reference | DM's internal pointer to the content; never exposed as a path |

---

## 4. Business Rules

1. Document content is **never** placed in Kafka events or in another context's database. Other contexts store document IDs or references only.
2. Content is addressed only through DM. Storage references are protected against path traversal.
3. **Content validation.** Present: an allow-list (PDF, PNG, JPEG) verified against the file signature (magic bytes); a declared type that contradicts the content is rejected; a size limit (25 MB); file-name normalisation. Target: malware scanning before a document becomes AVAILABLE.
4. **Safe delivery.** Present: content is served with `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff` and the type DM verified, never the caller's claim.
5. **Retention over deletion.** A business rejection marks documents INVALIDATED and retains them according to policy. Hard deletion is reserved for cleanup of uploads that never became part of a submitted application, and for disposal after retention.
6. **Read auditing (target).** Every content read is audited with the calling service and, in the target, the human on whose behalf it was read.

---

## 5. Authorization Requirements

| Operation | Current | Target |
|---|---|---|
| List | `documents-management.read` + always confined to the actor's branch; filtered and paged (max 100) in the database | — |
| Get metadata / content | `documents-management.read` + actor branch = document's resource branch (otherwise 404) | Also: the human is entitled to that business reference |
| Upload | `documents-management.write` + an actor branch is required (it becomes the document's resource branch) | A narrowly scoped upload grant (document type + business reference) |
| Delete | `documents-management.write` + only the Customer Onboarding BFF client + same branch | Restricted to documents not yet part of a submitted application |
| Actor branch | A human token's `branch` claim; for M2M, the `X-Actor-Branch` header, accepted **only** from the pinned CO and KYC BFF clients. Any other caller has no branch and is denied (fail closed). | Delegated user context (token exchange or a signed actor claim) instead of an asserted header. See [Authorization-Model §9.3](../../../../doc/Authorization-Model.md#93-delegated-user-context). |

Upload path: currently the BFF uploads on the user's behalf with an M2M token. The target is a direct, narrowly authorized upload (for example a pre-signed object-storage URL issued by DM), so that large files bypass the BFF and the user's context is preserved.

---

## 6. Implementation Status and Known Gaps

| Item | Status |
|---|---|
| Upload, metadata, content, delete; storage abstraction; path-traversal protection | Present |
| Read and write scopes enforced per operation | Present |
| Branch-scoped object-level authorization (`DocumentResourceAuthorization`) | Present |
| List filtered and paged in the database | Present |
| Delete restricted to the CO BFF client | Present |
| Content-type allow-list, magic-byte check, `nosniff` / attachment delivery | Present |
| Malware scanning | Planned |
| Delegated user context instead of `X-Actor-Branch` | Planned |
| Lifecycle states and retention | Planned |
| Encryption at rest, read audit | Planned |
| Documents without a resource branch (`resource_branch` NULL) | Inaccessible by design (fail closed) |

---

## 7. Acceptance Scenarios

- A token with only `documents-management.read` cannot upload or delete.
- A document uploaded with a declared `application/pdf` type but HTML content is rejected (422).
- An actor from another branch receives 404 for a document, and an empty list.
- An M2M client other than the CO / KYC BFFs gets 403, even with the right scope.
- The KYC BFF cannot delete documents.
- Content is always served as an attachment with `nosniff`.
