import { config } from '@/lib/server/config';

export const dynamic = 'force-dynamic';

// ┌─ :: ARCHITECTURE + SECURITY :: The MFE's Shell origin, set at run time ─────────────────────────
// │ Why : The Shell is the only window this MFE exchanges messages with; its origin is set per
// │       environment, never compiled into the build.
// │ How : window.__EWP_RUNTIME__ = { shellOrigin } from this BFF's own setting (validated, a
// │       missing one fails the request), JSON-encoded, frozen, not cached. The layout loads it
// │       with the page's CSP nonce; lib/shell-origin.ts reads it - the same contract as the
// │       .NET BFFs' /ewp-runtime-config.js (Common/WebUtilities/Security/MfeRuntimeConfig.cs).
// │ Ref : doc/Containers.md | Features 1.5.2, 2.3.2 | HTML postMessage security | CWE-346
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function GET() {
  const settings = JSON.stringify({ shellOrigin: new URL(config.shellOrigin).origin });
  return new Response(`window.__EWP_RUNTIME__ = Object.freeze(${settings});`, {
    headers: {
      'Content-Type': 'text/javascript; charset=utf-8',
      'Cache-Control': 'no-cache',
    },
  });
}