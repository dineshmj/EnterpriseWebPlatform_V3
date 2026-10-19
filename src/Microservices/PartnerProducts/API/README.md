# Partner Products API

EWP's side of the Partner Bank's **Digital Gold**, with EWP as the Partner Bank's **distributor** (Partner Bank repository, `doc/Architecture.md` D14). The customer signs paper forms at EWP's counter:

- **Form VII**, once: the Partner Bank's Digital Gold application -> `gold_account_applications` (RECORDED -> SENT -> OPENED / REFUSED);
- a **transfer authorization slip**, for each purchase: EWP's authority to debit the customer and buy at that day's price -> `gold_purchases` (RECORDED -> SENT -> ORDERED -> PAID / ALLOCATED, or REFUSED / CANCELLED).

The Partner Bank is the system of record of the account and its gold. EWP keeps the evidence of what the customer signed (the scan in Documents Management and its SHA-256), what it sent (the signed instruction's ID) and what the Partner Bank answered.

This API is also EWP's **adapter to the Partner Bank** - an external system, like the payment network for the Payments API (Threat-Model TB6): it holds EWP's keys and is the only EWP component that calls the Partner Bank.

```
Partner Products API  --mutual TLS (EWP's client certificate)-->  Partner Bank token endpoint (:47411)
                         client_credentials, scope partner.distribution  -->  EWP's own token, BOUND to that certificate
                      --mutual TLS + token-->  Partner Bank API (:47403)
                         POST /api/distribution/gold-accounts   body: SIGNED instruction (application/jwt, ES256)
                         GET  /api/distribution/gold-accounts/{number}   the holding (grams, value, activity)
```

| | |
|---|---|
| Address | https://partner-products-api.dev.localhost:46387 |
| Database | `EwpPartnerProductsDb` ([EwpPartnerProductsDb.sql](PartnerProductsDb/EwpPartnerProductsDb.sql)), user `ewp_partner_products_api` (SELECT, INSERT, UPDATE - no DELETE) |
| Accepts | Delegated tokens through `PartnerProducts.JourneyApi.ClientID` (audience `partner-products-api`); on `/internal` only the Partner Products Worker's machine token (`PartnerProducts.Worker.M2M.ClientID`) |

## Endpoints

| Endpoint | Policy | What |
|---|---|---|
| `GET /v1/partner-products/customers/{customerNumber}/digital-gold` | `DigitalGoldView` | The customer's application (latest) and purchases - in the caller's branch |
| `GET /v1/partner-products/gold-account-applications` | `DigitalGoldView` | The branch's latest Form VII applications, those still to be sent first |
| `POST /v1/partner-products/gold-account-applications` | `DigitalGoldOrder` | Record a signed Form VII |
| `POST /v1/partner-products/gold-account-applications/{number}/send` | `DigitalGoldOrder` | Send Form VII to the Partner Bank: OPENED (its account number) or REFUSED (its reason); no answer leaves it SENT - send again |
| `GET /v1/partner-products/gold-account-applications/{number}/holding` | `DigitalGoldView` | The open account's holding, read from the Partner Bank |
| `POST /v1/partner-products/gold-account-applications/{number}/partner-view` | `DigitalGoldView` | "View at the Partner Bank": pushes a signed staff view (PAR-style) and returns the configured staff-view page + the one-time `request_uri` (60 s) |
| `POST /v1/partner-products/gold-purchases` | `DigitalGoldOrder` | Record a signed transfer authorization slip (needs an OPEN account); its payment's Idempotency-Key is fixed here |
| `POST /v1/partner-products/gold-purchases/{number}/send` | `DigitalGoldOrder` | Send the slip to the Partner Bank as a signed `digital_gold_purchase`: ORDERED (its order number and quote) or REFUSED; no answer leaves it SENT - send again |
| `GET /v1/partner-products/gold-purchases/{number}/payment-instruction` | `DigitalGoldOrder` | What EWP Payments is asked to pay for an ORDERED purchase: the slip's account and amount, the Partner Bank's settlement account (configuration), the order number as reference, the fixed Idempotency-Key |
| `POST /v1/partner-products/gold-purchases/{number}/payment` | `DigitalGoldOrder` | Record the payment number EWP Payments gave the purchase's payment |
| `GET /internal/v1/gold-purchases/{number}` | `DigitalGoldWorker` | The purchase's state for the purchase workflow (status, amount, payment key, order number) |
| `POST /internal/v1/gold-purchases/{number}/payment-advice` | `DigitalGoldWorker` | The payment completed: PAID (checked: key, amount, payee), then the payment advice to the Partner Bank -> ALLOCATED |
| `POST /internal/v1/gold-purchases/{number}/cancel` | `DigitalGoldWorker` | The payment was not made: cancel the Partner Bank's unpaid order -> CANCELLED |

## Security controls

| # | Security control | If it were missing | Threat prevented | Where to look |
|---|---|---|---|---|
| 1 | JWT access tokens only (issuer, audience, signature, lifetime, `typ: at+jwt`); deny by default | Any token from the IDP, or none, could reach the data | Token confusion; anonymous access | [Program.cs](Program.cs) |
| 2 | Delegated tokens through the Partner Products Journey API only (`act`), with the action's scope, the person's permission (`partner.gold.view` / `partner.gold.order`) and a branch | A machine, or another client holding the scope, could record a form that opens an account at another bank | Broken function-level authorization (OWASP API5) | [DigitalGoldPolicies.cs](Authorization/DigitalGoldPolicies.cs) |
| 3 | The actor (subject, LAN ID, branch) comes from the token, never the body; every list is limited to the actor's branch (another branch's customer looks like one without Digital Gold) | A caller could record in someone else's name, or browse other branches' customers | Repudiation; broken object-level authorization (OWASP API1) | `ActorOf`, `ForCustomer` in [DigitalGoldController.cs](Controllers/DigitalGoldController.cs) |
| 4 | Form VII: only a KYC-verified customer, an adult, an Australian mobile on record, signed within 30 days, the scan's SHA-256, the date of birth checked against photo ID and the signature checked - what the Partner Bank would refuse is refused here first | An application the Partner Bank must refuse - or one for an unidentified person - would be recorded | Weak identification; invalid instructions | [GoldAccountApplication.cs](Domain/GoldAccountApplication.cs), [DigitalGoldRules.cs](Domain/DigitalGoldRules.cs) |
| 5 | A slip: only for the customer's OPEN account, signed today (Sydney), the phone number on it = the mobile on record, AUD in cents and at most 50,000, the signature checked | A stale or altered slip could debit the customer | Unauthorised debit | [GoldPurchase.cs](Domain/GoldPurchase.cs) |
| 6 | Once only, enforced by the database: one active application per customer, one per Form VII, one purchase per slip (unique indexes; a race or a double click is a 409) | A double click or two counters at once could open two applications or buy twice on one slip | Duplicate processing | [EwpPartnerProductsDb.sql](PartnerProductsDb/EwpPartnerProductsDb.sql), `SaveOnceAsync` in [DigitalGoldController.cs](Controllers/DigitalGoldController.cs) |
| 7 | No DELETE for the API's database user: the signed forms and the Partner Bank's answers are evidence | A compromised API or an injection could erase what a customer signed | Evidence tampering | `db\EwpServiceDbUsers.sql` |
| 8 | The mobile number leaves the API masked (last three digits) | The full number would reach every screen and log | Personal data exposure | `View` in [DigitalGoldController.cs](Controllers/DigitalGoldController.cs) |
| 9 | Errors as Problem Details with a traceId only; the domain's refusals carry their own (counter-ready) text | Stack traces or SQL would reach the caller | Information disclosure (CWE-209) | [ApiExceptionHandler.cs](Controllers/ApiExceptionHandler.cs) |
| 10 | Per-caller rate limits, host filtering, HSTS outside Development | Flooding; host-header poisoning | OWASP API4; A05 | [Program.cs](Program.cs), `appsettings.Development.json` |
| 11 | The Partner Bank over **mutual TLS**: EWP's client certificate; the server believed only if it chains to the PoC's own root (not the Windows store) and its name matches; no redirects; a timeout; both addresses checked at start-up (outside Development: https, permitted hosts, no internal address) | An impostor could receive the customer's data, or a redirect could move the call elsewhere | Spoofing; SSRF by configuration (CWE-918) | [PartnerBankClient.cs](PartnerBank/PartnerBankClient.cs), [PartnerBankCredentials.cs](PartnerBank/PartnerBankCredentials.cs), [Program.cs](Program.cs) |
| 12 | **EWP's own token** at the Partner Bank (client credentials over mutual TLS, scope `partner.distribution`), bound by the Partner Bank to EWP's certificate - useless to anyone without EWP's key | A stolen token could be replayed against the Partner Bank | Token theft and replay (RFC 8705) | `TokenAsync` in [PartnerBankClient.cs](PartnerBank/PartnerBankClient.cs) |
| 13 | **Every instruction signed** with EWP's request-signing key (ES256, the certificate's thumbprint as `kid`, `typ partner-instruction+jwt`, audience `partner-api`, 5 minutes) - the Partner Bank keeps signed evidence of exactly what EWP instructed | EWP could later deny an instruction, or a changed one could pass unnoticed | Repudiation; tampering | `Sign` in [PartnerBankClient.cs](PartnerBank/PartnerBankClient.cs) |
| 14 | **Once only across a lost answer**: SENT and the instruction ID are saved BEFORE the call; the ID (the signed `jti`) never changes, so "send again" is the same instruction and the Partner Bank opens one account; answers are final only when they are answers (201/200 opened; 400/404/409 refused) - anything else leaves it SENT | A timeout could open two accounts, or record a refusal that never happened | Duplicate processing; inconsistent state | `PrepareToSend` in [GoldAccountApplication.cs](Domain/GoldAccountApplication.cs), `SendFormVii` in [DigitalGoldController.cs](Controllers/DigitalGoldController.cs) |
| 15 | EWP's keys outside the repository (`%USERPROFILE%\.ewp-pki`), each with its own password file, loaded once at start-up (a missing file stops the API); the signing key in memory only; the instruction carries exactly the fields the Partner Bank's allow-list accepts | Keys in source control; a half-configured API calling the Partner Bank | Secret exposure (CWE-798); misconfiguration | [PartnerBankCredentials.cs](PartnerBank/PartnerBankCredentials.cs), `OpeningInstruction` in [GoldAccountApplication.cs](Domain/GoldAccountApplication.cs) |

