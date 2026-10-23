import * as fs from 'node:fs';

/** The certificate this service's own listener serves; undefined = plain HTTP behind the platform's TLS. */
export interface ListenerTls {
  pfx: Buffer;
  passphrase: string | undefined;
}

// ┌─ :: SECURITY + ARCHITECTURE :: TLS here or at the platform - never neither by accident ────────
// │ Why : In a container TLS ends at the platform (the ingress for browsers, service mesh mTLS
// │       between pods - doc/Containers.md, decision 4); on a developer's machine this service is
// │       its own HTTPS endpoint. A missing setting must not quietly open plain HTTP.
// │ How : <PREFIX>_PLAIN_HTTP=true (the container image sets it) = plain HTTP, no certificate.
// │       Unset or false = HTTPS from <PREFIX>_TLS_PFX_PATH (+ _PASSWORD), which is then required.
// │       Plain HTTP and a certificate together are refused: one of the two is a mistake.
// │ Ref : doc/Containers.md | OWASP A02, A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function listenerTls(prefix: string, service: string): ListenerTls | undefined {
  const plainHttp = process.env[`${prefix}_PLAIN_HTTP`]?.trim() || 'false';
  if (plainHttp !== 'true' && plainHttp !== 'false') {
    throw new Error(`Configuration ${prefix}_PLAIN_HTTP must be true or false (is "${plainHttp}"); the ${service} refuses to start.`);
  }
  const pfxPath = process.env[`${prefix}_TLS_PFX_PATH`]?.trim();
  if (plainHttp === 'true') {
    if (pfxPath) {
      throw new Error(`${prefix}_PLAIN_HTTP=true and ${prefix}_TLS_PFX_PATH are both set - set one of them; the ${service} refuses to start.`);
    }
    return undefined;
  }
  if (!pfxPath) {
    throw new Error(`Configuration ${prefix}_TLS_PFX_PATH is not set (behind a TLS-ending platform set ${prefix}_PLAIN_HTTP=true); the ${service} refuses to start.`);
  }
  return { pfx: fs.readFileSync(pfxPath), passphrase: process.env[`${prefix}_TLS_PFX_PASSWORD`] };
}