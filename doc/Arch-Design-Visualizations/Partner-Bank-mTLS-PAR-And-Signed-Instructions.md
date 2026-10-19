# mTLS, PAR and signed instructions - a conversation

Camilla Parker, a customer of EWP (the bank "Four Walls"), wants Digital Gold from the **Partner Bank**, another bank. There are two ways a real bank offers this, and they need different protections:

1. **Camilla's own channel** - she is on her own device: EWP sends her to the Partner Bank, where she signs in and approves. This is **mutual TLS + PAR** (FAPI 2.0).
2. **The branch counter** - Camilla sits opposite Sophie, a customer service agent, and signs a paper form. She never types a password on the bank's computer. EWP acts as the Partner Bank's **distributor** and sends a **signed instruction** over mutual TLS instead.

The parts involved talk to each other like colleagues. Technical details are in *italics*.

| Who | Where |
|---|---|
| **EWP** | EWP's server that talks to the Partner Bank (on EWP's screens: the Partner Products API) |
| **Browser** | Camilla's own browser (part 1 only) |
| **Partner AS** | The Partner Bank's authorization server (Duende IdentityServer) |
| **Partner API** | The Partner Bank's API, the system of record of the Digital Gold account |
| **Sophie's screen** | Partner Products -> Digital Gold, in EWP's Shell (part 2 only) |

---

## Before anything: the handshake both parts share

**EWP:** Partner AS, I'd like to talk. Here is my client certificate.
*TLS 1.2/1.3 handshake on the mutual-TLS port (47411). The server asks for a client certificate; I present `ewp-partner-client`, issued by the PoC's trust root. Only the public part travels; the private key proves I hold it, inside the handshake.*

**Partner AS:** Your certificate chains to a root I trust, and it's the one registered for client `FourWalls.Ewp.PartnerProducts`. You are EWP. And here's mine.
*The Partner Bank authenticates the client by its certificate (`tls_client_auth`, RFC 8705) - no client secret exists that could leak.*

**EWP:** Yours chains to the PoC's own root, and the name matches `partner-auth.ewp.home.arpa`. You are the Partner Bank. I don't trust anything else - not even what Windows trusts.
*A custom-root chain check; no redirects followed.*

**Partner AS:** Good. Whatever token I give you on this connection, I'll **bind to your certificate**: it names your certificate's thumbprint inside it.
*`cnf: { "x5t#S256": "<SHA-256 of EWP's certificate>" }`. A stolen token is useless without EWP's private key.*

---

## Part 1 - Camilla's own channel: mutual TLS + PAR

Camilla is at home, in EWP's online banking, and taps **"Buy Digital Gold - AUD 10"**.

**EWP:** Partner AS, before I send Camilla anywhere, I'm **pushing** you the request myself, server to server.
*`POST /connect/par` on the mutual-TLS port. The body is not a list of loose parameters but a **signed request object** (JAR, RFC 9101): a JWT signed with EWP's request-signing key (ES256), holding `client_id`, `redirect_uri`, the PKCE `code_challenge` (S256), `prompt=login`, a 5-minute life, and `authorization_details` (RFC 9396): `{ "type": "digital_gold_purchase", "instructedAmount": { "currency": "AUD", "amount": "10.00" }, "debtorAccount": …, "customer": { "ewpCustomerNumber": "CUST-100001", … }, "actor": … }`.*

**Partner AS:** Let me check. The connection is yours (certificate). The signature is your signing key's, unchanged, for me, not expired. The details are a type I know, with an amount I accept. `prompt=login` is inside the signed part - good, so nobody can remove it on the way. Here's a **request_uri**. It's valid for 60 seconds, once.
*`201 { "request_uri": "urn:ietf:params:oauth:request_uri:…", "expires_in": 60 }`.*

**EWP:** Browser, please take Camilla to the Partner Bank with this reference.
*A redirect to `/connect/authorize?client_id=…&request_uri=…`. The browser carries only a meaningless one-time reference. The amount, the account and the customer never pass through the browser, so nothing there can read or change them.*

**Browser:** Partner AS, here's the reference EWP gave me.

**Partner AS:** I know this one: unused and not expired. Camilla must sign in - here, at the Partner Bank, with her Partner Bank password. *Always*, even if she signed in a minute ago, because the request said `prompt=login`.
*If Camilla has no Partner Bank account yet, the page offers registration with the details EWP verified, shown read-only, confirmed by a one-time code to her mobile.*