| 16 | The purchase workflow's endpoints (`/internal`) accept ONLY the Partner Products Worker's machine token: its client ID pinned, no person (`sub`, `act`) in it; the staff policies never accept it | Another client holding the write scope could allocate or cancel purchases; a person's token could reach the machine-only door | Broken function-level authorization (OWASP API5) | `Worker` in [DigitalGoldPolicies.cs](Authorization/DigitalGoldPolicies.cs) |
| 17 | The purchase pays only the Partner Bank: the payee is the settlement account from configuration (checked at start-up), never typed; amount and paying account are the slip's; the reference is the Partner Bank's order number; the payment's Idempotency-Key is fixed when the slip is recorded (one payment per slip, unique in the database) | A tampered request could pay someone else, or another amount; a retry could pay twice | Unauthorised payment; duplicate payment | `PaymentInstruction` in [GoldPurchase.cs](Domain/GoldPurchase.cs), `PartnerBank:Settlement` in [appsettings.json](appsettings.json), [EwpPartnerProductsDb.sql](PartnerProductsDb/EwpPartnerProductsDb.sql) |
| 18 | PAID only for THIS purchase's payment (key, amount and payee checked again), saved BEFORE the payment advice; the advice is sent again until answered and allocates once; CANCELLED only while unpaid - a paid purchase is never cancelled; a refused advice or cancellation is kept as the reason for a person | A wrong or stale payment could allocate gold; a crash could leave paid money without gold, or cancel an order that was paid | Inconsistent state; financial loss | `MarkPaid`, `EnsureCancellable` in [GoldPurchase.cs](Domain/GoldPurchase.cs), [InternalGoldPurchasesController.cs](Controllers/InternalGoldPurchasesController.cs) |
| 19 | A slip is sent only on the day it was signed (Sydney): it buys at that day's price | A slip from last week could buy at today's price without the customer | Unauthorised purchase | `PrepareToSend` in [GoldPurchase.cs](Domain/GoldPurchase.cs) |

