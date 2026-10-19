# Partner Products Journey API

The counter's Digital Gold journeys (NestJS) - the customer's pattern, like the Audit and Operations Journey APIs. EWP acts as the Partner Bank's **distributor** here: the customer signs a paper form at EWP's counter (Form VII once, a transfer authorization slip per purchase) and the staff member records it.

```
Partner Products web (Next.js SPA + light BFF)  --token exchange-->  Partner Products Journey API
    --token exchange-->  Customer Onboarding API      (the customer: name, mobile, KYC status - read again on every record)
    --token exchange-->  Accounts API                 (the customer's active accounts in the staff member's branch)
    --token exchange-->  Documents Management API     (the scan of the signed form; its SHA-256)
    --token exchange-->  Partner Products API         (the record of Form VII / the slip; sending it to the Partner Bank)
    --token exchange-->  Payments API                 (the purchase's payment, started AS the staff member)
    --start workflow-->  Temporal                     (the purchase's follow-up, run by the Partner Products Worker)
```

It holds no data and no business rules of its own: it composes the Domain API calls of one screen, always FOR the signed-in staff member. It holds NO key of the Partner Bank's: sending a form there is the Partner Products API's job (its adapter holds EWP's certificate and signing key) - this API only asks it to send.

| | |
|---|---|
| Address | https://partner-products-journey.dev.localhost:46385 |
| Start | `runnow.bat` (or `.\ps\run\Start-NodeServices.ps1 -Only PartnerProducts`) |
| Accepts | Delegated tokens only: audience `partner-products-journey-api`, acting client `PartnerProducts.Microservice.Web.ClientID` |

## Endpoints

| Endpoint | Scope | Permission | What |
|---|---|---|---|
| `GET /v1/journeys/partner-products/customers?search=` | `partner-products-journey.read` | `partner.gold.view` | Customers of the staff member's branch (Customer Onboarding) |
| `GET /v1/journeys/partner-products/customers/{customerId}` | `partner-products-journey.read` | `partner.gold.view` | The customer (mobile masked), their paying accounts and their Digital Gold - three contexts in parallel; once the account is open, the holding read from the Partner Bank |
| `GET /v1/journeys/partner-products/applications` | `partner-products-journey.read` | `partner.gold.view` | The branch's latest Form VII applications |
| `POST /v1/journeys/partner-products/applications/{number}/send` | `partner-products-journey.write` | `partner.gold.order` | Ask the Partner Products API to send Form VII to the Partner Bank (only an EWP application number reaches the path) |
| `POST /v1/journeys/partner-products/customers/{customerId}/form-vii` | `partner-products-journey.write` | `partner.gold.order` | Multipart: Form VII's number, date signed, date of birth, the two checks, the scan |
| `POST /v1/journeys/partner-products/customers/{customerId}/transfer-slips` | `partner-products-journey.write` | `partner.gold.order` | Multipart: slip number, date signed (today), amount, paying account, phone number on the slip, the check, the scan |
| `POST /v1/journeys/partner-products/applications/{number}/partner-view` | `partner-products-journey.read` | `partner.gold.view` | "View at the Partner Bank": the one-time address of the Partner Bank's read-only page for the open account (the Partner Products API pushes the signed request) |
| `POST /v1/journeys/partner-products/purchases/{number}/buy` | `partner-products-journey.write` | `partner.gold.order` (+ `payment.initiate` at Payments) | "Send & pay" / "Continue": send the slip (ORDERED), start its payment at EWP Payments as the staff member, start the purchase workflow - each step once, the open ones only |

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | Delegated tokens only: issuer, audience, `typ: at+jwt`, the endpoint's scope, and the Partner Products web BFF as the acting client | Any client, or a person's own token, could call the API | Token confusion; calls bypassing the web BFF | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts) |
| 2 | Deny by default: an endpoint without an access rule refuses everybody; each needs a permission (`partner.gold.view`, `partner.gold.order`) and the person must have a branch | A new endpoint would be open to any signed-in person | Broken function-level authorization (OWASP API5) | [delegated-token.guard.ts](src/auth/delegated-token.guard.ts), [digital-gold.controller.ts](src/digital-gold/digital-gold.controller.ts) |
| 3 | The customer's facts are READ AGAIN from Customer Onboarding on every record, for the staff member (their branch) - name, mobile and KYC status never come from the screen | A tampered request could record a form for another person, a customer of another branch, or an unidentified one | Mass assignment / broken object-level authorization (OWASP API1, API3) | `recordFormVii`, `recordTransferSlip` in [digital-gold.service.ts](src/digital-gold/digital-gold.service.ts) |
| 4 | A slip's paying account must be one of the customer's active accounts (Accounts API, for the staff member) with enough available; the phone number on the slip must be the customer's mobile on record | A slip could name someone else's account, or a number that is not the customer's | Unauthorised debit; social engineering at the counter | `recordTransferSlip` in [digital-gold.service.ts](src/digital-gold/digital-gold.service.ts) |
| 5 | The scan: one file, at most 5 MB, PDF / PNG / JPEG, held in memory for the request only; Documents Management checks its content (magic bytes) and returns its SHA-256, which goes into the record; if the record is then refused, the scan is removed again | Large or disguised files; scans attached to nothing | Unrestricted upload (CWE-434); resource consumption | [digital-gold.controller.ts](src/digital-gold/digital-gold.controller.ts), `upload` / `removeDocument` in [domain-api.client.ts](src/services/domain-api.client.ts) |
| 6 | Every downstream call with its own exchanged token (RFC 8693, this API's exchange-only client): exactly one scope per API, this API added to the `act` chain; downstream answers translated (the domain's own 400 / 409 texts pass, 401 / 403 become 403, anything else 503) | The Journey API could act as itself, replay one API's token at another, or leak a Domain API's internals | Confused deputy; information disclosure (CWE-209) | [token-exchange.service.ts](src/services/token-exchange.service.ts), [domain-api.client.ts](src/services/domain-api.client.ts) |
| 7 | Fail-closed configuration: `PARTNER_PRODUCTS_JOURNEY_ENVIRONMENT` has no default, every downstream address must be https, audience and caller have no defaults | A forgotten setting would accept the wrong tokens or send bearer tokens in the clear | Misconfiguration (OWASP A05) | [journey-options.ts](src/configuration/journey-options.ts) |
| 8 | Request limits: overall (600 a minute, before any token check) and per person and calling client - 120 reads and 10 records a minute; 429 with `Retry-After`; per instance (Threat-Model R-14) | A stolen session or a runaway client could flood the API or the Domain APIs behind it | Unrestricted resource consumption (OWASP API4) | [rate-limit.ts](src/http/rate-limit.ts) |
| 9 | A JSON API for one server-side caller: `nosniff`, `default-src 'none'`, `frame-ancestors 'none'`, `no-store`; errors as Problem Details (RFC 9457) without internals | A browser could frame or cache its answers; stack traces could reach the caller | Clickjacking; information disclosure | [main.ts](src/main.ts), [problem-details.filter.ts](src/http/problem-details.filter.ts) |
| 10 | The purchase's payment is started AS THE STAFF MEMBER (token exchange for `payments.write`; Payments still needs their `payment.initiate` and branch, and applies its approval tier and four eyes); every payment detail comes from the Partner Products API (the slip and the settlement account), with the slip's fixed Idempotency-Key - nothing from the screen | A tampered request could pay another payee or amount; a retry could pay twice; a person without payment rights could pay through Digital Gold | Unauthorised payment; privilege escalation | `buy` in [digital-gold.service.ts](src/digital-gold/digital-gold.service.ts) |
| 11 | The workflow is started with the purchase number only (workflow ID `gold-purchase-<number>`: a second start is "already running") - no token or account number goes into Temporal's history | Tokens or account numbers stored in workflow history; two workflows following one purchase | Credential exposure; duplicate processing | [purchase-workflow.starter.ts](src/services/purchase-workflow.starter.ts) |
| 12 | Calls that go on to the Partner Bank (send Form VII, send a slip, a staff view) are waited for up to 40 s, so the Partner Products API's own answer ("not reachable - it stays SENT, send again") reaches the counter; a time-out here says the same | The counter told "unavailable" while a form may already be at the Partner Bank | Misleading state; duplicate sends | `postViaPartnerBank` in [domain-api.client.ts](src/services/domain-api.client.ts) |