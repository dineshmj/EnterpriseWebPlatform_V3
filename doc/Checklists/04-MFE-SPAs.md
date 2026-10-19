# 04 — Microservice MFE SPAs

Deployables: `src/Microservices/<Context>/BFF.Web/client-app` for Customer Onboarding, KYC, Compliance, Accounts and Payments (Next.js `output: 'export'`, served by their own BFF)

## 1. No tokens, no secrets

- [x] **The SPA never holds a token.** It calls only its own BFF, with the session cookie. *Because* a token in the browser is exposed to every XSS bug. → `client-app/app/lib/api.ts`
- [x] **No tokens or personal data in browser storage,** and no `dangerouslySetInnerHTML` or `innerHTML`.
- [x] **A 401 from the BFF means the session has ended.** The SPA goes to the BFF's silent sign-in and comes back to the same page. → `app/lib/api.ts`

## 2. Requests that change state

- [x] **Every POST, PUT or DELETE carries the anti-forgery token** in `X-CSRF-TOKEN`. The SPA reads the token from its BFF's `/api/auth/csrf`. *Because* a cross-site page can neither read nor send it. → `app/lib/api.ts`
- [x] **A change is never retried automatically, except once after a step-up.** → `app/lib/api.ts`
- [x] **Step-up asks the Shell, not a pop-up window** (KYC, Compliance, Accounts, Payments, and the Operations web):
  - on 403 `mfa_required`, the SPA posts `BSS_STEP_UP_REQUEST` (a request ID only) to the Shell's origin;
  - the Shell shows its in-page overlay with the IDP's code page and answers `BSS_STEP_UP_RESULT`, accepted only from the parent window and the Shell's origin, for that request ID;
  - the action is retried once, whatever the answer; the API checks the fresh MFA itself.
  - Outside the Shell (the page opened on its own) a small window is used, as before.

  *Because* pop-up windows are blocked by many company browsers, and only the IDP's step-up code page may be framed - by the Shell. → `app/lib/step-up.ts`

## 3. Inside the Shell's frame

- [x] **Shell messages are accepted only from the parent window AND the configured Shell origin;** messages are posted only to that origin. *Because* a page that frames the MFE must not pose as the Shell. → `app/components/MfeShell.tsx`
- [x] **Rendering never trusts the screen.** Branch, role and permission decide only what is shown; every API authorizes again.
- [x] **Activity is reported to the Shell:** a click or key sends `BSS_MFE_ACTIVITY` to the Shell's origin, at most once a minute, with no data. *Because* the Shell keeps its own session alive only while the person is active, wherever they work. → `app/components/MfeShell.tsx` (`useReportActivityToShell`)

## 4. Build

- [x] **Static export,** with inline code allowed only by the BFF's start-up hashes (CSP). → `next.config.ts`, the BFF's `ContentSecurityPolicy.Build`
- [x] **A not-found page without inline styles.** → `app/not-found.tsx`
- [x] **Exports are regenerated with CompileAndExport after every front-end change.** *Because* a stale export means stale CSP hashes, and new inline code would be blocked.