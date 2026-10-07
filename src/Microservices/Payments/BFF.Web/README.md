# Payments BFF + MFE

The Payments micro-frontend (Next.js static export in `wwwroot`) and its Backend-for-Frontend (ASP.NET Core 10, Duende BFF). The browser holds a session cookie only; the staff member's tokens stay in this BFF's server-side session. Framed by the Shell; may be framed by nothing else (CSP `frame-ancestors`).

URL: `https://payments.dev.localhost:46388`. IDP client: `Payments.Microservice.BFF.ClientID` (scopes `payments.read`, `payments.write` and, read-only, `accounts.read`).

## Pages

| Page | Who | What |
|---|---|---|
| `/v1/payments/new` | Customer service agent (`payment.initiate`) | The assisted-channel payment: (1) find the customer and choose the paying account, with its **available** amount; (2) payee BSB (looked up in the BSB directory), account number and name, with **Confirmation of Payee**; (3) amount and reference; (4) review and **Transfer**. |
| `/v1/payments/view-details?paymentId=` | Staff of the branch | Status, outcome, the saga's state, and the **timeline** - re-read every 2 seconds while the payment is in progress. |
| `/v1/payments/view-all` | Staff of the branch | The branch's payments with status filters. |
| `/v1/payments/approvals/view-all` | Payments officer (`payment.approve`) | The approval queue: the branch's payments above the tier. The status page then shows **Your decision**: Approve and send, or Reject (remarks required). It explains up front when the officer started the payment (SoD) or the amount is above their clearance's limit; the API enforces both. |

The New payment form creates ONE Idempotency-Key when it opens: pressing Transfer twice, or trying again after an error, can never create a second payment. Typed details are protected by the Shell's unsaved-changes check. A Confirmation of Payee "close match" offers the name the bank holds; a "no match" needs an explicit confirmation that the details were checked with the customer (a common scam signal).

The screen explains limits early (available funds, the approval tier from `GET /v1/payments/policy`), but every rule is decided by the APIs: the Payments API validates the payment, and Accounts reserves the funds under the account's row lock.

## API facade (`/bff/api`, anti-forgery header on every POST)

| Route | Forwards to |
|---|---|
| `GET payments`, `GET payments/{id}` | Payments API `GET /v1/payments`, `/v1/payments/{id}` |
| `POST payments` (header `Idempotency-Key`) | Payments API `POST /v1/payments` - passed through unchanged |
| `GET payments/policy`, `GET payments/bsb/{bsb}`, `POST payments/payee-confirmations` | Payments API (the last two are answered by the payment network) |
| `POST payments/{id}/approve`, `POST payments/{id}/reject` | Payments API (the officer's decision) |
| `GET payer-accounts?search=` | Accounts API `GET /v1/accounts/for-payment` - the branch's ACTIVE accounts with their available amount (policy: `accounts.read` + `payment.initiate` + branch) |

Every call carries the staff member's own access token; GETs are retried, POSTs never (the screen resends with the same Idempotency-Key instead). `silent-login` accepts only the four pages above (the details page with exactly one numeric `paymentId`).

## Build

`CompileAndExportBFFClients_V3.ps1` (step 7) builds the MFE and copies it to `wwwroot`; restart this BFF afterwards (its CSP hashes are computed at start-up).