**Browser:** She's signed in.

**Partner AS:** Now the consent page. I show her exactly what EWP asked: *"Four Walls asks to buy AUD 10.00 of Digital Gold for you, paid from account 062-000 ••••5678. About 0.06 g at today's price."* Approve or decline - and I never remember the answer: the next request asks again.

**Browser:** She approved.

**Partner AS:** Browser, back to EWP's registered address with a code. It's valid for one minute.
*Redirect to EWP's `redirect_uri` with `code` and `state`. A decline returns `access_denied` and no code.*

**EWP:** Partner AS, here's the code, plus the secret behind my PKCE challenge.
*`POST /connect/token` on the mutual-TLS port, `grant_type=authorization_code`, `code_verifier`.*

**Partner AS:** Same certificate as the push, the verifier matches the challenge, and the code is unused. Here's Camilla's token, **bound to your certificate**, and stating exactly what she approved.
*The access token carries `sub` (Camilla at the Partner Bank), `cnf.x5t#S256` and the approved `authorization_details`.*

**EWP:** Partner API, place the order.
*`POST /api/gold-orders` over mutual TLS with the token.*

**Partner API:** The token is from my AS, meant for me, and bound to the certificate on this very connection. It allows AUD 10.00 for this customer - and that's all I take from it, nothing from your request body. Order placed, grams locked for 5 minutes.

---

## Part 2 - At the branch counter: mutual TLS + a signed instruction

Camilla walks into the branch. She doesn't want to type her password on Sophie's computer, and the bank wouldn't let her. She fills in and signs **Form VII**, the Partner Bank's Digital Gold application.

**Sophie's screen:** EWP, Sophie found Camilla (CUST-100001, onboarding complete) and recorded Form VII 109238, signed today, with its scan. The scan is in Documents Management. Its SHA-256 is `9c1f…`.
*Camilla's name, mobile and KYC status come from EWP's own customer record, never typed.*

**Sophie's screen:** Sophie clicks **Send to the Partner Bank**.

**EWP:** First I write down that I'm sending it, with an instruction ID that will never change: `7d0e…`. If my answer gets lost, I'll send the same instruction again, never a new one.
*Status SENT and `instruction_id` saved before the call.*

**EWP:** Partner AS, a token for **myself** this time, please. No customer is signing in.
*`POST /connect/token` on the mutual-TLS port, `grant_type=client_credentials`, `scope=partner.distribution`.*

**Partner AS:** Your certificate, your client, a scope you're registered for. Here's EWP's own token, **bound to your certificate** like every other one. It names no customer.

**EWP:** Partner API, an instruction. The body itself is **signed**.
*`POST /api/distribution/gold-accounts`, `Content-Type: application/jwt`. The body is a JWT signed with EWP's request-signing key (ES256, `kid` = the signing certificate's thumbprint), `typ: partner-instruction+jwt`, `iss` = EWP's client ID, `aud: partner-api`, 5 minutes, `jti` = the instruction ID. Its `instruction`: `{ "type": "digital_gold_account_opening", "customer": { "ewpCustomerNumber": "CUST-100001", "name": "Camilla Parker", "dateOfBirth": …, "mobile": "+614…", "kycVerified": true }, "form": { "number": "109238", "signedOn": "2026-10-17", "scanSha256": "9c1f…" }, "actor": { "lanId": "somit", "branch": "SYD001" } }`.*

**Partner API:** Let me check the call, then the instruction.
- The token: from my AS, for me, bound to this connection's certificate, with the distribution scope.
- The signature: your signing key, nothing changed, `typ` and audience right, signed by the same client that's calling, and less than 5 minutes old.
- The content: only the fields I know - an unknown one would be refused, not ignored. A KYC-verified adult, an Australian mobile, a form signed within 30 days, the scan's fingerprint.
- Once only: I've never seen this instruction ID, this form, or an account for this customer from you.

**Partner API:** All good. Digital Gold account **PB-DGA-CCRNCU** is open. I'm keeping your signed instruction - it's my evidence of exactly what you told me. I'm also texting Camilla myself: *"Your Digital Gold account is open. You applied through Four Walls. Not you? Call us."* I didn't see her sign, so she hears it from me too.

