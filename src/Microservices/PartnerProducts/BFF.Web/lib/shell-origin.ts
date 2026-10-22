// ┌─ :: SECURITY :: The Shell's origin, from this BFF at run time ─────────────────────────────────
// │ Why : The Shell is the only window this MFE exchanges messages with (context, navigation,
// │       activity, step-up). Its origin must be an exact allow-list entry - never
// │       document.referrer, never '*' - and set per environment without rebuilding the image.
// │ How : The BFF's /ewp-runtime-config.js (a beforeInteractive script in the layout: Next.js
// │       loads it before it starts the page) sets window.__EWP_RUNTIME__.shellOrigin from the
// │       BFF's ShellOrigin setting. Anything missing
// │       or not an https origin = a reserved address that can never match (RFC 2606 ".invalid"):
// │       no message is sent to or accepted from any window - fail closed, with a console error.
// │ Ref : doc/Containers.md | Features 1.5.2, 2.3.2 | HTML postMessage security | CWE-346
// └────────────────────────────────────────────────────────────────────────────────────────────────
declare global {
  interface Window {
    __EWP_RUNTIME__?: { shellOrigin?: string };
  }
}

const NOT_CONFIGURED = 'https://shell-origin.invalid';
let resolved: string | undefined;

/** The Shell's exact origin (scheme, host, port), or an address that matches no window. */
export function shellOrigin(): string {
  if (resolved) return resolved;
  // Pre-rendering has no window: answer without remembering it.
  if (typeof window === 'undefined') return NOT_CONFIGURED;

  const value = window.__EWP_RUNTIME__?.shellOrigin;
  if (isHttpsOrigin(value)) {
    resolved = value;
    return resolved;
  }
  // Not remembered: a later call sees the value once the script has run.
  console.error('The Shell origin is not configured (/ewp-runtime-config.js): this page exchanges no messages with the Shell.');
  return NOT_CONFIGURED;
}

function isHttpsOrigin(value: string | undefined): value is string {
  if (!value) return false;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' && url.origin === value;
  } catch {
    return false;
  }
}