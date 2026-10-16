/**
 * The pages a sign-in may return to - an allow-list, never an arbitrary URL (open redirect).
 * This app has one page, so every return goes there, whatever was asked for.
 */
export const DIGITAL_GOLD_PAGE = '/v1/partner-products/digital-gold/view-all';

// ┌─ :: SECURITY :: Return-URL allow-list ─────────────────────────────────────────────────────────
// │ Why : A sign-in endpoint that returns to any URL it is given is an open redirector for phishing
// │       links.
// │ How : Only this app's own pages are accepted; anything else returns to the main page.
// │ Ref : Features 2.3.5 | OWASP Unvalidated Redirects and Forwards | CWE-601
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function safeReturnUrl(_requested: string | null | undefined): string {
  return DIGITAL_GOLD_PAGE;
}