/**
 * The pages a sign-in may return to - an allow-list, never an arbitrary URL (open redirect).
 * This app has one page, so every return goes there, whatever was asked for.
 */
export const HEALTH_PAGE = '/v1/operations/platform/view-all';

/** Where the IDP sends the step-up window back to (fixed: never taken from the request). */
export const STEP_UP_DONE_PATH = '/api/auth/step-up-done';

// ┌─ :: SECURITY :: Return-URL allow-list ─────────────────────────────────────────────────────────
// │ Why : A sign-in endpoint that returns to any URL it is given is an open redirector for phishing
// │       links.
// │ How : Only this app's own pages are accepted; anything else returns to the main page.
// │ Ref : Features 2.3.5 | OWASP Unvalidated Redirects and Forwards | CWE-601
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function safeReturnUrl(_requested: string | null | undefined): string {
  return HEALTH_PAGE;
}