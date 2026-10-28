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
5. **Switch it without a redeploy.** All four `/v2` controllers sit behind the release toggle
   `bian-v2-endpoints` (default on; [feature-flags/README.md](../../feature-flags/README.md)):
   off, every `/v2` request answers 404 before authentication, as if it did not exist; `/v1` is
   never touched.

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
| Accounts (accounts, cash deposit, holds for payments) | **CurrentAccount** - "a consumer checking/demand deposit account … payments and deposits, … liens" | **`/v2` built**: Retrieve, Deposit (below). AmountBlock not offered: the holds are the payment saga's internal, machine-only commands |
| Customer Onboarding (customer records) | **PartyReferenceDataDirectory** - "party reference information … contacts … demographic information" | **`/v2` built**: Retrieve, Reference Retrieve and Update (below). Register not offered: BIAN's directory entry has no contact details, and EWP registers no customer without them |
| Documents Management | **DocumentDirectory** - "directory based categorization and storage … for documents" | **`/v2` built**, read-only: Retrieve, DocumentReferenceProperties Retrieve (below). Register and Update not offered: BIAN's directory entry carries no file |
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

## Accounts: CurrentAccount

| BIAN operation | EWP | Same rules as |
|---|---|---|
| `GET /v2/CurrentAccount/{id}/Retrieve` | the account (an account officer, own branch) | `GET /v1/accounts/{id}` |
| `POST /v2/CurrentAccount/{id}/Deposit/Initiate` | cash paid in at the counter (201; `Idempotency-Key` header) | `POST /v1/accounts/cash-deposits` |

Not offered: **AmountBlock** (place, settle, release a hold). In EWP those are the payment
saga's internal commands - one pinned machine client, an Inbox, replies through the Outbox. A
second HTTP door into moving held money would add risk and show nothing new; the holds are
visible through Retrieve (current balance - available balance). And EWP's "settle" is a debit,
not a change to the block, so it would not map to `AmountBlock/Update` honestly anyway.

Where EWP's account sits in BIAN's `CurrentAccountFacility`:

| EWP | BIAN property |
|---|---|
| account number | `CurrentAccountNumber` (`AccountIdentificationType` "AccountNumber"), also in `AccountDetails.AccountIdentification` |
| BSB | `BankBranchLocationReference.BranchIdentification` - a BSB identifies a bank branch |
| owner: customer number, holder name | `AssociationType` "PartyIsPrimaryOwnerOfAccount", `AssociationReference` (`PartyIdentification`, `PartyName`). BIAN's own `CustomerReference` is typed as an "ItemInventory" with a single text property - not used |
| core-banking reference, product | `ProductInstanceReference` (`ProductAgreementIdentification`, `AgreementDescription`) |
| balance (funds on hold included) | `AccountBalance`, `BalanceType` "CurrentBalance" |
| available balance | `AccountDetails.AccountBalance`, `BalanceType` "AvailableBalance". BIAN gives each level ONE balance; the amount on hold is current - available |
| status | `AccountStatus`: ACTIVE -> "Enabled", FROZEN and CLOSED -> "Disabled" (BIAN: "cannot be used, temporarily or permanently"; it has no "Closed"); EWP's code in `AccountDetails.AccountStatus.AccountStatus.StatusReason` |
| opened at | `AccountDateType` "OpeningDate", `AccountDate` |

A cash deposit in BIAN's `Deposit`:

| EWP | BIAN property |
|---|---|
| the depositing customer (must own the account) | `DepositTransactionSourceReference` - required by EWP |
| amount (AUD, positive) | `DepositTransactionAmount` |
| cash, nothing else | `DepositType` "CashDeposit" (absent = cash; any other type refused) |
| deposit number, balance after (answer) | `DepositTransaction.TransactionIdentification`, `DepositTransaction.FinancialTransactionTargetAccount.AccountBalance` |

Code: [Bian/](../../src/Microservices/Accounts/API/Bian),
[CurrentAccountController.cs](../../src/Microservices/Accounts/API/Controllers/CurrentAccountController.cs).

## Customers: PartyReferenceDataDirectory

