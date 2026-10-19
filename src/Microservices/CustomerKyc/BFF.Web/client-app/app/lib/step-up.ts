/**
 * Step-up for a risky action (an approval, a decision, a resend, a reconciliation): the API answers
 * 403 "mfa_required" when the person's last sign-in with the authenticator code is too old. Inside
 * the Shell, this asks the Shell to show its in-page overlay with the IDP's code page; the BFF's
 * session gets tokens with a new auth_time, and the action is tried once more. Outside the Shell
 * (the page opened on its own), a small window does the same. The person stays signed in throughout.
 */
// ┌─ :: SECURITY :: Step-up in the Shell's overlay ────────────────────────────────────────────────
// │ Why : A risky action inside an MFE frame may need a fresh code; pop-up windows are blocked by
// │       many company browsers and nobody expects a code prompt in one.
// │ How : postMessage to the Shell ONLY (its fixed origin): BSS_STEP_UP_REQUEST; the Shell frames
// │       this app's /api/auth/step-up?in=shell in a modal overlay (the IDP's step-up code page is
// │       the only IDP page it may frame) and answers BSS_STEP_UP_RESULT - accepted only from the
// │       parent window and the Shell's origin, for this request's ID. Confirmed or not, the action
// │       is retried at most once: the API itself checks the fresh MFA.
// │ Ref : Features 2.1.5 | RFC 9470 | HTML postMessage security | Threat-Model R-13
// └────────────────────────────────────────────────────────────────────────────────────────────────
const SHELL_ORIGIN = process.env.NEXT_PUBLIC_SHELL_ORIGIN ?? 'https://shell.dev.localhost:46367';
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

/** Resolves true once the code was confirmed, false when the person closed the prompt first. */
export function confirmWithAuthenticator(): Promise<boolean> {
  return window.parent !== window ? askTheShell() : inAWindow();
}

function askTheShell(): Promise<boolean> {
  const requestId = crypto.randomUUID();
  return new Promise(resolve => {
    const finish = (confirmed: boolean) => {
      window.removeEventListener('message', onMessage);
      window.clearTimeout(timer);
      resolve(confirmed);
    };
    function onMessage(event: MessageEvent) {
      // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
      // │ If the answer is not from the parent window AND the Shell's origin, or not for this
      // │ request, ignore it: a page that frames this app must not answer for the Shell.
      // └──────────────────────────────────────────────────────────────────────────────────────
      if (event.source !== window.parent || event.origin !== SHELL_ORIGIN) return;
      if (event.data?.type !== 'BSS_STEP_UP_RESULT' || event.data.requestId !== requestId) return;
      finish(event.data.confirmed === true);
    }
    window.addEventListener('message', onMessage);
    const timer = window.setTimeout(() => finish(false), GIVE_UP_AFTER_MS + 30_000);
    window.parent.postMessage({ type: 'BSS_STEP_UP_REQUEST', requestId }, SHELL_ORIGIN);
  });
}

function inAWindow(): Promise<boolean> {
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

export const NOT_CONFIRMED = 'The action needs a recent confirmation with your authenticator code. Try again, and enter the code when asked.';