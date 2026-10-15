Below is a short, clear human-style conversation that shows the full interaction (happy path + key failure branches) where a SPA posts data to its BFF, the BFF exchanges the human’s access token at the IDP for a delegated token (RFC 8693) and then calls `Documents Management` with that delegated token. Implementation notes are in brackets.

---

# Delegated Token — Human-style Conversation

P1 (MFE SPA):  
"P2, I have the user's upload — here's a `POST` with the file and metadata."

P2 (BFF):  
"P1, I received your `POST`. I hold the user's server session and the user's Access Token, but I must call Documents Management (P4) on the user's behalf. P3 (IDP), I need a delegated token so I can act for the user."

P3 (IDP):  
"P2, tell me who you are and present the subject token you want me to validate."

P2 (BFF):  
"P3 — here is my client auth and the exchange request: `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token=<user-access-token>`, `subject_token_type=access_token`, requested scope `documents-management.write`."

P3 (IDP):  
"Okay. I validate the `subject_token` (is the user session valid?), check that your client is permitted to exchange and request that scope, and apply any policy (e.g., `branch` claim allowed). If that all passes I will issue a short‑lived delegated access token whose subject is the user and which carries scopes/claims like `branch`, `sub` and `documents-management.write`."

P2 (BFF):  
"P3, token received. I will cache it for its TTL keyed by `(user, target, scope)` to avoid exchanging on every request. Now I call P4."

P2 → P4 (Documents Management):  
"`POST /api/documents` — `Authorization: Bearer <delegated-token>`. Body: the file and metadata."

P4 (Documents Management):  
"I verify the bearer token (signature, expiry, audience, scopes). I confirm the token's subject and `branch` claim, enforce branch scoping and audit who performed the upload. Request accepted — Document created with id=12345."

P2 (BFF) → P1 (MFE SPA):  
"201 Created — here is the document id and link. Upload complete."

---

Failure branches

P3 (IDP) → P2:  
"If the exchange is not permitted (policy, expired `subject_token`, or your client not allowed that scope) I return an error (`400`/`403`)."

P2 (BFF):  
"On exchange failure I log and audit the error, and return a user-friendly error to P1 (e.g., 'Unable to complete upload — try signing in again' or 'Service unavailable')."

P4 (Documents Management) → P2:  
"If the delegated token is rejected (expired or missing scope), I return `401`/`403`."

P2 (BFF):  
"On `401`/`403` from P4 I may try one fresh token exchange (if the previous token was expired), then retry the call once. If that fails again I fail fast and return an appropriate error to P1."

---

Operational notes (brief)

- P2 never forwards the user's raw Access Token to P4 or the browser; only the short‑lived delegated token is used server‑to‑server.  
- Token exchange uses RFC 8693: `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token`, `subject_token_type`, requested `scope`.  
- Cache delegated tokens for their TTL to reduce IDP load; respect expiry and revocation.  
- P4 must validate tokens strictly (JWKs, audience, scope, expiry, required claims like `branch`) and audit actions by token subject.  
- Instrument and audit every exchange and delegated call for observability and compliance.