| BIAN operation | EWP | Same rules as |
|---|---|---|
| `GET /v2/PartyReferenceDataDirectory/{id}/Retrieve` | the customer's directory entry (in the caller's scope) | `GET /v1/customers/{id}` |
| `GET /v2/PartyReferenceDataDirectory/{id}/Reference/primary/Retrieve` | legal name, e-mail, phone, residential address | `GET /v1/customers/{id}` |
| `PUT /v2/PartyReferenceDataDirectory/{id}/Reference/primary/Update` | a new e-mail and / or phone (the managing agent; `If-Match` required) | `PUT /v1/customers/{id}` |

The rating was "confident" before BIAN's file was read field by field (27-OCT-2026); two gaps
narrowed what `/v2` offers:

- **Register is not offered.** Its body, the directory entry, holds a name, a party type, an
  identifier and a date - no e-mail, phone or address. EWP registers no customer without them,
  and BIAN's Reference (where they live) has no Register of its own. Customers are registered
  through `/v1`.
- **The name cannot change through BIAN.** BIAN has one name string (`PartyName`,
  `PartyFullLegalName`); EWP keeps a first and a last name, and splitting "Mary Anne Smith" would
  be a guess. Update changes the contact details; the name, the address and the dates may be sent
  back unchanged (read, modify, write) but a change to them is refused and named. `/v1` changes
  no address either.

EWP keeps one reference record per customer, addressed as `primary`. `/v1`'s `ExpectedVersion`
becomes HTTP's own check: each answer carries the customer's version as its `ETag`, and Update
needs it in `If-Match` (428 without it, 412 when the customer changed meanwhile). A property BIAN
defines but EWP does not keep (nationality, date of birth, political exposure, …) is refused, not
silently dropped.

Where EWP's customer sits in BIAN's `PartyReferenceDataDirectoryEntry` and `Reference`:

| EWP | BIAN property |
|---|---|
| first and last name | `PartyReference.PartyName.Name`, `Reference.PartyFullLegalName.Name` ("first last") |
| customer number | `PartyReference.PartyIdentification.PartyIdentification.IdentifierValue` (BIAN's identification types are government documents only - no type is set) |
| Individual / Business | `PartyType` "Person" / "Organisation"; an individual's legal structure "Individual" (a business has none: EWP does not record partnership, company, …) |
| registered at | `DirectoryEntryDateType` "OpenDate", `DirectoryEntryDate`; `Reference.CustomerSinceDate` |
| e-mail | `Reference.eMailAddress`: `ContactPointType` "ElectronicAddress", the address in `LocationReference.LocationAddress` |
| phone | `Reference.CellPhoneNumber`: `ContactPointType` "PhoneNumber" (EWP keeps one phone number, mobile or not) |
| residential address | `Reference.ResidentialAddress` (`AddressType` "Residential"): lines 1 and 2 in the one free-format `AddressLine`, `PostCode`, `TownName`, `Country.CountryCode`; the state's name in `AddressDescription` - BIAN's `CountrySubDivision` is the kind of area (State, Province, …), not its name |
| status (PROSPECT … CLOSED) | not here: a party's lifecycle belongs to BIAN's PartyLifecycleManagement |

Code: [Bian/](../../src/Microservices/CustomerOnboarding/API/API/Bian),
[PartyReferenceDataDirectoryController.cs](../../src/Microservices/CustomerOnboarding/API/API/Controllers/PartyReferenceDataDirectoryController.cs).

## Documents: DocumentDirectory

| BIAN operation | EWP | Same rules as |
|---|---|---|
| `GET /v2/DocumentDirectory/{id}/Retrieve` | the document's directory entry (own branch) | `GET /v1/documents/{id}` |
| `GET /v2/DocumentDirectory/{id}/DocumentReferenceProperties/primary/Retrieve` | the document's version and type | `GET /v1/documents/{id}` |

The rating was "confident: Register, Retrieve" before BIAN's file was read field by field
(28-OCT-2026). BIAN's DocumentDirectory is a directory of POINTERS to documents: the entry holds
an identification, a date type (`OpenDate` / `RefreshDate`), a date and a status - and no
operation in the file takes or returns a file (no binary, no multipart, no file name, content
type, size or hash). BIAN's `Document` schema is defined but reached by no operation. So:

- **Register is not offered.** EWP stores no document without its content - the file's
  signature decides its type, the content is hashed and encrypted at rest. A Register without the
  file would create an entry pointing at nothing; a multipart Register would not be BIAN's
  contract. Documents are uploaded through `/v1`.
- **Update is not offered.** A document's content never changes, and its status (attached as
  evidence, invalidated) changes only through the saga's internal steps - never by a caller.
