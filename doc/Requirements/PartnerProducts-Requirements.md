# Partner Products — Bounded Context Requirements

**Bounded context:** Partner Products (EWP as the Partner Bank's distributor)  
**Subdomain type:** Supporting (distribution of another bank's product)  
**Status:** Present (2026-10-19): Digital Gold at the assisted-channel counter. Form VII opens the customer's account at the Partner Bank (steps 6-7); a transfer authorization slip buys gold, paid through EWP Payments and followed by a Temporal workflow to ALLOCATED or CANCELLED (step 8); "View at the Partner Bank" opens the Partner Bank's own read-only page (a PAR-style handoff). Not yet: Outbox / audit events, the date of birth on EWP's customer record (Threat-Model R-15).

Platform-wide rules are not repeated here. See [doc/](..). The Partner Bank is a separate system in its own repository (`EnterpriseWebPlatform_V3_PartnerBank`), running on another machine; its decisions D1-D15 are in that repository's `doc/Architecture.md`. The conversations [Partner-Bank-mTLS-PAR-And-Signed-Instructions.md](../Arch-Design-Visualizations/Partner-Bank-mTLS-PAR-And-Signed-Instructions.md) and [mTLS-Handshake.md](../Arch-Design-Visualizations/mTLS-Handshake.md) explain the integration step by step.

---

## 1. Purpose and Boundary

A customer buys Digital Gold from the **Partner Bank** at EWP's branch counter. EWP acts as the Partner Bank's **distributor**: the customer signs paper forms at the counter and never signs in at the Partner Bank there; the staff member records the forms with their scans; EWP instructs the Partner Bank as an organisation (mutual TLS, its own certificate-bound token, a signed instruction); EWP pays for the purchase from the customer's EWP account. The Partner Bank is the **system of record** of the account and its gold.

| Owns | Does not own |
|---|---|
| EWP's record of each signed Form VII (`gold_account_applications`) and transfer authorization slip (`gold_purchases`): what the customer signed, the scan's SHA-256, who recorded and sent it, what the Partner Bank answered | The Digital Gold account, its grams, prices, orders and SMS notices (the Partner Bank) |
| The once-only instruction IDs and the purchase's payment key | The customer (Customer Onboarding), the paying account (Accounts), the scans (Documents Management) |
| The adapter to the Partner Bank: EWP's client certificate, request-signing key and token | The payment and its saga (Payments) |
| The purchase workflow's follow-up (payment advice or cancellation) | Temporal itself (an external runtime) |

### Deployable components

| Component | Location | Technology | Status |
|---|---|---|---|
| Partner Products web | [BFF.Web](../../src/Microservices/PartnerProducts/BFF.Web/README.md) — `https://ewp-partner.dev.localhost:46384`, page `/v1/partner-products/digital-gold/view-all` | Next.js SPA + light BFF | Present |
| Partner Products Journey API | [JourneyApi](../../src/Microservices/PartnerProducts/JourneyApi/README.md) — `https://partner-products-journey.dev.localhost:46385` | NestJS | Present |
| Partner Products API (domain + Partner Bank adapter) | [API](../../src/Microservices/PartnerProducts/API/README.md) — `https://partner-products-api.dev.localhost:46387` | ASP.NET Core 10, EF Core, PostgreSQL | Present |
| Partner Products Worker | [Worker](../../src/Microservices/PartnerProducts/Worker/README.md) — health `https://localhost:46383/health` | Node.js, Temporal TypeScript SDK | Present |
| Database | `EwpPartnerProductsDb` | PostgreSQL | Present |
| Temporal | development server `localhost:7233`, UI `http://localhost:8233` (`ps\temporal\Start-Temporal.ps1`) | Temporal CLI | Present (PoC) |

---

## 2. Personas in This Context

| Persona | May | Must not |
|---|---|---|
| Customer Service Agent (Sophie) | Find a customer of her branch; record Form VII and transfer slips with their scans; send them to the Partner Bank; pay for a purchase (her own `payment.initiate`); view the holding and the Partner Bank's own page | Type the customer's name or mobile (they come from EWP's record); act for another branch's customer; choose the payee |
| Payments Officer (Emily) | Approve a purchase's payment above the approval threshold, as for any payment (Payments) | Anything in Partner Products |
| Customer | Sign Form VII and the slips at the counter; receive the Partner Bank's SMS notices; use the Partner Bank's own channel ("My Partner Bank") | Sign in at the Partner Bank on the counter's computer |
| Partner Products Worker (machine) | Read a purchase's payment outcome; send the payment advice or the cancellation | Act for a person; reach any staff endpoint |

---

## 3. Permissions

| Permission | Customer Service Agent |
|---|:-:|
| `partner.gold.view` | ✓ |
| `partner.gold.order` | ✓ |
| `payment.initiate` (Payments, for the purchase's payment) | ✓ |

API scopes: `partner-products.read`, `partner-products.write` (Domain API); `partner-products-journey.read`, `partner-products-journey.write` (Journey API). Clients: `PartnerProducts.Microservice.Web.ClientID` (web BFF), `PartnerProducts.JourneyApi.ClientID` (token exchange only), `PartnerProducts.Worker.M2M.ClientID` (client credentials: `payments.read`, `partner-products.write`).

---

## 4. Domain Model

- **`GoldAccountApplication`** (Form VII): customer number and name (from EWP's record), date of birth (from the form, checked against photo ID), mobile, form number and date, the scan's document ID and SHA-256, the instruction ID, the Partner Bank's account number.
- **`GoldPurchase`** (a transfer authorization slip): amount, paying account, slip number and date, mobile on the slip, the scan, the instruction ID, the Partner Bank's order number and quote (price per gram, grams, lock time), the payment key (Idempotency-Key at EWP Payments), the payment number and network reference.

### States

```text
Form VII:  RECORDED → SENT → OPENED (the Partner Bank's account number)
                         ↘ REFUSED (its reason) - unless it already holds the customer's account: then linked (OPENED)
           SENT with no answer stays SENT: "Send again" re-sends the SAME instruction

Purchase:  RECORDED → SENT → ORDERED → PAID → ALLOCATED
                         ↘ REFUSED          ↘ CANCELLED (the payment was not made, or none was started in 30 minutes)
```

---

## 5. Business Rules

1. **The customer's facts come from EWP's record**, read again for every record, never typed: name, mobile and KYC status (Customer Onboarding). Only a KYC-verified adult with an Australian mobile may apply.
2. **Form VII** must be signed within 30 days and not in the future; the staff member confirms the photo ID (date of birth) and the signature; the scan is required. One active application per customer, one per form.
3. **A slip** needs an OPEN account, is signed **today** (Sydney) and is sent on that day only (it buys at that day's price); the mobile written on it must be the customer's; the paying account is one of the customer's active accounts with enough available; AUD, at most 50,000. One purchase per slip.
4. **Once only across lost answers.** SENT and the instruction ID are saved before each call; the ID never changes; "send again" is the same instruction. The Partner Bank does each instruction once.
5. **The purchase pays only the Partner Bank**: the payee is its settlement account from configuration (`PartnerBank:Settlement`), the amount and paying account are the slip's, the reference is the Partner Bank's order number, and the payment key is fixed when the slip is recorded. The payment runs through the normal Payments saga (approval tier, four eyes, funds hold, payment network).
6. **One ending per purchase.** The workflow sends the payment advice only for the purchase's own COMPLETED payment (key, amount and payee checked again); PAID is saved before the advice; a paid purchase is never cancelled; an unpaid order is cancelled when the payment is REJECTED, FAILED or COMPENSATION_FAILED, or when no payment was started within 30 minutes.
7. **The Partner Bank is the system of record**: the holding and its activity are read from it on every look, never copied.
8. **Viewing at the Partner Bank** is a PAR-style handoff: a new signed request per click, a one-time `request_uri` (60 s), the Partner Bank's own read-only page in a new tab.

---

## 6. Context-Specific Authorization Rules

| Operation | Rule |
|---|---|
| View (customer, applications, holding, purchases) | Delegated token through the Partner Products Journey API (`act`), `partner-products.read` + `partner.gold.view` + a branch; own branch only (another branch's customer looks like one without Digital Gold) |
| Record / send Form VII, record / send a slip, record the payment | Delegated token through the Journey API, `partner-products.write` + `partner.gold.order` + a branch; own branch only |
| Pay for a purchase | The Journey API exchanges the staff member's token for `payments.write`; Payments requires her own `payment.initiate` and branch |
| View at the Partner Bank | `partner.gold.view`; the instruction names the staff member who looks (from her token) |
| Payment advice / cancellation (`/internal`) | Only the Partner Products Worker's machine token: its client ID pinned, no `sub` and no `act` in it |
| Payment outcome (Payments `/internal`) | Only the Partner Products Worker's machine token with `payments.read`; one payment by its Idempotency-Key |
| Towards the Partner Bank | Mutual TLS (EWP's client certificate, the server trusted only through the PoC's root), EWP's own token bound to that certificate, every instruction signed (ES256, `typ partner-instruction+jwt`, 5 minutes, `jti`) |

---

## 7. Integration

- **Synchronous, for the staff member** (token exchange): Customer Onboarding (the customer), Accounts (paying accounts), Documents Management (the scans, document types `PARTNER_FORM_VII` and `PARTNER_TRANSFER_SLIP`), Payments (the purchase's payment).
- **The Partner Bank** (another organisation): `POST /api/distribution/gold-accounts`, `/gold-orders`, `/staff-views` (signed instructions), `GET /api/distribution/gold-accounts[?ewpCustomerNumber=|/{number}]`, `POST /api/gold-orders/{n}/payment-advice` and `/cancel` - all over mutual TLS with EWP's bound token.
- **Temporal**: the Journey API starts `goldPurchaseWorkflow` (ID `gold-purchase-<number>`, task queue `partner-products`, input = the purchase number only); the Worker runs it.
- **No Kafka events yet** (planned: an Outbox with recorded / sent / opened / refused / ordered / allocated / cancelled, to the audit trail).

---

## 8. Reserved Topology

- Local URLs: web `https://ewp-partner.dev.localhost:46384`; Journey API `https://partner-products-journey.dev.localhost:46385`; API `https://partner-products-api.dev.localhost:46387`; Worker health `https://localhost:46383/health`; Temporal `localhost:7233` (UI `http://localhost:8233`). The Shell menu: Partner Products → Digital Gold.
- The Partner Bank (home PC, `Start-PartnerBank.ps1 -Lan`): `partner-auth.ewp.home.arpa` 47401 (public) and 47411 (mutual TLS), `partner-products.ewp.home.arpa` 47402 (web, staff view), `partner-api.ewp.home.arpa` 47403 (mutual TLS); the laptop's hosts file maps the names to the PC.
- EWP's keys: `%USERPROFILE%\.ewp-pki` (made by `ps\pki\New-EwpLocalPki.ps1`).