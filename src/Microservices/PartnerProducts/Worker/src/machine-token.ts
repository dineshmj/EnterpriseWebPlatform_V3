import type { WorkerOptions } from './config';

// ┌─ :: SECURITY :: The worker's own machine identity, one token per API ─────────────────────────
// │ Why : The workflow runs long after the staff member's token expired, and it must never act as
// │       (or with the reach of) a person.
// │ How : Client credentials for this worker's own client; one token per scope - payments.read for
// │       the Payments API, partner-products.write for the Partner Products API - so a token for one
// │       API is never sent to the other. Kept until a minute before it expires; a 401 drops it.
// │       Both APIs accept it only on their internal endpoints that pin this client ID.
// │ Ref : RFC 6749 4.4 | OWASP API5 | Partner Products step 8
// └────────────────────────────────────────────────────────────────────────────────────────────────
export class MachineToken {
  private readonly cache = new Map<string, { token: string; expiresAt: number }>();

  constructor(private readonly options: WorkerOptions) {}

  async for(scope: string): Promise<string> {
    const cached = this.cache.get(scope);
    if (cached && cached.expiresAt > Date.now() + 60_000) return cached.token;

    const response = await fetch(`${this.options.authority}/connect/token`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: new URLSearchParams({
        grant_type: 'client_credentials',
        client_id: this.options.clientId,
        client_secret: this.options.clientSecret,
        scope,
      }),
      signal: AbortSignal.timeout(10_000),
    });
    const body = (await response.json().catch(() => ({}))) as { access_token?: string; expires_in?: number; error?: string };
    if (!response.ok || !body.access_token) {
      // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
      // │ The activity fails and Temporal retries it: the IDP may be restarting.
      // └──────────────────────────────────────────────────────────────────────────────────────
      throw new Error(`The IDP refused the worker's token for ${scope}: HTTP ${response.status} ${body.error ?? ''}`.trim());
    }
    this.cache.set(scope, { token: body.access_token, expiresAt: Date.now() + Math.max(60, body.expires_in ?? 300) * 1000 });
    return body.access_token;
  }

  forget(scope: string) {
    this.cache.delete(scope);
  }
}