When I asked another architect expert, he opines that using a Microservice API as a pass-through layer for large file transfers with the help of an M2M token is a known anti-pattern. This approach causes latency and loses the "user's context" completely as M2M token won't have user's details. They also opine that it is OK for a Microservice's MFE to send directly to another Microservice API other than the MFE's own microservice API. So, they suggest this approach:

### Core Architectural Analysis & Proposed Options

#### Why the Pass-Through M2M Proxy Approach is problematic

1.  **User Context Loss:** An M2M token uses the OAuth 2.0 Client Credentials flow, which identifies the *calling application* (Customer Onboarding API), not the *end user*. The Documents Management (DM) service loses vital audit metadata, user permissions, and user-level authorization context unless complex custom headers or token-exchange mechanisms are introduced.

2.  **Resource Exhaustion on Onboarding API:** Streaming binary payloads (BLOBs) through an API forces the server to allocate memory buffers and hold network threads open twice as long (Client $\\rightarrow$ Customer Onboarding API $\\rightarrow$ Document Management API). Highly concurrent uploads can lead to memory spikes and thread starvation on the Customer Onboarding service, degrading core onboarding business logic.

3.  **Tight Service Coupling & Bandwidth Costs:** The Customer Onboarding service becomes an unnecessary network bottleneck and single point of failure for document processing. Doubling the internal network transfers also increases cloud ingress/egress costs unnecessarily.

### Suggested Alternative Approaches

To preserve seamless UX without sacrificing backend microservice purity or performance, the experts recommend two primary patterns:

#### Option 1: Direct Cross-Domain MFE Call (Recommended for Direct API Uploads)

An MFE running in the browser is not strictly bound to communicate only with its "parent" domain API. The Customer Onboarding (CO) MFE can orchestrate requests to both backend services directly using the end user's original Bearer Token.

```container
+-------------------------------------------------------------+
|              Shell Application (Container)                  |
|  +-------------------------------------------------------+  |
|  |             Customer Onboarding MFE                   |  |
|  +----+----------------------------------------------+----+  |
+-------|----------------------------------------------|------+
        | 1. POST /onboarding (Form Data)              | 2. POST /documents (Binary Payload)
        v                                              v
+-----------------------+                      +-----------------------+
|  Customer Onboarding  |                      |  Document Management  |
|     Microservice      |                      |     Microservice      |
+-----------------------+                      +-----------------------+
```

-   **Workflow:**

    1.  CO MFE submits customer onboarding details to **Customer Onboarding API** $\\rightarrow$ receives `ApplicationID`.

    2.  CO MFE sends the document binary directly to **Document Management API** along with the `ApplicationID` and the user's Bearer JWT token.

-   **Benefits:**

    -   Maintains **uninterrupted UX** (no page redirects or frame switches).

    -   Preserves full **user context** and audit trails on the Document Management API.

    -   Offloads heavy binary transfers entirely from the Customer Onboarding backend.

#### Option 2: Presigned Cloud Storage Uploads (Industry Gold Standard)

For optimal scalability and handling large files, neither business API should stream raw binary streams. Instead, pass control directly to cloud object storage (S3 / Azure Blob Storage).

```container
+--------+                      +---------------+                      +---------------+
| CO MFE |                      |    CO API     |                      |    DM API     |
+---+----+                      +-------+-------+                      +-------+-------+
    |                                   |                                      |
    | 1. POST /onboarding               |                                      |
    |---------------------------------->|                                      |
    |    Returns ApplicationID          |                                      |
    |                                   |                                      |
    | 2. POST /documents/upload-url (ApplicationID)                            |
    |------------------------------------------------------------------------->|
    |    Returns Presigned URL + DocumentID                                    |
    |                                                                          |
    | 3. PUT [BLOB Binary] directly to S3 / Azure Blob Storage                 |
    |------------------------------------------------------------------------->| Cloud Storage
```

-   **Workflow:**

    1.  CO MFE submits onboarding data to CO API and receives an `ApplicationID`.

    2.  CO MFE requests a time-limited **Presigned Upload URL** from the Document Management API.

    3.  CO MFE streams the file directly from the browser to Cloud Storage.

    4.  DM API is notified asynchronously when the upload completes to run validations or malware scanning.

-   **Benefits:**

    -   Zero memory or I/O overhead on both microservice backend APIs.

    -   Maximum upload reliability, throughput, and scalability.

### Architectural Decision Summary

| **Criteria** | **Pass-Through M2M (Current)** | **Direct MFE-to-DM Call (Option 1)** | **Presigned Storage URL (Option 2)** |
| --- | --- | --- | --- |
| **UX Continuity** | High | High | High |
| **User Identity Context** | Lost (M2M Token) | Preserved (User JWT) | Preserved (User JWT) |
| **CO Server Load** | High (Streams heavy binary) | Low (Metadata only) | Very Low |
| **Architectural Pattern** | Proxy Anti-Pattern | Direct Domain Routing | Enterprise Cloud Standard |