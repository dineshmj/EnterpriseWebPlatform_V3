# EWP V3: Data Privacy and Security Flow

**What this is.** A plain list of the precautions EWP V3 and the Partner Bank take to keep personal data private and the platform secure. It follows a request in the order it travels: from the browser, through sign-in, the BFFs and the APIs, into the databases and Kafka, out to other organisations, and finally to the build and the cloud demo. Each item says what is done, what it protects against, and where to find it.

**Where it comes from.** It covers the controls the AI architects raised in their five review rounds ([Response-to-AI-Architect-Reviews.md](AI-Architect-Reviews/Response-to-AI-Architect-Reviews.md)) and the ones built without anyone asking. For the full threat analysis see [Threat-Model.md](Threat-Model.md). For a per-component check, see the [Checklists](Checklists/README.md). Each component's README also has a "Security controls" table.

**What is not here.** What is still open, or accepted only for a PoC, is in [Threat-Model.md §7 Residual risks](Threat-Model.md#7-residual-risks). The last section below lists only the most important of those.

---

## 1. In the browser (Shell and micro front ends)

1. **The browser never holds a token.** Every screen calls only its own BFF with a session cookie. The access and refresh tokens stay on the server, so an XSS bug cannot steal them. See [Checklist 02](Checklists/02-Shell-SPA.md) and [Checklist 04](Checklists/04-MFE-SPAs.md).
2. **Nothing personal in browser storage.** The Shell remembers only the last menu entry visited, per user. It never stores a token or a customer's details there. See [Checklist 02 §1](Checklists/02-Shell-SPA.md).
3. **No raw HTML is rendered.** No screen uses React's raw-HTML escape hatch or writes HTML directly. React escapes everything it shows, which closes the most common XSS sink.
4. **Every state-changing call carries an anti-forgery token** (the Shell sends Duende's anti-forgery header; the MFEs send a token read from their BFF). A page on another site can neither read nor send it. This prevents CSRF. See [Checklist 04 §2](Checklists/04-MFE-SPAs.md).
5. **The Shell trusts only the frame it loaded.** It takes the micro front end's origin from the selected menu entry, never from an incoming message. It accepts messages only from that frame's window AND that origin, and posts only to it. See [page.tsx:131](../src/Shell/client-app/app/page.tsx#L131).
6. **The micro front ends trust only the Shell.** They accept messages only from the parent window and the configured Shell origin, so a hostile page that frames them cannot pose as the Shell. See [Checklist 04 §3](Checklists/04-MFE-SPAs.md).
7. **Notification links cannot become open redirects.** A link opens only if it matches a fixed pattern and belongs to a microservice in the person's own menu, and the BFF then checks the path again. See [notifications.ts:70](../src/Shell/client-app/app/lib/notifications.ts#L70).
8. **Step-up MFA happens in an in-page overlay, never a pop-up.** The overlay frames only a fixed step-up address on the requesting application's own origin. It closes unconfirmed after 5 minutes. The API still checks the fresh MFA itself. See [Checklist 02 §3](Checklists/02-Shell-SPA.md).
9. **What a screen shows is never the security check.** Branch, role and permission only decide what is displayed. Every API authorizes each request again.
10. **Static export only.** The front ends have no Node server of their own in production, which leaves one runtime fewer to patch and expose. Inline code runs only if its hash is in the CSP (see section 2, item 6).

## 2. At the BFFs (session, cookies, headers)

1. **Sessions live on the server.** The cookie only refers to a session row in PostgreSQL. A sign-out at the IDP (back-channel logout) can therefore end it, and a restart signs nobody out. See [Checklist 03 §3](Checklists/03-Shell-BFF.md).
2. **Session cookies are locked down.** Each is HttpOnly (script cannot read it), Secure (never over HTTP), SameSite=Lax (not sent on cross-site sub-requests) and named with the __Host- prefix (locked to this host). Sessions last 30 minutes and slide only on real activity. See [Shell Program.cs:149](../src/Shell/Program.cs#L149).
3. **Anti-forgery cookies are HttpOnly too.** A screen gets its request token from the BFF's csrf endpoint, never from the cookie, so an XSS bug cannot read it either. See [Accounts BFF Program.cs:80](../src/Microservices/Accounts/BFF.Web/Program.cs#L80).
4. **The KYC BFF compares anti-forgery tokens in constant time**, so response timing gives nothing away. See [csrf.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/csrf.ts).
5. **Session fixation is prevented.** The KYC BFF issues a new session ID at sign-in. See [oidc.service.ts:17](../src/Microservices/CustomerKyc/BFF.Web/src/auth/oidc.service.ts#L17).
6. **Strict Content-Security-Policy on every BFF.** Scripts and styles run only from the site itself or by SHA-256 hash, never as unhashed inline code. Hashes are computed from the exported pages at start-up, so a page altered after the build is blocked. The light Next.js BFFs use a per-request nonce instead. See [ContentSecurityPolicy.cs:7](../src/Common/WebUtilities/Security/ContentSecurityPolicy.cs#L7).
7. **Clickjacking is blocked.** The Shell is never framed. A micro front end may be framed only by the Shell and the IDP. The Shell itself may frame only the micro front ends and the IDP. See [Checklist 03 §5](Checklists/03-Shell-BFF.md).
8. **Further headers on every response:** nosniff, a strict Referrer-Policy, HSTS outside Development, and AllowedHosts limiting the host names answered.
9. **WebSocket hijacking is blocked.** A browser cannot add the anti-forgery header to a WebSocket upgrade, so the notifications hub accepts only a same-origin Origin header. See [Checklist 03 §4](Checklists/03-Shell-BFF.md).
10. **Each BFF's Data Protection keys sit in its own database schema**, under a fixed application name. Every instance of a BFF can read its own cookies, and no other BFF can. See [PersistentDataProtection.cs:72](../src/Common/WebUtilities/Security/PersistentDataProtection.cs#L72).
11. **Sessions stored by the Node BFFs are hashed and encrypted.** Only the SHA-256 of the session ID is stored, and the session (tokens included) is encrypted with AES-256-GCM under a key that is not in the database. Someone who reads the database can neither hijack nor read a session. See [session-store.ts](../src/Microservices/CustomerKyc/BFF.Web/src/auth/session-store.ts).
12. **The sign-in round trip is protected.** State, nonce and PKCE verifier sit in a short-lived, encrypted, HttpOnly cookie that works once. A callback without that cookie is refused, which blocks login CSRF. See [Checklist 06 §1](Checklists/06-KYC-BFF-NestJS.md) and [Checklist 07 §1](Checklists/07-Light-BFFs-NextJs.md).
13. **Return URLs come from an allow-list** of the app's own pages. This prevents open redirects (phishing). See [Checklist 05 §3](Checklists/05-MFE-BFFs-DotNet.md).
14. **A BFF never repeats a change automatically.** Only GETs are retried. A payment or a decision is never sent twice by the BFF.
15. **Per-caller rate limits** on every BFF, answering 429 with Retry-After.
16. **BFFs are thin.** They forward the call with the person's token and hold no business rules, so no authorization decision lives only in a BFF.

## 3. At the IDP: staff sign-in

1. **Passwords are hashed with PBKDF2 and a per-user salt**, so a copied database does not reveal them. See [PasswordManager.cs:9](../src/IDP/Security/PasswordManager.cs#L9).
2. **No user enumeration.** When the user name does not exist, the password is still checked against a dummy hash. The answer takes as long as for a real user, so timing does not reveal which users exist. See [UserRepository.cs:151](../src/IDP/Repositories/UserRepository.cs#L151).
3. **The account locks for 15 minutes after 5 wrong passwords** (brute force). See [UserRepository.cs:168](../src/IDP/Repositories/UserRepository.cs#L168).
4. **Sign-ins are throttled per IP address** (20 a minute), against password spraying. See [IDP Program.cs:114](../src/IDP/Program.cs#L114).
5. **No SQL injection.** User lookups go through Entity Framework queries, so input always travels as a parameter and is never pasted into SQL text. See [UserRepository.cs](../src/IDP/Repositories/UserRepository.cs).
6. **TOTP MFA is protected at every step.** The secret is encrypted with Data Protection, recovery codes are stored hashed, and each code is claimed with one atomic update so it never works twice. Wrong codes count towards the lockout. See [MfaService.cs:75](../src/IDP/Security/MfaService.cs#L75).
7. **MFA cannot be switched off by mistake.** The IDP and every API that needs MFA refuse to start with MFA off outside Development.
8. **The half-finished sign-in waits in a five-minute cookie** that is encrypted, authenticated, HttpOnly and named with the __Host- prefix. If someone changes the cookie it fails to decrypt; it does not grant a sign-in. See [PendingSignIn.cs:40](../src/IDP/Security/PendingSignIn.cs#L40).
9. **IDP sessions live on the server.** An expired session triggers back-channel logout to every client. See [IDP Program.cs:182](../src/IDP/Program.cs#L182).
10. **Sign-in pages are never framed.** The only exception is the step-up code page, which the Shell's own origin alone may frame, for its overlay. See [SecurityHeadersAttribute.cs:13](../src/IDP/SecurityHeadersAttribute.cs#L13).
11. **Pushed Authorization Requests (PAR) are required for every staff app.** The BFF pushes the request directly to the IDP. The browser carries only a one-time reference valid for 60 seconds, so nobody can read or change scope, redirect or PKCE on the way. See [IDP README](../src/IDP/README.md) #25.
12. **No client secrets for staff apps (private_key_jwt).** Each app proves itself with a one-minute assertion signed by its own key. The IDP holds only public keys, so there is no secret anywhere to copy. See [IDP README](../src/IDP/README.md) #26.
13. **Only Authorization Code + PKCE.** There is no implicit or hybrid flow, no tokens through the browser and no wildcard redirect. Redirect addresses are exact.
14. **Refresh tokens are one-time and stored hashed.** A replayed refresh token is useless.
15. **Tokens carry only what their audience needs.** Claims are added per request and only for the scopes asked for.
16. **The signing key is a developer key in Development only.** Anywhere else the IDP will not start without a signing certificate. See [SigningCredentialExtensions.cs:52](../src/IDP/Security/SigningCredentialExtensions.cs#L52).
17. **Fail closed on missing secrets or keys.** A missing client key or secret stops the IDP from starting. See [ClientSecretStore.cs](../src/IDP/Security/ClientSecretStore.cs).
18. **The Bruno API-testing client exists only in Development.** A client without a secret must never exist where real data is.
19. **Customers and staff are separate populations.** They have separate tables, pages and clients, and each sign-in page refuses the other population's client. A customer sign-in can never open a staff screen. See [IDP README](../src/IDP/README.md) #15.

## 4. At the IDP: the customers' own channel (My EWP)

1. **FAPI 2.0 Security Profile.** PAR, PKCE, private_key_jwt, DPoP-bound access and refresh tokens, and PS256 signatures. See [IDP README](../src/IDP/README.md) #16, #17.
2. **Online registration cannot be used to probe for customers.** Customer Onboarding answers only "match" or nothing. Every outcome shows the same page and wording, and no code is sent on a miss. Attempts are throttled per IP. See [IDP README](../src/IDP/README.md) #18.
3. **Registering in another customer's name is blocked.** The e-mail on record must match too, and the code goes to the customer's own mobile on record. Each customer can have only one online identity; a second registration is refused, never treated as a takeover.
4. **One-time codes are hard to guess and replay.** Each is 6 digits, lasts 5 minutes, allows 5 tries counted on the server, and is accepted once. A wrong code counts towards the lockout.

## 5. Tokens between BFFs, Journey APIs and Domain APIs

1. **Every API checks every token completely.** It checks the issuer, the audience (this API), the signature, the lifetime, and the access-token type, so an ID token or a token for another API never passes. See [Checklist 09 §1](Checklists/09-Domain-APIs.md).
2. **Every token is DPoP-bound.** Each token is tied to its holder's key, and every use carries a fresh proof for that method and address, accepted once. A token copied from a log, a memory dump or a proxy is useless without the private key. A token without a binding is refused. See [Threat-Model.md TB2](Threat-Model.md#tb2--bffs--idp).
3. **Delegation uses token exchange with an allow-list** (RFC 8693). The allow-list says which client may exchange which token for which audience. A token without a person is never exchanged, and the acting client is recorded in the act claim. See [TokenExchangeGrantValidator.cs:39](../src/IDP/Security/TokenExchangeGrantValidator.cs#L39).
4. **The token rules are implemented the same way in .NET and NestJS**, with a parity checklist so the two stacks cannot drift apart. See [Threat-Model.md TB3, parity checklist](Threat-Model.md#token-rules-implemented-twice-net-and-nestjs---parity-checklist).
5. **Journey APIs accept only delegated tokens:** a person, the endpoint's scope, their own web app as the acting client, and the endpoint's permission. A stolen machine token cannot get in. See [Checklist 08 §1](Checklists/08-Journey-APIs-NestJS.md).
6. **Journey APIs refuse to follow redirects.** A domain API or the IDP could otherwise carry their token, client assertion or admin key to another host.
7. **Claim names are kept exactly as issued**, with no remapping, so a renamed claim cannot silently weaken authorization.
8. **Machine clients (workers) have one write scope each**, prove themselves with a key, and are DPoP-bound. A stolen worker credential opens one endpoint only. See [CachedM2MTokenClient.cs](../src/AsyncWorkflows/Infrastructure/Subscribers/CachedM2MTokenClient.cs).

## 6. Authorization inside the APIs

1. **Deny by default.** Every domain API uses one helper with an authenticated fallback policy, so a new endpoint without an attribute is never public. Journey APIs refuse an endpoint without an access rule. See [ApiEndpointAuthorization.cs:29](../src/Common/WebUtilities/Security/ApiEndpointAuthorization.cs#L29).
2. **Scope plus role or permission per operation.** The scope is the coarse boundary; the permission decides.
3. **Object-level checks against BOLA (OWASP API1).** Branch scope (ABAC) and relationships such as managing agent or assigned officer (ReBAC) are checked both in the database query and in the aggregate. A record outside the person's scope answers 404. See [Checklist 09 §2](Checklists/09-Domain-APIs.md).
4. **Branch and identity always come from the token**, never from a header or the request body.
5. **Separation of duties.** The maker is never the checker, and a KYC decider never decides the same application's compliance case. If the initiator is unknown, everyone is denied (fail closed). See [Threat-Model.md §5](Threat-Model.md#5-abuse-cases-banking).
6. **Approval limits by clearance**, and the screened risk sets the clearance needed in Compliance. A failed sanctions screening never counts as a pass.
7. **Step-up MFA for decisions.** Approving needs an MFA sign-in from the last 15 minutes, not just MFA at some point. See [MfaStepUp.cs:46](../src/Common/WebUtilities/Security/MfaStepUp.cs#L46).
8. **Internal endpoints pin the exact client ID**, not just the scope. A stolen worker token cannot post fake workflow steps. The Partner Products one also refuses any token that carries a person.
9. **The auditor's permission counts only through the Audit Journey API**, where every look is itself audited. An auditor role combined with a branch role cannot be used to browse payments unrecorded.
10. **Money cannot be created from outside.** Cash deposits are accepted only from the Payments BFF's client, for a person with the deposit permission, in the account's own branch, and up to 9,999.99. Accounts open at 0.
11. **Four eyes on risky operations.** This covers manual payment reconciliation, resolving an unconfirmed payment, and erasing personal data.
12. **A feature flag can only switch something off.** A switched-off endpoint answers 404 before authentication. A missing flag source changes nothing, and permissions always stay with the policies. See [feature-flags/README.md](../feature-flags/README.md).
13. **The operations "switches" exist only outside production.** These are the simulator behaviour switch and the fault routes. Each needs its own permission and a recent MFA sign-in, is logged with the operator's LAN ID, and turns itself back after 15 minutes. See [Operations Journey API README](../src/Microservices/Operations/JourneyApi/README.md).

## 7. Input, errors and abuse

1. **Errors never leak internals.** All errors are Problem Details: the .NET APIs return only a trace ID (no stack trace, SQL or exception text), and the Journey APIs a generic detail. See [Accounts ApiExceptionHandler.cs](../src/Microservices/Accounts/API/Controllers/ApiExceptionHandler.cs).
2. **Per-caller rate limits** (per person, per machine client and per anonymous IP), with changes limited far more tightly than reads. See [RateLimiting.cs:33](../src/Common/WebUtilities/Security/RateLimiting.cs#L33).
3. **Idempotency-Key on payments**, bound to the person and the payment's details, backed by unique constraints. The same key with other details is refused with 409, so a retry never pays twice.
4. **Uploads are checked by their bytes.** The content type comes from the file's magic bytes (PDF, PNG or JPEG), and a mismatch is refused. A script cannot be uploaded as "evidence". See [DocumentContentPolicy.cs](../src/Microservices/DocumentsManagement/API/Domain/Policies/DocumentContentPolicy.cs).
5. **Documents are served safely.** Each is served with its verified content type and nosniff. Only a PDF is shown inline and everything else downloads, so an upload never runs as HTML (stored XSS).
6. **Strict input shapes.** Document IDs, application numbers and notification parameters are checked against exact patterns before use.
7. **Database CHECK constraints keep invalid states out** (status values, amounts, the four-eyes columns), even if the application has a bug.
8. **Optimistic concurrency.** Every aggregate has a Version, plus row locks where two people can act at once.
9. **SignalR limits:** at most 10 connections per person and 4 KB messages, and the connection closes when the token expires. See [NotificationsHub.cs](../src/Microservices/Notifications/API/Hubs/NotificationsHub.cs).
10. **Swagger exists only in Development.**

## 8. Personal data at rest (privacy by design)

1. **Names, e-mail addresses, phone numbers and address lines are never stored as typed.** Each customer has their own data key, and every personal field is encrypted with it using AES-256-GCM. Someone who gets into the database sees only ciphertext. See [Crypto-Shredding.md](Crypto-Shredding.md) and [PersonalDataCipher.cs:34](../src/Microservices/CustomerOnboarding/API/Infrastructure/Privacy/PersonalDataCipher.cs#L34).
2. **A value copied onto another customer's row fails to decrypt.** The customer number is bound in as associated data, so copying ciphertext between rows does not work.
3. **Each customer's data key is stored only wrapped** by a master key. The API refuses to start if a master key is missing, shorter than 32 bytes, or the same as the blind-index key.
4. **The database refuses plain text.** A personal field without the encrypted-form prefix is rejected, so a code bug cannot slip clear text in.
5. **Searching without decrypting.** E-mail and surname match only exactly, through an HMAC-SHA256 blind index. On its own, the index only confirms a value someone already knows.
6. **Every context encrypts its own copy under its own key.** KYC (applicant), Compliance (applicant) and Accounts (holder name) each encrypt their copy under a key of their own. See [Crypto-Shredding.md, What is where](Crypto-Shredding.md#what-is-where).
7. **Only non-identifying location stays readable:** city, state, postcode and country. On their own these identify nobody, and the branch scope needs them.
8. **KYC documents are encrypted at rest with envelope encryption.** Each document has its own AES-256-GCM key, wrapped by a rotatable key-encryption key, with the document ID bound in. See [EncryptedDocumentStorage.cs](../src/Microservices/DocumentsManagement/API/Infrastructure/Storage/EncryptedDocumentStorage.cs).
9. **Right to erasure (crypto-shredding).** A prospect is erased by deleting their key, which makes their data in every copy unreadable while the audit trail stays intact. Every context erases its own copy and confirms only after it is done. Documents are deleted and their file names blanked. See [Crypto-Shredding.md](Crypto-Shredding.md#the-erasure-itself-one-operator-asks-another-decides).
10. **Erasure cannot be done by one person alone.** One operator asks and another decides; the database enforces this too. Each step needs its own permission and a recent MFA. Only a prospect unchanged for the retention period qualifies. A reason code is required, never free text.
11. **Data in transit has an end date.** Kafka keeps messages for 7 days, and the Outbox relay blanks a published payload 7 days after sending it. See [CustomerOutboxPublisher.cs](../src/AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs).
12. **The audit trail and notifications hold no personal data.** They hold only IDs and outcomes, so they never need erasing and the hash chain still verifies.
13. **Data minimisation towards other parties.** The screening provider receives only name and address. Partner Products audit events carry identifiers only, with no name, date of birth or mobile.
14. **Customer facts are read again, never typed.** For a Form VII, the agent does not type the customer's name, mobile or KYC status; they are read from Customer Onboarding.

## 9. Database access (PostgreSQL)

1. **One database per bounded context.** Database-per-context is enforced by the server, not by convention. See [Checklist 13](Checklists/13-PostgreSQL-Database-Users.md).
2. **One login per deployable; no component uses the superuser.** A leaked connection string exposes one component's data, not everybody's.
3. **Logins have no special powers.** None is a superuser, none can create databases or roles, and none inherits rights. See [EwpServiceDbUsers.sql:64](../db/EwpServiceDbUsers.sql#L64).
4. **CONNECT is revoked from PUBLIC on every database** and granted only to that database's own login(s). See [EwpServiceDbUsers.sql:75](../db/EwpServiceDbUsers.sql#L75).
5. **No DDL for application logins.** Logins cannot create objects, and the objects are owned by the administrator.
6. **Grants name exact privileges, never ALL.** No login has TRUNCATE, REFERENCES or TRIGGER. See [Checklist 14](Checklists/14-PostgreSQL-Table-Privileges.md).
7. **The Outbox relay can only read and mark its outbox rows.** The Customer Onboarding relay's login may SELECT and UPDATE only the outbox table: no insert, no delete, no other table. A compromised relay cannot change customers or applications. See [EwpServiceDbUsers.sql:152](../db/EwpServiceDbUsers.sql#L152).
8. **The Outbox and Inbox cannot be deleted from.** Even the Customer Onboarding API's own login has no DELETE on them, so a compromised API cannot erase the history of what was published. See [EwpServiceDbUsers.sql:143](../db/EwpServiceDbUsers.sql#L143).
9. **No DELETE where nothing is deleted.** The KYC, Compliance, Accounts, Notifications, Payments and Partner Products logins have SELECT, INSERT and UPDATE only. An injection or a compromised API cannot erase evidence.
10. **The audit login may only SELECT and INSERT**: no update, no delete.
11. **BFF state is separated per BFF.** Each BFF has its own schema and login in the shared BFF database, so one BFF's credentials cannot read another's sessions or keys.
12. **Default privileges keep grants stable.** A recreated table gets exactly the same grants, never wider ones.
13. **Row-level security was considered and not needed.** Each context has its own database and branch scope is applied in every query. See [Checklist 14 §2](Checklists/14-PostgreSQL-Table-Privileges.md).

## 10. Audit trail

1. **Append-only twice over.** The login may only add rows, and database triggers refuse UPDATE, DELETE and TRUNCATE for every user, including the administrator. See [EwpAuditDb.sql:88](../src/Microservices/Audit/API/AuditDb/EwpAuditDb.sql#L88).
2. **Hash chain, verified continuously.** A changed or removed entry breaks the chain, and readiness turns Degraded.
3. **Signed checkpoints outside the database** (ECDSA P-256, write-once files). Even an administrator who recomputes the whole chain is caught. See [AuditAnchors.cs](../src/Microservices/Audit/API/Infrastructure/AuditAnchors.cs).
4. **Readiness also degrades** when verification is overdue, the newest entry is not anchored, or checkpoints go missing.
5. **Every look by an auditor is itself audited.**

## 11. Messaging (Outbox, Kafka, Inbox)

1. **Transactional Outbox.** State and the events it produces are saved in one transaction. Nothing is published that did not happen, and nothing that happened is lost. See [Checklist 10](Checklists/10-Outbox-Publishers.md).
2. **Inbox (idempotent consumers).** A redelivered message is answered "already processed" and never acted on twice.
3. **One Kafka user per component** (SASL/SCRAM-SHA-512). A client without credentials cannot connect at all. See [kafka/README.md](../kafka/README.md).
4. **ACLs deny by default.** Nothing is allowed unless granted. Producers write only their own topics, and consumers read only their own topics and group. A compromised service cannot forge events on another context's topic. See [Setup-KafkaSecurity.ps1:284](../ps/kafka/Setup-KafkaSecurity.ps1#L284).
5. **Topics are never created automatically**, so a typo cannot create a new, unprotected topic. See [Setup-KafkaSecurity.ps1:317](../ps/kafka/Setup-KafkaSecurity.ps1#L317).
6. **TLS on the Kafka listener.** The broker certificate is checked against the EWP trust root, host name included. SCRAM protects the password, and TLS protects the events themselves.
7. **Kafka clients fail closed.** Without SASL settings, a client does not start. See [KafkaClientSecurity.cs:22](../src/AsyncWorkflows/Infrastructure/Kafka/KafkaClientSecurity.cs#L22).
8. **The Kafka UI is read-only.**
9. **Saga replies are accepted only for the current command.** A forged or stale "funds held" message cannot move a payment.
10. **Poison messages go to a dead-letter topic**, so one bad message cannot stop a partition.
11. **A new consumer group starts at the end of its topic**, so it never replays every retained message, including personal data.

## 12. Long-running workflows (Temporal)

1. **Workflow input is a business reference only.** The workflow history stores no token, account number, amount or personal data, because the history is kept and shown in the Temporal UI. See [Checklist 15](Checklists/15-Workflow-Workers-Temporal.md).
2. **The worker has its own machine identity**, never a stored person's token. It holds one DPoP-bound token per API and proves itself with a key. The internal endpoints that accept it refuse any token carrying a person.
3. **Exactly one ending.** Gold is allocated only for the purchase's own completed payment, and only an unpaid order can be cancelled. The APIs check again before acting.
4. **Install scripts are not run** for the worker's dependencies, except one that is named explicitly.

## 13. External systems (core banking, payment network, screening provider)

1. **SSRF is prevented by configuration checks.** Outside Development, every base URL must be HTTPS, on a permitted host, and not a loopback or private address. Redirects are never followed. See [ExternalEndpoint.cs:13](../src/Common/WebUtilities/Security/ExternalEndpoint.cs#L13).
2. **Every call carries an Idempotency-Key**, the same on each retry. A retried account opening or payment never happens twice, and a refusal is final.
3. **A resilience pipeline on every call:** timeouts, retries with jitter and a circuit breaker. Saga timeouts and compensation stop one slow provider from cascading.
4. **The fraud check never lets a payment through blindly.** It has a 250 ms budget. Without a usable answer, fallback rules send a new payee or an amount of AUD 500 or more to an officer. Only a well-formed answer counts as "allow".
5. **The external call happens outside the database transaction.** A row lock is never held while waiting for the payment network.
6. **Unknown outcomes are reconciled, never released blind.** The saga asks the network by Idempotency-Key, and any manual resolution needs four eyes.

## 14. Partner Bank: EWP's side (Partner Products)

1. **Mutual TLS with EWP's client certificate.** The Partner Bank server is trusted only through the PoC's own root. See [PartnerBankClient.cs](../src/Microservices/PartnerProducts/API/PartnerBank/PartnerBankClient.cs).
2. **EWP's token is bound to its certificate** (RFC 8705). A stolen token is useless without EWP's private key.
3. **Every instruction is signed** (ES256, 5 minutes) with an ID fixed at the first send. The Partner Bank can prove what EWP asked, and EWP cannot deny it later.
4. **Once only, even across a lost answer.** The state and the instruction ID are saved before the call, so a resend repeats the same instruction, and unique indexes have the final word.
5. **The payee never comes from a request.** It is the Partner Bank's settlement account from configuration, checked at start-up.
6. **A purchase is paid as the staff member**, so Payments' own permission, approval tier and four eyes still apply.
7. **Transfer slips cannot be reused or misused.** The paying account must be one of the customer's active accounts, and the slip's phone number must be the mobile on record. The slip must be signed today, and each slip buys once.
8. **The signed Form VII is required as evidence.** The scan of the signed form is required and its SHA-256 recorded, the agent confirms photo ID and signature, and each customer may have only one application.
9. **"View at the Partner Bank" is a PAR-style handoff.** Each click creates a new signed staff view, pushed over mutual TLS. The browser gets only a random one-time reference valid for 60 seconds, so a copied link is useless.
10. **A kill switch can pause Digital Gold sales** without a redeploy, and never leaves an order at the Partner Bank unpaid.
11. **EWP's keys are referenced only as file paths in configuration.** A missing key file stops the API.

## 15. Partner Bank: its own side

Source: the Partner Bank repository's [README "Security controls"](https://github.com/dineshmj/EnterpriseWebPlatform_V3_PartnerBank/blob/main/README.md#security-controls) and [Threat-Model.md](https://github.com/dineshmj/EnterpriseWebPlatform_V3_PartnerBank/blob/main/doc/Threat-Model.md).

1. **No shared secret with EWP.** EWP's client certificate must chain to the trust root only, be valid now, carry the client-authentication purpose and have exactly the configured subject (controls 1, 6).
2. **Two ports.** The public port never asks for a client certificate. The mutual-TLS port requires one in the handshake and serves only PAR, token, revocation and introspection (controls 6, 6a, 13).
3. **PAR required for every client, and PKCE S256 only.** A pushed request is valid for 60 seconds and used once (controls 2, 3).
4. **Short lifetimes.** Codes last 60 seconds, access tokens 5 minutes, and there are no refresh tokens (control 5).
5. **Certificate-bound tokens on every external call.** The same token over another certificate is refused (controls 4, 14).
6. **Signed requests (JAR) and Rich Authorization Requests (RAR).** Every value is allow-listed. The approved detail travels into the token through the stored code, never through the browser (controls 12a to 12c).
7. **Explicit customer consent for every request from EWP.** Consent is never remembered, the page uses plain words, and account numbers are masked to the last four digits (control 12d).
8. **Signed distributor instructions** with strict contents. The database enforces once only: one account per Form VII and one purchase per slip (controls 18d to 18f).
9. **The Partner Bank makes its own checks.** It acts only on accounts this distributor introduced, and the slip's mobile must be the account's. The customer is told of every account and order by SMS (control 18g).
10. **Orders come only from the consented authorisation.** Amount, account, customer and staff member come from the token, never from a request body (control 18a).
11. **Its own customer accounts are protected.** Passwords are hashed with PBKDF2, the account locks for 15 minutes after 5 failures, and an unknown user name costs the same work as a known one (control 10a).
12. **No open redirect**, and one message for both an unknown user and a wrong password (control 10).
13. **A fresh sign-in for every request from EWP**, a 10-minute idle session and a real sign-out (controls 10c, 21b).
14. **The staff view is read-only and works once.** It has no cookie and is sent with no-store and no-referrer. Every push and every opening is logged (control 22a).
15. **The customer's window holds no data.** It calls the internal API with the customer's own token, kept server-side (control 20).
16. **The internal port is localhost-only.** In a cluster, only the window and the authorization server may reach it (controls 13, 28).
17. **Browser security headers.** A CSP without inline code, never framed, nosniff and no referrer (control 9).
18. **Fail closed at start-up.** With no server certificate, password or trust root, there is no server (control 8).
19. **Error details appear only in Development** (control 11).

## 16. Logs, monitoring and observability

1. **No full account numbers in logs.** Only the BSB and the last four digits are logged. See [LogMasking.cs](../src/Common/Observability/LogMasking.cs).
2. **No query strings in the Node services' logs**, and a trace ID on every line.
3. **Security events are logged.** IdentityServer events (success, failure, error) go into the structured log, and so do operator actions with their LAN ID.
4. **Grafana in the cloud demo is reached only through EWP's IDP.** There is no login form, basic auth or anonymous access. Only operations and platform administrators get in, and the log stores accept writes only from the collector and reads only from Grafana. See [Threat-Model.md TB3](Threat-Model.md#tb3--callers--apis).

## 17. Build, supply chain and CI

1. **main is protected.** Changes arrive only as a squash-merged pull request with "CI result" green, force pushes and deletion are blocked, and nobody is on the bypass list. See [CI.md](CI.md#branch-protection-ruleset-for-main).
2. **Vulnerable packages fail CI.** A HIGH or CRITICAL advisory fails the NuGet check (transitive packages included) and the production pnpm audit. See [ci.yml](../.github/workflows/ci.yml).
3. **Lockfiles are enforced**, so a lockfile out of step with its package file fails the build.
4. **Code scanning:** CodeQL (security-extended, the workflows included), SonarQube Cloud and Snyk. See [code-scanning.yml](../.github/workflows/code-scanning.yml).
5. **Secret push protection is on in GitHub**, so a push containing a known secret format is refused before it reaches the history.
6. **No install scripts run during builds**, except one named package. This blocks supply-chain attacks of the event-stream kind.
7. **Pinned dependencies.** Actions are pinned to commit SHAs and base images to tag plus digest. Tools are kept only if their published SHA-256 matches. Dependabot proposes updates weekly. See [dependabot.yml](../.github/dependabot.yml).
8. **Development secrets stay out of Git**, the developer signing key included.

## 18. Containers and Kubernetes

1. **Images are built only by CI from a commit.** hadolint runs, and Trivy scans for fixable HIGH or CRITICAL vulnerabilities and for secrets; any finding stops the image. On main, images are published with an SBOM and SLSA provenance. See [Containers.md](Containers.md#what-ci-does-per-image).
2. **Nothing secret in an image.** Settings and secrets come only from the environment at run time, and key material never enters the build context. See [.dockerignore](../.dockerignore).
3. **Minimal runtimes.** .NET uses chiseled images and Node uses distroless: no shell, no package manager, and a non-root user.
4. **Locked-down pods.** Pods run as non-root with a read-only root file system and no privilege escalation. All capabilities are dropped, RuntimeDefault seccomp applies, and no service-account token is mounted (Pod Security Standard "restricted"). See [Kubernetes.md](Kubernetes.md) K7.
5. **TLS between pods from a private CA.** Certificates are renewed with a new key each time, and the Gateway re-encrypts to the pod and checks its certificate (K3, K4).
6. **Default-deny network policies, both ways.** A pod accepts traffic only from its callers and reaches out only to what it calls (K6, K12).
7. **Secrets are referenced by name only**, and certificate files are mounted read-only (K5).
8. **Required values refuse to render**, so a half-configured release never reaches a cluster (K10).

## 19. Cloud demo (Azure)

1. **No stored deployment credential.** GitHub's short-lived OIDC token is exchanged for the demo identity, and only for this repository's demo environments on main. See [Azure.md](Azure.md) A1, A10.
2. **The demo identity cannot widen its own rights.** It has no subscription rights, and role assignments are limited to a short list (ABAC condition) (A2 to A4).
3. **The Terraform state is protected.** It uses Entra ID sign-in only (no account key exists), with versions, soft delete and a delete lock (A5).
4. **DNS takeover is blocked.** The identity can write only A and CNAME records, CAA allows Let's Encrypt only, and the zones carry delete locks (A8, A9).
5. **Secrets go only to Key Vault**, never printed and never in GitHub or the state. There is one vault per organisation, read through workload identity. The demo PKI's root key is never stored. See [Cloud-Demo.md](Cloud-Demo.md) CD14, CD15.
6. **No public database, and Entra-only access to the cluster and the vault.** The cluster has no local admin certificate.
7. **Cost and leftovers are watched.** Budget alerts are set, and an hourly watchdog removes any demo older than 6 hours.

## 20. Known limits (accepted for the PoC)

The full list, with production answers, is in [Threat-Model.md §7](Threat-Model.md#7-residual-risks). The most important for privacy and security:

1. **Development secrets and certificates sit in configuration files** (R-10). In production they move to a secret store.
2. **Crypto-shredding and backups (R-22).** The personal-data keys share a database with the data, so a backup taken before an erasure still holds the key.
3. **Rate limits and replay caches count per instance** (R-14). In production they must be deployment-wide.
4. **Temporal runs as the development server**, with no TLS and no authorization on its frontend (R-16).
5. **Service-to-service authentication uses DPoP-bound tokens, not mutual TLS** (R-9). Towards the Partner Bank, mutual TLS is in place.
6. **Customers' second factor is a simulated SMS code** (R-17).
7. **Partner Bank: no certificate revocation, and EWP's signing key sits in a file** (Partner Bank R1, R8).