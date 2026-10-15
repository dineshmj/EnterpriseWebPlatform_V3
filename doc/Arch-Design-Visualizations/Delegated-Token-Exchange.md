# Delegated Token Exchange - a conversation

Sophie, a customer service agent, submits a new onboarding application with two PDFs: an identity proof and a tax proof. The parts of EWP that handle it talk to each other like colleagues. Technical details are in *italics*.

| Who | In EWP |
|---|---|
| **SPA** | The Customer Onboarding MFE in Sophie's browser |
| **BFF** | The Customer Onboarding BFF (.NET, Duende BFF) |
| **CO API** | The Customer Onboarding API |
| **IDP** | The Identity Provider (Duende IdentityServer) |
| **Docs API** | The Documents Management API |

---

## The happy path

**SPA:** Hi BFF, Sophie just pressed Submit. Here's the new customer, plus her two PDFs.
*`POST` (multipart) with Sophie's session cookie and the `X-CSRF-TOKEN` header. I don't hold any tokens. I couldn't attach one if I wanted to.*

**BFF:** Got it. The cookie tells me it's Sophie, and the anti-forgery token tells me the request really came from your page, not another site. Give me a moment, this takes a few steps.

**BFF:** CO API, please create this customer and an onboarding application for them.
*`Authorization: Bearer <Sophie's access token>`. The CO API is the API Sophie signed in for, so her own token is the right one here.*

**CO API:** Done. That's CUST-100042, application APP-261015-K3D9QX.

**BFF:** Thanks. Now the PDFs go to Documents Management. Sophie's token wasn't issued for the Docs API, so it would refuse it, and rightly so. IDP, can you swap it for me?
*`POST /connect/token`, `grant_type=urn:ietf:params:oauth:grant-type:token-exchange`, `subject_token=<Sophie's access token>`, `subject_token_type=urn:ietf:params:oauth:token-type:access_token`, `scope=documents-management.write`, plus my client ID and secret.*

**IDP:** First, who are you? ...Right, the Customer Onboarding BFF, and the secret checks out. Now the token you brought me:
- I issued it, the signature is mine, and it hasn't expired.
- It belongs to a person, Sophie. *(A machine's token would be refused: there's nobody to act for.)*
- It's one you got yourself when Sophie signed in, not a token someone else already exchanged. You're on my list for exactly that, and nothing more.
- You're allowed to ask for `documents-management.write`.

**IDP:** All good. Here's a new token, for the Docs API only. It still says it's *Sophie* (`sub`), with her role and her branch, and it notes that *you* are acting for her (`act: { client_id: CustomerOnboarding BFF }`). I've kept the time and method of her original sign-in, because an exchange is not a new sign-in.

**BFF:** Perfect. I'll use it for this submission only. Next time, I'll ask again.
*Nothing is cached between requests or between people.*

**BFF:** Docs API, here's the identity proof for CUST-100042.
*`POST` (multipart), `Authorization: Bearer <exchanged token>`, `X-Document-Type: KYCProof`.*

**Docs API:** Let me look at that token. It's from the IDP, meant for me, still valid, and it has the write scope. It's Sophie, from branch SYD001, and I'll file the document under that branch. Now the file: it claims to be a PDF, so I'll check its first bytes rather than trust the name... yes, it really is a PDF. Encrypted and stored. Document `7f3c…`, created.

**BFF:** And here's the tax proof.

**Docs API:** Same checks, also fine. Document `a91e…`.

**BFF:** CO API, please submit APP-261015-K3D9QX, with these two documents as its evidence.

**CO API:** Submitted. KYC will pick it up from here.

**BFF:** SPA, all done: 201 Created. Here are the customer number, the application number and the two document IDs.

**SPA:** Lovely. I'll show Sophie "Application submitted".

---

## When something goes wrong

**IDP:** If I can't swap the token, I answer `invalid_grant`. That happens if it has expired, has no person in it, or you aren't allowed to exchange it.

**BFF:** If you're just busy or briefly down, I try again, at most three times in all, waiting a little longer each time (250 ms, then 500 ms). If it still doesn't work, I stop and tell the SPA: *"Documents Management authorization could not be established. The onboarding application remains available for retry."* (HTTP 502)

**Docs API:** If the token isn't one I accept, I say 401. If it has no branch in it, I say 403. If the file isn't really a PDF, I refuse it.

**BFF:** If the tax proof fails after the identity proof went through, I don't leave a half-finished submission behind. I ask you to delete the identity proof I just uploaded, and then I pass the error on to the SPA. The application stays a draft, so Sophie can try again.

---

## Why it's done this way

- **No tokens in the browser.** Sophie's tokens stay in the BFF's server-side session. The SPA only ever has a cookie that script can't read.
- **Each token is meant for one API.** Sophie's own token works for the CO API. The Docs API gets a token issued for the Docs API. A token stolen from one API is useless at the other.
- **The person stays the person.** The Docs API applies Sophie's branch, and the audit records Sophie, not "the BFF". The `act` claim still shows which service called on her behalf.
- **A fixed list of who may exchange what.** If a client or token isn't on the IDP's list, the IDP refuses it.
- **Short and fresh.** A new exchange for every submission: nothing is shared between people, and nothing outlives the request.

*Code:*
- *`src/Microservices/CustomerOnboarding/BFF.Web/Services/DocumentsManagementTokenService.cs`*
- *`Controllers/OnboardingController.cs`*
- *`src/IDP/Security/TokenExchangeGrantValidator.cs`*
- *`src/Microservices/DocumentsManagement/API/API/Controllers/DocumentsController.cs`*