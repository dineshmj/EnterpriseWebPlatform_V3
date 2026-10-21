/** The main page: Platform Health (read-only, every environment). */
export const HEALTH_PAGE = '/v1/operations/platform/view-all';

/** The simulator and fault controls (the switches; Development and test only). */
export const FAULT_CONTROLS_PAGE = '/v1/operations/platform/fault-controls';

/** Where the IDP sends the step-up window back to (fixed: never taken from the request). */
export const STEP_UP_DONE_PATH = '/api/auth/step-up-done';

const PAGES: readonly string[] = [HEALTH_PAGE, FAULT_CONTROLS_PAGE];

// ┌─ :: SECURITY :: Return-URL allow-list ─────────────────────────────────────────────────────────
// │ Why : A sign-in endpoint that returns to any URL it is given is an open redirector for phishing
// │       links.
// │ How : Only an exact match with one of this app's own pages is accepted (no query string, no
// │       prefix match); anything else returns to the main page.
// │ Ref : Features 2.3.5 | OWASP Unvalidated Redirects and Forwards | CWE-601
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function safeReturnUrl(requested: string | null | undefined): string {
  return requested && PAGES.includes(requested) ? requested : HEALTH_PAGE;
}