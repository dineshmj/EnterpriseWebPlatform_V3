# 04 — Microservice MFE SPAs

Deployables: `src/Microservices/<Context>/BFF.Web/client-app` for Customer Onboarding, KYC, Compliance, Accounts and Payments (Next.js `output: 'export'`, served by their own BFF)

## 1. No tokens, no secrets

- [x] **The SPA never holds a token.** It calls only its own BFF, with the session cookie. *Because* a token in the browser is exposed to every XSS bug. → `client-app/app/lib/api.ts`
- [x] **No tokens or personal data in browser storage,** and no `dangerouslySetInnerHTML` or `innerHTML`.
- [x] **A 401 from the BFF means the session has ended.** The SPA goes to the BFF's silent sign-in and comes back to the same page. → `app/lib/api.ts`

## 2. Requests that change state

- [x] **Every POST, PUT or DELETE carries the anti-forgery token** in `X-CSRF-TOKEN`. The SPA reads the token from its BFF's `/api/auth/csrf`. *Because* a cross-site page can neither read nor send it. → `app/lib/api.ts`
- [x] **A change is never retried automatically, except once after a step-up.** → `app/lib/api.ts`
- [x] **Step-up opens a small window, not an iframe** (KYC, Compliance, Accounts, Payments):
  - on 403 `mfa_required`, the window opens `/api/auth/step-up`;
  - the IDP asks only for a fresh code;
  - when the window is back on `/api/auth/step-up-done`, the SPA closes it and retries the action once.

  *Because* the IDP's pages may never be framed. → `app/lib/step-up.ts`

## 3. Inside the Shell's frame

- [x] **Shell messages are accepted only from the parent window AND the configured Shell origin;** messages are posted only to that origin. *Because* a page that frames the MFE must not pose as the Shell. → `app/components/MfeShell.tsx`
- [x] **Rendering never trusts the screen.** Branch, role and permission decide only what is shown; every API authorizes again.

## 4. Build

- [x] **Static export,** with inline code allowed only by the BFF's start-up hashes (CSP). → `next.config.ts`, the BFF's `ContentSecurityPolicy.Build`
- [x] **A not-found page without inline styles.** → `app/not-found.tsx`
- [x] **Exports are regenerated with CompileAndExport after every front-end change.** *Because* a stale export means stale CSP hashes, and new inline code would be blocked.