# 02 — Shell SPA

Deployable: `src/Shell/client-app` (Next.js `output: 'export'`, served by the Shell BFF)

## 1. No tokens, no secrets

- [x] **The SPA never holds a token.** Every call goes to its own BFF with the session cookie (`credentials: 'include'`). *Because* a token in the browser is exposed to every XSS bug. → `src/Shell/client-app/app/lib/auth.ts`
- [x] **Browser storage holds only the last-visited menu entry, per user, and never a token or personal data.** The page is reopened only if it is still in the person's menu. → `app/lib/last-visit.ts`
- [x] **No `dangerouslySetInnerHTML` and no `innerHTML`.** React escapes everything it renders. *Because* this closes the most common XSS sink.

## 2. Calls to the BFF

- [x] **Every BFF call carries Duende's `X-CSRF: 1` header.** A cross-site page cannot add it. → `app/lib/auth.ts`, `app/lib/notifications.ts`
- [x] **The menu comes from the BFF, already filtered by the person's roles.** The SPA shows only what it is given. The menu only shows the way: every API still authorizes each request. → `src/Shell/Controllers/MenuController.cs`
- [x] **The Shell's session is renewed only by real activity:** a click or key in the Shell, or `BSS_MFE_ACTIVITY` from the application in the frame; renewal at most every 5 minutes. *Because* each BFF has its own session, and the Shell's would otherwise end while the person works inside an application. → `app/lib/session.ts`
- [x] **An ended session is shown, never hidden:** the workspace bar shows **Session ended** with **Sign in again**, and live updates stop retrying. Checks made while reconnecting pass `slide=false`, so they never keep an idle session alive. → `app/hooks/useNotifications.ts`, `app/page.tsx`

## 3. Hosting the MFEs (iframe and postMessage)

- [x] **The iframe's origin is taken from the selected menu entry's `baseURL`, never from an incoming message.** *Because* a frame that navigated elsewhere must not receive the workspace context. → `app/page.tsx` (`expectedFrameOriginRef`)
- [x] **Messages are accepted only from the iframe's window AND the expected origin;** messages are posted only to that origin. `BSS_MFE_ACTIVITY` carries no data and only renews the Shell's session (throttled). → `app/page.tsx` (`handleFrameMessage`)
- [x] **Notification deep links are checked.** A path opens only if it matches a fixed pattern (`/v1/...` with one optional numeric or reference parameter), and only through a microservice in the person's own menu. That BFF checks the path again. *Because* an event must never turn into an arbitrary URL (open redirect). → `app/lib/notifications.ts` (`OPENABLE_PATH`)
- [ ] **Hardening idea: a `sandbox` attribute on the MFE iframe** (`allow-scripts allow-same-origin allow-forms allow-popups`). The CSP `frame-src` allow-list already limits what can load there.

## 4. Build

- [x] **Static export only:** no Node server in production; the BFF serves the files. *Because* there is one fewer runtime to patch and expose.
- [x] **Inline scripts and styles are allowed only by hash.** The BFF computes SHA-256 hashes of the exported pages at start-up for its CSP. A page changed after export is blocked, not trusted. → `src/Common/WebUtilities/Security/ContentSecurityPolicy.cs`
- [x] **A not-found page without inline styles.** *Because* the CSP would otherwise have to allow Next.js's default inline CSS. → `app/not-found.tsx`