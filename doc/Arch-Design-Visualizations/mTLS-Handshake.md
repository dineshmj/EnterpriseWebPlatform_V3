# Mutual TLS (mTLS) Handshake — Human-Style Conversation

Here is a realistic dialogue showing how a **TLS 1.3 mTLS** handshake actually works behind the scenes, with enhancements for certificate pinning, token binding, and failure cases.

One key correction: in TLS 1.3, the symmetric key generation is so fast that it actually happens in the **very first sentence** (using an algebraic trick called Diffie-Hellman or ECDH), and then both sides use their certificates to *prove who they are* immediately after.

---

## 🤝 Act 1: The Secure Room & Server Identity

*(The Client walks up to the Server's castle gate.)*

**Client:** "Hey there! I want to open a secure channel with you. To save time, here are some random numbers and my half of a secret mathematical puzzle (**Client Ephemeral Public Key**). Let's use this to calculate a temporary symmetric key that we'll use to encrypt everything we say from this point on."

**Server:** "Welcome! I took your puzzle piece, mixed it with mine, and calculated the temporary symmetric key. From this exact millisecond forward, **everything we say is completely encrypted.**

But wait, you need to make sure I'm the real server, right? Here is my **Server Certificate**, issued by a Certificate Authority to prove my domain name. *(In this PoC that CA is not a public one: it is the private trust root both organisations agreed on, `ewp-trust-root`.)* To prove I actually own this certificate, I just took a digital snapshot of everything we've said so far, signed it using my **Server Private Key**, and encrypted it. Try verifying it!"

**Client:** *(Looks up the Server Certificate, extracts the Server's Public Key, verifies the signature, and checks the certificate chain).* "The signature matches perfectly! I am 100% sure you are the real server."

**Client:** *(Continues)* "But wait — I also need to verify something else. I don't just trust any CA. I have a **pinned root certificate** (a specific CA I was configured to trust, not the system's certificate store). Let me check that your certificate chains to that exact root... Yes! Your certificate's chain leads back to my trusted root CA. I am certain you are the legitimate server, not an impostor or MITM."

---

## 🕵️ Act 2: The Mutual Check (Client Authentication)

*(In regular TLS, the conversation would stop here and data would flow. But this is **mTLS**... In the real TLS 1.3 message order, the server sends its request for the client's certificate (**CertificateRequest**) in the same reply as its own certificate. The client answers with its certificate after checking the server. The two acts are split here only to make them easier to follow.)*

**Server:** "Great. Now it's my turn. I don't let just anyone in here. **I need to see your certificate, too.** It needs to be issued by one of these specific CAs that I trust, and it must support client authentication (**Extended Key Usage: TLS Web Client Authentication**)."

**Client:** "No problem, I brought it with me. Here is my **Client Certificate** (which contains my Public Key)."

**Server:** *(Verifies the Client Certificate chain against its pinned root CA, checks the certificate's validity period, and confirms the **Extended Key Usage** extension).* "I see the certificate, and it looks valid and chains to my trusted root. But how do I know *you* didn't just steal that certificate from someone else's computer? Prove to me that you possess the secret private key that matches it."

**Client:** "Fair enough. I'll do exactly what you did. I just took a digital snapshot of our entire conversation up to this point, signed it using my **Private Key**, and included it in a **CertificateVerify** message. Here is the signature."

**Server:** *(Takes the Client's Public Key from the certificate and uses it to verify the signature over the handshake transcript).* "Boom. The signature is mathematically perfect and matches the exact conversation we've had. Only the true owner of that certificate could have generated that signature. I now know exactly who you are, and I trust you."

*(How the Partner Bank does this: the TLS layer lets any certificate through, so that the application decides and answers with a proper error. The application then checks that the certificate chains to the PoC's root ONLY (not to any root Windows trusts), that it is within its validity period, and that it has the client-authentication purpose. The authorization server also checks that the certificate's subject is exactly the one registered for EWP (RFC 8705 `tls_client_auth`: `CN=EWP BSS Partner Products Client, O=Four Walls Inc.`). A different certificate from the same root, such as the Partner Bank's own server certificate, is not EWP.)*

---

## 🔐 Act 3: Token Binding (Proof-of-Possession — RFC 8705)

*(The mTLS handshake is complete, but there's one more security layer... Two different Partner Bank servers take part here, each over its own mutual-TLS connection: the **authorization server** issues the token (`partner-auth…:47411/connect/token`, client credentials, scope `partner.distribution`), and the **API** checks it (`partner-api…:47403`).)*

**Authorization server:** "Before you go, I'm going to issue you an access token. But here's the key: this token is **bound to your certificate**. I'm going to embed your certificate's **thumbprint** (its SHA-256 fingerprint, `cnf.x5t#S256`) inside the token and sign it."

**Client:** *(Receives the access token)* "Got it. I'll keep this token safe. If anyone tries to use it without my certificate's private key, the token will be useless."

**Authorization server:** "Exactly. If someone steals your token (from a log or a proxy, say), they still can't use it without your certificate and its private key. This is **proof-of-possession binding** — the token only works for the certificate it was issued to."

**Client:** *(On subsequent API calls)* "When I make requests, I'll send both:
- `Authorization: Bearer <token>` (the bound access token)
- My client certificate in the TLS handshake (the same one the token is bound to)"

**API:** *(On each request)* "I verify:
1. The token's signature, issuer, audience (`partner-api`), type (`at+jwt`) and expiry.
2. The token's embedded certificate thumbprint matches the client certificate presented in the TLS handshake of THIS connection (and that certificate chains to the PoC's root).
If the thumbprints don't match, I reject the request with 401. **A stolen token is useless on its own.**"

---

## 📨 Act 4: Instruction Signing & Idempotency

*(After authentication and token exchange, signed instructions are sent...)*

**Client:** "I have an instruction to send (e.g., open an account). I'll sign it with my **request-signing key** (a separate ECDSA P-256 key with its own certificate, NOT the TLS client certificate) and include:
- A stable **instruction ID (jti)** that never changes
- A 5-minute expiry
- My client ID as the issuer
- The instruction's payload

Here's the signed JWT:

"
Header: { "typ": "partner-instruction+jwt", "kid": "<cert-thumbprint>", "alg": "ES256" }
Body: { "iss": "<client-id>", "aud": "partner-api", "jti": "<stable-id>", "exp": "<+5min>", "instruction": {...} }
Signature: <signed with request-signing key>
"

**Server:** "I receive and verify the JWT signature. I check:
1. The signature is valid, using the public key of your **request-signing certificate**. I hold a copy of it (`ewp-request-signing.cer`), and `kid` names it by its thumbprint. It is not the key from your TLS client certificate.
2. The type (`partner-instruction+jwt`) and the audience (`partner-api`) are what I expect, and the issuer is the same client that is calling me on this connection.
3. It is not expired, and it lives at most 5 minutes from `iat`.
4. The instruction contains exactly the fields I accept. An unknown field is refused, not ignored.
5. The instruction ID (`jti`) hasn't been processed before.

If everything checks out, I process the instruction and record its `jti` with the result. If you send the same `jti` again, I find what I already did and answer with that (200 instead of 201) — **never a duplicate.**"

**Client:** *(After a timeout or network error)* "Hey, I didn't get an answer. I'll send the same instruction again with the same `jti`. I sign it afresh (new `iat` and `exp`), but the ID never changes."

**Server:** "I see the same `jti`. I already processed this — here's the account I opened then. No duplicate instruction, no duplicate account opened."

---

## ❌ Act 5: Failure Cases

### Case 1: Untrusted Server Certificate

**Client:** "Server, I received your certificate. Let me check if it chains to my pinned root CA...

ERROR: Your certificate chains to a different root CA (not the one I'm configured to trust). This could be:
- An impostor server
- A MITM attack
- A misconfigured server

I'm rejecting this connection immediately. No HTTP data will flow."

**Result:** Connection refused. No handshake completion.

---

### Case 2: Missing or Invalid Client Certificate

**Server:** "Client, I requested your certificate, but you either didn't send one or sent an invalid certificate (expired, wrong Extended Key Usage, doesn't chain to my trusted root, or isn't the subject registered for you).

Access denied."

**Result:** No certificate at all: the mutual-TLS ports require one, so the TLS handshake fails and no HTTP request is ever made. A certificate that fails the checks: the TLS layer lets it through (the application decides), then the authorization server refuses the client (`invalid_client`), and the API answers `401 Unauthorized`.

---

### Case 3: CertificateVerify Signature Mismatch

**Server:** "Client, I received your certificate and your **CertificateVerify** signature. Let me verify the signature using your public key...

ERROR: The signature doesn't match. Either:
- You don't own the private key for this certificate
- Someone forged the signature
- The handshake transcript was tampered with

I'm closing the connection."

**Result:** TLS handshake failure. Connection terminated.

---

### Case 4: Token Thumbprint Mismatch (Token Replay/Theft)

**Server:** *(On an API request)* "You sent an access token and presented a client certificate. Let me verify:
1. Token signature: ✓ valid
2. Token expiry: ✓ not expired
3. Token's embedded certificate thumbprint: `ABC123`
4. Your presented certificate's thumbprint: `XYZ789`

ERROR: Thumbprints don't match! This token was issued to a different certificate. You either:
- Stole the token from another client
- Are using a compromised certificate
- Are experiencing a misconfiguration

Access denied. Returning `HTTP 401 Unauthorized`."

**Result:** Request rejected. Potential security alert logged.

---

### Case 5: Expired or Tampered Instruction JWT

**Server:** *(Receives a signed instruction)* "I'm verifying your signed instruction:
1. Signature: ✓ valid
2. Issuer: ✓ matches your client ID
3. Audience: ✓ is `partner-api`
4. Expiry: ✗ this instruction was signed 10 minutes ago (expired after 5 minutes)

ERROR: Instruction expired. You must sign it again (a fresh `iat` and `exp`). Returning `HTTP 400 Bad Request`."

**Result:** Instruction rejected. The client signs it again, with the SAME `jti`, so that the retry is still the same instruction.

---

## 📋 Security Properties Achieved

| Property | How It's Achieved |
|----------|-------------------|
| **Server authentication** | Server cert validated against pinned root CA; name verified |
| **Client authentication** | Client cert validated; `CertificateVerify` proves private key possession |
| **Confidentiality** | TLS 1.3 symmetric encryption (AES-256-GCM or similar) |
| **Integrity** | TLS 1.3 authenticated encryption (AEAD: every record is encrypted and tamper-checked together) + signed instructions (JWTs) |
| **Token binding (PoP)** | Access token bound to the certificate's SHA-256 thumbprint (`cnf.x5t#S256`, RFC 8705): useless without the private key |
| **Non-repudiation** | The signature on each instruction (EWP's request-signing key) proves exactly what EWP instructed; the Partner Bank keeps it |
| **Idempotency** | Stable `jti` + server deduplication prevents duplicate processing on retry |
| **Forward secrecy** | Ephemeral key exchange (ECDHE) ensures past sessions stay secret even if keys are compromised later |

---

## 🎯 Real-World Context (EWP & Partner Bank)

In the **EWP ↔ Partner Bank** integration:
- **EWP (Client)**: presents its client certificate + request-signing key
- **Partner Bank (Server)**: validates via pinned root CA, issues bound tokens
- **Instruction flow**: EWP signs instructions with stable IDs; Partner Bank processes once, returns idempotent answers
- **Result**: EWP can prove it instructed an account opening; Partner Bank can prove which exact instructions it received; no repudiation, no duplicates even on network failures

---

## ✅ Standards & References

- **TLS 1.3**: RFC 8446 (client certificates: CertificateRequest §4.3.2, Certificate and CertificateVerify §4.4.2–4.4.3)
- **Mutual TLS for OAuth (client authentication and certificate-bound tokens)**: RFC 8705
- **Proof-of-Possession (PoP)**: RFC 8705, RFC 7800 (the `cnf` claim)
- **JWT Signing**: RFC 7515 (JWS), RFC 7519 (JWT; `jti` §4.1.7)
- **Once-only instructions**: by the instruction ID (`jti`) and the unique indexes at the Partner Bank. The `Idempotency-Key` header is what EWP's own Payments API uses; it is not part of this exchange.

# What Gets Stored Where

### **On EWP's machine** (the client calling Partner Bank):

1. **EWP's own client certificate** (public + private key):
   - File: `%USERPROFILE%\.ewp-pki\ewp-partner-client.pfx` (PKCS#12, RSA, client-authentication purpose), with its password in `ewp-partner-client.pfx.password`
   - Contains: EWP's public cert + EWP's private key
   - **Must be kept secure** — it proves EWP's identity
   - **Used for**: Authenticating EWP to the Partner Bank during TLS handshake; presented as proof that EWP is a trusted client

2. **EWP's own request-signing key** (ECDSA, public + private):
   - File: `%USERPROFILE%\.ewp-pki\ewp-request-signing.pfx` (ECDSA P-256, no extended key usage), with its password in `ewp-request-signing.pfx.password`
   - Contains: the private key used to sign instructions (and its certificate, whose public part goes to the Partner Bank)
   - **Strictly confidential** — signing key never leaves the machine
   - **Used for**: Signing every instruction (account opening, purchase orders) with ES256; generates cryptographic proof that EWP issued and authorizes each instruction

3. **The shared root CA certificate** (public only):
   - File: `%USERPROFILE%\.ewp-pki\ewp-trust-root.cer`
   - Contains: only the public key and cert info, no private key
   - **Not confidential** — this is the root CA's public cert that both organizations trust
   - **Used for**: Validating the Partner Bank's server certificate during TLS handshake; ensures the Partner Bank's cert chains to the agreed-upon root CA (prevents MITM and impostor attacks)

4. **The root's private key** (PoC only):
   - File: `%USERPROFILE%\.ewp-pki\ewp-trust-root.pfx`
   - Made by `ps\pki\New-EwpLocalPki.ps1`, it signed all the certificates above and the Partner Bank's server certificate. It stays on the laptop and never goes to the Partner Bank. In production, a real certificate authority run by a third party holds it (Partner Bank Threat Model R4).

5. **Partner Bank's server certificate** (received during TLS handshake, not stored):
   - NOT pre-stored on EWP's machine
   - Received in the TLS handshake from Partner Bank
   - **Used for**: Verifying the Partner Bank's identity and extracting its public key to validate signatures; temporary, discarded after handshake (or cached for session reuse)

---

### **On Partner Bank's machine** (the server):

1. **Partner Bank's own server certificate** (public + private key):
   - File: `%USERPROFILE%\.ewp-pki\partner-bank-server.pfx` (RSA, server-authentication purpose, for the `partner-*.ewp.home.arpa` names), with its password in `partner-bank-server.pfx.password`
   - Contains: Partner Bank's public cert + private key
   - **Used for**: Proving Partner Bank's identity to EWP during TLS handshake; signs handshake messages (CertificateVerify) to prove key possession

2. **The shared root CA certificate** (public only):
   - File: `%USERPROFILE%\.ewp-pki\ewp-trust-root.cer`
   - Same root file as EWP has
   - **Used for**: Validating EWP's client certificate when EWP connects; ensures EWP's cert chains to the agreed-upon root CA

3. **EWP's request-signing certificate** (public only, pre-stored):
   - File: `%USERPROFILE%\.ewp-pki\ewp-request-signing.cer`, copied from the laptop
   - **Used for**: Checking the signature on every instruction EWP sends. Without this file the Partner Bank API refuses to start.

4. **EWP's client certificate** (received during TLS handshake, not pre-stored):
   - NOT pre-stored on Partner Bank's machine. Only its exact **subject** is in the authorization server's configuration (`tls_client_auth`), so a different certificate from the same root is not accepted as EWP.
   - Received from EWP during the TLS handshake
   - **Used for**: Verifying EWP's identity and extracting its public key to validate the CertificateVerify signature; temporary, discarded after handshake (or cached for session reuse). Its SHA-256 thumbprint is what EWP's tokens are bound to.