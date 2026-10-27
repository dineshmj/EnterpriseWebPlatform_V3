# BIAN compatibility

Which EWP domain APIs also speak **BIAN** - the Banking Industry Architecture Network's standard
service-domain contracts - and how.

In short: **where an EWP API maps to a BIAN service domain with confidence, it offers BIAN's
contract as `/v2` next to its own `/v1`, on the same business rules; EWP's own callers keep
`/v1`. Every `/v2` body is checked in CI against the file BIAN publishes. Where BIAN has no honest
fit, the API stays `/v1` only, and the reason is written down below.**

## The approach

The customer's way of adopting BIAN, in small:

1. **Map** each domain API to the closest BIAN service domain - by BIAN's own definition and
   operations, not by name.
2. **Rate** the mapping: confident -> a `/v2`; plausible or no fit -> no `/v2` (a bank would
   propose a new service domain to BIAN, or keep a BIAN-like interface of its own).
3. **Translate at the edge.** BIAN's schemas are general (every property optional, deep
   nesting, amounts and identifiers as objects); a mapper per API turns them into the existing
   commands and queries (an anti-corruption layer). The domain, the sagas and the tables do not
   change. EWP requires what its business needs and names each missing BIAN property.
4. **Prove it** with BIAN's own file: the tests validate every body `/v2` sends or accepts
   against BIAN's published OpenAPI schema, and a test with an invalid value shows the check can
   fail.

## The release used

BIAN semantic APIs **release 14.0.0**, from
[github.com/bian-official/public](https://github.com/bian-official/public) at commit
`b58bf4c2c300dd018afc084e9bb24b7adaaa3500` (12-FEB-2026). The files used are copied unchanged
into [release14.0.0](release14.0.0) (Apache License 2.0, [LICENSE](LICENSE));
[.gitattributes](../../.gitattributes) keeps them byte for byte.

Release 14 replaced the earlier *Payment Order*, *Payment Initiation* and *Payment Execution*
service domains (still in release 13) with **PaymentOrderInitiation** and **PaymentRail**. A bank
on an older release would name the payments contract differently.

## The mapping (27-OCT-2026)

| EWP API | BIAN service domain | Decision |
|---|---|---|
| Payments (start, read, approve or reject a payment) | **PaymentOrderInitiation** - "captures the payer and payee details and other key properties of the payment and initiates the orchestration of the transaction" | **`/v2` built** (below) |
| Accounts (accounts, cash deposit, holds for payments) | **CurrentAccount** - "a consumer checking/demand deposit account … payments and deposits, … liens" | Confident: Retrieve, Deposit, AmountBlock - next |
| Customer Onboarding (customer records) | **PartyReferenceDataDirectory** - "party reference information … contacts … demographic information" | Confident: Register, Retrieve, Update - next |
| Documents Management | **DocumentDirectory** - "directory based categorization and storage … for documents" | Confident: Register, Retrieve - next |
| Accounts (account-opening applications) | CustomerOffer | Plausible only (BIAN's offer covers credit, collateral, underwriting) - `/v1` only |
| Customer Onboarding (onboarding applications) | PartyLifecycleManagement | Plausible only - `/v1` only |
| KYC (identity and document verification) | PartyLifecycleManagement (IdentityProofing, Documentation) | Would merge two bounded contexts into one BIAN domain; IdentityProofing offers Retrieve only - `/v1` only |
| Compliance (screening cases) | - | No fit: BIAN has no KYC or sanctions-screening service domain in any release; RegulatoryCompliance defines compliance tests, it does not screen |
| Notifications (staff in-app) | - | No fit: Correspondence produces letters and mail-shots |
| Audit trail | - | No fit |
| Partner Products (Digital Gold, as distributor) | - | No fit: BrokeredProductProxy has no operations in release 14; ProductServiceAgency covers agency contracts |
| Operations, Outbox endpoints | - | Technical, not banking services |

## Payments: PaymentOrderInitiation

| BIAN operation | EWP | Same rules as |
|---|---|---|
| `POST /v2/PaymentOrderInitiation/Initiate` | start a payment (202 + `Location`; `Idempotency-Key` header) | `POST /v1/payments` |
| `GET /v2/PaymentOrderInitiation/{id}/Retrieve` | the payment | `GET /v1/payments/{id}` |
| `PUT /v2/PaymentOrderInitiation/{id}/OrderInitiation/approval/Exchange` | approve (`Task` "Approve") or reject ("Reject") | `/approve`, `/reject` (step-up) |

Where EWP's payment sits in BIAN's `PaymentOrderInitiationTransaction`:

| EWP | BIAN property |
|---|---|
| customer number (the payer) | `PayerReference.PayerIdentifier` |
| paying BSB / account | `PayerBankReference.PayerBankIdentifier` / `PayerProductInstanceReference.ProductAgreementIdentification.IdentifierValue` |
| payee name | `PayeeReference.PayeeIdentifier` - BIAN's payee has no name property; a domestic payee is identified by name, BSB and account |
| payee BSB / account | `PayeeBankReference.PayeeBankIdentifier` / `PayeeProductInstanceReference.ProductAgreementIdentification.IdentifierValue` |
| amount (AUD only) | `Amount.AmountValue` (e.g. `"1100.00"`), `Amount.AmountCurrency.Currencycode` |
| reference | `PaymentPurpose` ("details captured for reference") |
| payment number | `PaymentTransaction.PaymentTransactionIdentification` (`PaymentOrderID`) |
| status | `PaymentTransaction.PaymentTransactionStatus`: BIAN's type, EWP's own code in `StatusReason` |
| (fixed) | `PaymentMechanism` "ACH" (BECS Direct Entry), `PaymentTransactionType` "FinancialTransaction", `DateType` "InitiatedDate" |

EWP's statuses in BIAN's status types (BIAN's list is coarser; EWP's code always travels with it):

| EWP | BIAN |
|---|---|
| INITIATED | Received |
| RESERVING_FUNDS | Acceptedtechnicalvalidation |
| PENDING_APPROVAL, COMPENSATING, COMPENSATION_FAILED | Pending |
| SENDING_TO_NETWORK, SETTLING_FUNDS | Acceptedsettlementinprocess |
| COMPLETED | Acceptedsettlementcompleted |
| REJECTED, FAILED | Rejected |

Code: [Bian/](../../src/Microservices/Payments/API/Bian),
[PaymentOrderInitiationController.cs](../../src/Microservices/Payments/API/Controllers/PaymentOrderInitiationController.cs).

## Checking it

- **CI, no running service:** `tst/dotnet/Payments.Api.Tests/Bian` - every `/v2` body (each EWP
  status, a partner's request, both decisions) validated against BIAN's file; the mapping rules
  (required properties, currency, amount format, the decision task); a body with a value BIAN
  does not list - at the top and three references deep - must fail the check.
- **Live, as a demo:** `tst/playwright/tests/20-functional/bian/payment-order-initiation.spec.ts`
  - Sophie initiates through BIAN; `/v1` and `/v2` show the same payment; the same
  Idempotency-Key through `/v1` finds it; Sophie may not approve through BIAN's Exchange (403);
  Emily approves through it and the payment completes.