| 20 | A refused Form VII is linked to an account the Partner Bank already holds only when the Partner Bank says EWP introduced it, for the same customer number AND holder's name, and no other EWP application uses it; the reason says it was linked | EWP could take over another person's account, or two records could point at one account | Account takeover; inconsistent state | `ExistingAccountAsync` in [DigitalGoldController.cs](Controllers/DigitalGoldController.cs), `LinkExisting` in [GoldAccountApplication.cs](Domain/GoldAccountApplication.cs) |
| 21 | "View at the Partner Bank" is a PAR-style handoff: a NEW signed `staff_account_view` per click (the open account, its customer, the staff member LOOKING - from their token), pushed over mutual TLS; only the one-time `request_uri` comes back, its format checked, and the browser gets the CONFIGURED staff-view page (`PartnerBank:StaffViewUrl`, checked at start-up) plus that reference - nothing else from the answer, no account data in the address | A reusable or guessable link to a customer's account; an address from the Partner Bank's answer sending the browser elsewhere; a view in someone else's name | Broken access control (OWASP A01); open redirect (CWE-601); repudiation | `PartnerView` in [DigitalGoldController.cs](Controllers/DigitalGoldController.cs), `StaffViewInstruction` in [GoldAccountApplication.cs](Domain/GoldAccountApplication.cs) |
| 22 | The Partner Bank unreachable fails within 5 s (connect timeout), so the counter gets "not reachable - it stays SENT, send again" instead of a caller's time-out | A generic "unavailable" that hides whether anything was sent | Misleading state at the counter | `ConnectTimeout` in [Program.cs](Program.cs) |

## Not yet (next steps)

- Events through an Outbox to the audit trail (recorded, sent, opened, refused, ordered, paid, allocated, cancelled).