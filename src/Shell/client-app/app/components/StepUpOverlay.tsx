'use client';

import { useEffect, useRef } from 'react';
import styles from './StepUpOverlay.module.css';

/** The Shell's own page a step-up ends on (public/step-up-done.html): same origin, so its address can be read. */
const DONE_PATH = '/step-up-done.html';
const GIVE_UP_AFTER_MS = 5 * 60_000;

// ┌─ :: SECURITY :: Step-up in an in-page overlay ─────────────────────────────────────────────────
// │ Why : A risky action needs a fresh authenticator code; a pop-up window is blocked by many company
// │       browsers, and the IDP's sign-in pages must never be framed (clickjacking).
// │ How : The Shell (never framed itself) shows a modal over the page and frames the requesting
// │       application's OWN /api/auth/step-up?in=shell - built from the origin the Shell accepted for
// │       the frame, never from the message. The IDP lets the Shell frame only its step-up code page
// │       (no password on it). The step-up ends on the Shell's own done page: the overlay sees that
// │       address and closes. Cancel, Escape or 5 minutes close it unconfirmed. If the frame cannot
// │       show the IDP (e.g. a full sign-in is needed), "Use a separate window" does the same in a
// │       window opened by this click. The API checks the fresh MFA itself; this only says "retry".
// │ Ref : Features 2.1.5 | RFC 9470 | OWASP Clickjacking Defense | Threat-Model R-13
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function StepUpOverlay({ origin, onFinish }: { origin: string; onFinish: (confirmed: boolean) => void }) {
  const frameRef = useRef<HTMLIFrameElement>(null);
  const finishedRef = useRef(false);
  const stepUpUrl = `${origin}/api/auth/step-up?in=shell`;

  const finish = (confirmed: boolean) => {
    if (finishedRef.current) return;
    finishedRef.current = true;
    onFinish(confirmed);
  };

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') finish(false); };
    window.addEventListener('keydown', onKey);
    const timer = window.setTimeout(() => finish(false), GIVE_UP_AFTER_MS);
    return () => { window.removeEventListener('keydown', onKey); window.clearTimeout(timer); };
  }, []);

  /** True when the window is on the Shell's own done page (another origin's address cannot be read). */
  const isDone = (target: Window | null | undefined) => {
    try {
      return target?.location.origin === window.location.origin && target.location.pathname === DONE_PATH;
    } catch {
      return false;
    }
  };

  const openSeparateWindow = () => {
    const popup = window.open(stepUpUrl, 'ewp-step-up', 'popup,width=480,height=720');
    if (!popup) return;
    const timer = window.setInterval(() => {
      if (isDone(popup)) {
        window.clearInterval(timer);
        popup.close();
        finish(true);
      } else if (popup.closed || finishedRef.current) {
        window.clearInterval(timer);
      }
    }, 400);
  };

  return (
    <div className={styles.backdrop}>
      <div className={styles.dialog} role="dialog" aria-modal="true" aria-labelledby="step-up-title">
        <div className={styles.head}>
          <h2 id="step-up-title" className={styles.title}>Confirm it&apos;s you</h2>
          <p className={styles.lead}>The action you started needs a fresh code from your authenticator app. Your page stays as it is.</p>
        </div>
        <iframe
          ref={frameRef}
          className={styles.frame}
          src={stepUpUrl}
          title="Authenticator code"
          onLoad={() => { if (isDone(frameRef.current?.contentWindow)) finish(true); }}
        />
        <div className={styles.foot}>
          <button type="button" className={styles.link} onClick={openSeparateWindow}>Nothing shown? Use a separate window</button>
          <button type="button" className={styles.cancel} onClick={() => finish(false)}>Cancel</button>
        </div>
      </div>
    </div>
  );
}