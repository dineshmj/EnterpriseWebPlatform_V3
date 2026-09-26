When I asked a few architect experts, they opine that using a Microservice API as a pass-through layer for large file transfers with the help of an M2M token is a known anti-pattern. This approach causes latency and loses the "user's context" completely as M2M token won't have user's details. They also opine that it is OK for a Microservice's MFE to send directly to another Microservice API other than the MFE's own microservice API. So, they suggest this approach:

### Recommended Pattern: Direct Upload with Broker Authorization

**Core idea:** Customer Onboarding (CO) MFE stays the single point of user interaction (no iframe/navigation switch to Documents Management's MFE), but for the document upload step, the CO MFE's BFF calls the Documents Management (DM) Microservice API **directly** — bypassing the CO Microservice API entirely for the byte transfer. CO's API is only involved to broker authorization, not to touch any binary data.

#### Flow

1.  **Onboarding data submission**: CO MFE submits onboarding data to CO API as today. This is saved in CO's own DB. No change here.
2.  **Upload authorization request**: CO MFE (still the same screen, same UX) calls CO API to request permission to upload a document for this `applicationId`.
3.  **Broker step (service-to-service)**: CO API calls DM API using the existing M2M client-credentials setup — but only to request a **scoped upload authorization**, not to relay any file bytes. DM API responds with either:
    -   A pre-signed upload URL (if the underlying storage is S3/Azure Blob/GCS-backed), or
    -   A short-lived, narrowly scoped upload token tied to `applicationId`, document type, and an expiry window.
4.  **Direct upload**: CO MFE's BFF uses this authorization to upload the binary **directly to DM API** (or DM's storage layer) — one network hop, straight from the browser to the owning microservice. CO Microservice API never sees the file bytes, not even as a stream.
5.  **Validation and persistence**: DM API validates and persists the document exactly as it does today. Since this call carries the user's actual identity (propagated alongside or instead of a bare storage token), DM's audit trail correctly reflects the real user, not a generic service account.

#### Why This Is the Right Approach

| Concern | How it's addressed |
| --- | --- |
| **UX ownership** | User never leaves the CO MFE screen; no iframe navigation, no context hand-off between MFEs, no visual disruption to the onboarding wizard flow. |
| **Data ownership** | DM Microservice still exclusively owns document validation and persistence. CO never writes to or reads from DM's data store. |
| **Performance** | Only one network hop for the binary payload (browser → DM), instead of two (browser → CO → DM). CO has zero exposure to upload traffic and needs no capacity planning for large file transfer. |
| **User context / audit** | The upload call carries the actual user's identity, not just a service-level M2M token — so DM's audit logs show who uploaded what, not just which service forwarded it. |
| **Failure isolation** | A slow or failing upload only affects DM's ingest path. CO's request threads/connections are never held hostage by someone else's file transfer. |

#### What This Requires From Documents Management

DM API needs to expose a **browser-facing upload surface** — CORS-enabled, and capable of validating a user-scoped credential (not only accepting internal M2M service calls as it may today). This is the one real engineering lift in this approach, and it should be scoped and estimated with the DM team before committing to a timeline.

#### Fallback Option

If DM cannot expose this user-facing surface within the required timeframe (due to auth model constraints, network zoning restrictions, etc.), the interim fallback is the M2M streaming-proxy pattern — where CO API streams (never buffers to disk or memory in full) the binary through to DM API using the existing service-to-service token. This should be treated as a **temporary measure**, and must include:

-   True stream-through implementation (verified, not assumed, in the chosen framework/library)
-   Explicit propagation of the original user's identity alongside the M2M token, so DM's audit trail isn't attributed to a generic service account
-   Timeout alignment between the CO↔client leg and the CO↔DM leg
-   Idempotency handling on DM's ingest endpoint for retry scenarios

#### Recommendation

Adopt the direct-upload broker pattern as the target architecture. It satisfies both architectural principles raised — **MFE owns UX, Microservice API owns business data** — without the latency, resource cost, or audit-trail gaps introduced by routing file bytes through an intermediate service.