**EWP:** Thanks. OPENED, PB-DGA-CCRNCU.

**Sophie's screen:** The Partner Bank opened Digital Gold account PB-DGA-CCRNCU. Camilla holds 0.0000 g.
*The holding is read from the Partner Bank on every look, never copied into EWP.*

---

## When something goes wrong

**Partner AS:** No certificate, or one that doesn't chain to the trust root: I end the TLS handshake. There's no HTTP request to answer.

**Partner AS (part 1):** An unsigned request, an altered one, one signed with another key, one without `prompt=login` or without details: refused at the push. Nobody gets a request_uri.

**Partner API:** A token over a connection with **another** certificate: 401, even though the token itself is genuine. That's what binding is for.

**Partner API (part 2):** An unsigned instruction, an altered one (say, another name after signing), an expired one, one signed by another key, one with an extra field: 400. A second Form VII for the same customer, a reused slip, a slip whose mobile isn't the account's: 409. An account you didn't introduce: 404, so you can't even tell whether it exists.

**EWP (part 2):** If the Partner Bank doesn't answer, the form stays **SENT** and Sophie sees "send again". Sending again re-signs the **same** instruction ID. If the first one did arrive, the Partner Bank answers with the account it already opened. A refusal (400/404/409) is final and recorded as REFUSED with the Partner Bank's own words.

---

## Why PAR in part 1, and not in part 2

PAR protects a request that has to **pass through a browser**. At the counter, nothing passes through a browser: Camilla consented on paper, and EWP talks to the Partner Bank directly.

| What must be protected | Part 1: Camilla's own channel | Part 2: the counter |
|---|---|---|
| Who is calling | mutual TLS (EWP's certificate) | mutual TLS (EWP's certificate) |
| Whose authority | Camilla's: she signs in at the Partner Bank and approves on its consent page | Camilla's signature on Form VII, and EWP's accountability as distributor |
| What exactly is asked, tamper-proof | the signed request object (JAR), pushed server to server (PAR) - the browser carries only a reference | the signed instruction is the request body itself |
| Which token | Camilla's token, bound to EWP's certificate, carrying her approved `authorization_details` | EWP's own token (client credentials), bound to EWP's certificate |
| Proof afterwards | the Partner Bank keeps the signed request and the consent | the Partner Bank keeps the signed instruction; EWP keeps the scan and its SHA-256 |
| Telling the customer | she saw the consent page herself | the Partner Bank texts her: "Not you? Call us." |
| Doing it twice by accident | the code and the request_uri are one-time | the instruction ID (`jti`), the form number and the customer are once only |

- **mutual TLS everywhere.** Both sides prove who they are with certificates, and every token is bound to EWP's certificate. A stolen token can't be used without EWP's private key.
- **Signatures over what matters.** In both parts, the Partner Bank receives what EWP asked for as a signed JWT, so nobody can change it on the way, and EWP can't later deny it.
- **The customer authenticates where her relationship is.** On her own device, Camilla signs in at the Partner Bank, never at EWP. At the counter, her paper signature is the consent, and staff never handle her credentials.
- **The Partner Bank is the system of record.** EWP keeps evidence and status, and reads the gold back. It never keeps a copy of its own.

*In this PoC, part 1 runs between the Partner Bank and its check tool, which plays EWP (`tools/JourneyCheck` in the Partner Bank repository). EWP's own screens use part 2.*

*Code:*
- *Partner Bank repo: `src/PartnerBank.AuthorizationServer/Program.cs`, `Configuration/Config.cs` (PAR required, request objects, mutual TLS); `Authorization/AuthorizationDetails*.cs`; `Pages/Consent`*
- *Partner Bank repo: `src/PartnerBank.Api/Security/BoundTokens.cs`; `Distribution/SignedInstructions.cs`, `Distribution/Instructions.cs`; `Api/DistributionApi.cs`*
- *EWP: `src/Microservices/PartnerProducts/API/PartnerBank/PartnerBankClient.cs`, `PartnerBankCredentials.cs`; `Domain/GoldAccountApplication.cs` (`PrepareToSend`, `OpeningInstruction`)*
- *EWP: `ps/pki/New-EwpLocalPki.ps1` (the trust root, the mutual-TLS certificate, the request-signing key)*