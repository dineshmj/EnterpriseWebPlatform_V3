/** The pages a sign-in may return to - an allow-list, never an arbitrary URL (open redirect). */
export const TRAIL_PAGE = '/v1/audit/trail/view-all';
export const RECORD_PAGE = '/v1/audit/records/view-details';

// ┌─ :: SECURITY :: Return-URL allow-list ─────────────────────────────────────────────────────────
// │ Why : A sign-in endpoint that returns to any URL it is given is an open redirector for phishing
// │       links.
// │ How : Only this app's own pages are accepted; anything else returns to the main page.
// │ Ref : Features 2.3.5 | OWASP Unvalidated Redirects and Forwards | CWE-601
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function safeReturnUrl(value: string | null | undefined): string {
  if (!value) return TRAIL_PAGE;
  if (value === TRAIL_PAGE || value === `${TRAIL_PAGE}/`) return TRAIL_PAGE;

  // The record page with exactly one record reference.
  const match = /^\/v1\/audit\/records\/view-details\/?\?recordRef=([A-Za-z0-9-]{1,100})$/.exec(value);
  return match ? `${RECORD_PAGE}?recordRef=${match[1]}` : TRAIL_PAGE;
}