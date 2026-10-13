/**
 * Step-up for a risky action (an approval, a decision, a resend, a reconciliation): the API answers
 * 403 "mfa_required" when the person's last sign-in with the authenticator code is too old. This
 * opens a small window on this app's BFF (/api/auth/step-up), where the IDP asks only for a fresh
 * code - its pages are never shown inside a frame - and the BFF's session gets tokens with a new
 * auth_time. When the window is back on this app's own address (/api/auth/step-up-done) it is
 * closed here and the action is tried once more. The person stays signed in throughout.
 */
const STEP_UP_URL = '/api/auth/step-up';
const STEP_UP_DONE_PATH = '/api/auth/step-up-done';
const GIVE_UP_AFTER_MS = 5 * 60_000;

/** True when an error body says the action needs a recent authenticator code. */
export function needsStepUp(status: number, body: string): boolean {
  if (status !== 403 || !body) return false;
  try {
    return (JSON.parse(body) as { code?: string }).code === 'mfa_required';
  } catch {
    return false;
  }
}

/** Opens the code window and resolves true once the code was confirmed, false when the window was closed first. */
export function confirmWithAuthenticator(): Promise<boolean> {
  const popup = window.open(STEP_UP_URL, 'ewp-step-up', 'popup,width=480,height=720');
  if (!popup) {
    return Promise.reject(new Error(
      'Your browser blocked the window for the authenticator code. Allow pop-ups for this site, then try again.'));
  }

  return new Promise(resolve => {
    const started = Date.now();
    const timer = window.setInterval(() => {
      let confirmed = false;
      try {
        confirmed = popup.location.pathname === STEP_UP_DONE_PATH;
      } catch {
        // Still at the IDP (another origin): its address cannot be read from here.
      }
      if (confirmed || popup.closed || Date.now() - started > GIVE_UP_AFTER_MS) {
        window.clearInterval(timer);
        if (!popup.closed) popup.close();
        resolve(confirmed);
      }
    }, 400);
  });
}

export const NOT_CONFIRMED = 'The action needs a recent confirmation with your authenticator code. Try again, and enter the code in the window that opens.';