- **The content stays on `/v1`** (`GET /v1/documents/{id}/content`).

EWP keeps one reference-properties record per document, addressed as `primary`.

Where EWP's document sits in BIAN's `DocumentDirectoryEntry` and `DocumentReferenceProperties`:

| EWP | BIAN property |
|---|---|
| document ID | `DocumentDirectoryEntryIdentification` (also in `DocumentReferenceProperties.DocumentDirectoryEntryReference`) |
| uploaded at | `DirectoryEntryDateType` "OpenDate", `DirectoryEntryDate.DateContent` (the UTC date) |
| status AVAILABLE / ATTACHED / INVALIDATED | `DirectoryEntryStatus.StatusReason` - BIAN's `Status` has no status value, so the reason starts with EWP's code: `AVAILABLE`, `ATTACHED: evidence of <application>`, `INVALIDATED: <reason>` |
| when that status began | `DirectoryEntryStatus.StatusDateTime` (uploaded, attached or invalidated at) |
| version | `DocumentReferenceProperties.VersionNumber` |
| document type (e.g. `KYCProof`) | `DocumentReferencePropertiesType` (absent when the upload named none) |
| file name, content type, size, SHA-256, content | not in BIAN's DocumentDirectory - `/v1` only |
| a service performed on the document | BIAN's `DocumentService` (provider, schedule, outcome): EWP records none - left out |

Code: [Bian/](../../src/Microservices/DocumentsManagement/API/API/Bian),
[DocumentDirectoryController.cs](../../src/Microservices/DocumentsManagement/API/API/Controllers/DocumentDirectoryController.cs).

## Checking it

- **CI, no running service:** `tst/dotnet/<API>.Tests/Bian` - every `/v2` body validated against
  BIAN's file (Payments: each EWP status, a partner's request, both decisions; Accounts: the
  account in each status, a deposit request, the recorded deposit; Customers: a person's and a
  business's entry, the reference with and without an address, a contact change; Documents: the
  entry in each status, the reference properties with and without a type); the mapping rules
  (required properties, currency, amount format, the decision task, cash only, only the contacts
  change, unknown BIAN properties refused); and a body with a value BIAN does not list must fail
  the check (Payments: at the top and three references deep; Accounts, Customers and Documents:
  two deep). The schema check is one shared helper,
  [tst/dotnet/Shared/BianSchema.cs](../../tst/dotnet/Shared/BianSchema.cs) (JSON Schema Draft-07
  rules, as OpenAPI 3.0 uses), linked into each test project.
- **Live, as a demo:** `tst/playwright/tests/20-functional/bian/`
  - `payment-order-initiation.spec.ts`: Sophie initiates through BIAN; `/v1` and `/v2` show the
    same payment; the same Idempotency-Key through `/v1` finds it; Sophie may not approve through
    BIAN's Exchange (403); Emily approves through it and the payment completes.
  - `current-account.spec.ts`: Jack retrieves the account through BIAN and `/v1` shows the same
    (both balances); Sophie deposits cash through BIAN; the same Idempotency-Key through `/v2` and
    `/v1` finds that deposit; Jack may not deposit (403); the balance moved once.
  - `party-reference-data-directory.spec.ts`: Sophie registers a customer through `/v1`; BIAN's
    Retrieve and RetrieveReference show the same; Mia (another branch) finds nothing (404); an
    Update without `If-Match` is refused (428); with the ETag it changes the e-mail and `/v1` sees
    it; the old ETag again is refused (412); a name change through BIAN is refused (400).
  - `document-directory.spec.ts`: Sophie (Sydney) and Mia (Melbourne) upload through `/v1`;
    Liam, a Sydney KYC officer, sees Sophie's document through BIAN's Retrieve and
    DocumentReferenceProperties as `/v1` shows it; Mia's does not exist for him (404); both
    uploads are removed again.
  - `bian-release-toggle.spec.ts`: no sign-in - each API's `/v2` is 401 (exists) with the toggle
    on, 404 with no body with it off, and 401 again once it is back; `/v1` stays 401 throughout.