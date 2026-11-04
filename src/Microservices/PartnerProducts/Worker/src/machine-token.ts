import { createHash, createPrivateKey, createPublicKey, randomUUID, sign, type KeyObject } from 'node:crypto';
import { readFileSync } from 'node:fs';
import type { WorkerOptions } from './config';

// ┌─ :: SECURITY :: The worker's own machine identity, one token per API ─────────────────────────
// │ Why : The workflow runs long after the staff member's token expired, and it must never act as
// │       (or with the reach of) a person. A shared client secret, or a token that works for whoever
// │       copies it, would let anyone who reads them act as the worker.
// │ How : Client credentials for this worker's own client; one token per scope - payments.read for
// │       the Payments API, partner-products.write for the Partner Products API - so a token for one
// │       API is never sent to the other. The worker proves itself with a one-time assertion signed
// │       by its client key (private_key_jwt - no secret exists) and sends a DPoP proof, so the IDP
// │       binds the token to the worker's DPoP key (cnf.jkt); a token that comes back unbound is
// │       refused. Kept until a minute before it expires; a 401 drops it. Every API call carries a
// │       new proof (authorization()). Both APIs accept it only on their internal endpoints that
// │       pin this client ID.
// │ Ref : Features 2.1.2 | RFC 6749 4.4, RFC 7523, 7638, 9449 | FAPI 2.0 Security Profile §5.3.1,
// │       §5.3.2.2 | OWASP API2, API5 | Threat-Model R-9 | Partner Products step 8
// └────────────────────────────────────────────────────────────────────────────────────────────────
export class MachineToken {
  private readonly cache = new Map<string, { token: string; expiresAt: number }>();
  private readonly clientKey: KeyObject;
  private readonly clientKeyId: string;
  private readonly dpopKey: KeyObject;
  private readonly dpopJwk: { kty: string; crv: string; x: string; y: string };
  private idp?: Promise<{ issuer: string; tokenEndpoint: string }>;

  /** Reads both keys at start-up (fail closed): missing, unreadable, another curve, or the same key twice. */
  constructor(private readonly options: WorkerOptions) {
    this.clientKey = readKey(options.clientKeyPath, options.clientKeyPasswordPath, 'client key');
    this.dpopKey = readKey(options.dpopKeyPath, options.dpopKeyPasswordPath, 'DPoP key');
    this.clientKeyId = thumbprint(this.clientKey);
    // SEC: "I am this client" and "I hold this token" stay apart - one key for both is refused.
    if (this.clientKeyId === thumbprint(this.dpopKey)) throw new Error('The DPoP key must not be the client key.');
    const { kty, crv, x, y } = createPublicKey(this.dpopKey).export({ format: 'jwk' }) as Record<string, string>;
    this.dpopJwk = { kty, crv, x, y };
  }

  /** The headers for ONE call with this scope's token: the "DPoP" scheme and a proof for exactly this method and address. */
  async authorization(scope: string, method: string, url: string): Promise<{ Authorization: string; DPoP: string }> {
    const token = await this.for(scope);
    return { Authorization: `DPoP ${token}`, DPoP: this.proof(method, url, token) };
  }

  forget(scope: string) {
    this.cache.delete(scope);
  }

  private async for(scope: string): Promise<string> {
    const cached = this.cache.get(scope);
    if (cached && cached.expiresAt > Date.now() + 60_000) return cached.token;

    const idp = await this.discover();
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ If the IDP asks for a DPoP nonce (RFC 9449 section 8), send the request ONCE more with
    // │ it - new assertion, new proof; never loop.
    // └──────────────────────────────────────────────────────────────────────────────────────
    let response = await this.requestToken(idp, scope);
    let body = (await response.json().catch(() => ({}))) as { access_token?: string; token_type?: string; expires_in?: number; error?: string };
    const nonce = response.headers.get('DPoP-Nonce');
    if (response.status === 400 && body.error === 'use_dpop_nonce' && nonce) {
      response = await this.requestToken(idp, scope, nonce);
      body = (await response.json().catch(() => ({}))) as typeof body;
    }
    if (!response.ok || !body.access_token) {
      // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
      // │ The activity fails and Temporal retries it: the IDP may be restarting.
      // └──────────────────────────────────────────────────────────────────────────────────────
      throw new Error(`The IDP refused the worker's token for ${scope}: HTTP ${response.status} ${body.error ?? ''}`.trim());
    }
    // SEC: a bearer token here means the client is not set up for DPoP - refuse to use it.
    if (body.token_type?.toLowerCase() !== 'dpop') {
      throw new Error(`The IDP issued a '${body.token_type}' token for ${scope}; this worker uses DPoP-bound tokens only.`);
    }
    this.cache.set(scope, { token: body.access_token, expiresAt: Date.now() + Math.max(60, body.expires_in ?? 300) * 1000 });
    return body.access_token;
  }

  private requestToken(idp: { issuer: string; tokenEndpoint: string }, scope: string, nonce?: string): Promise<Response> {
    return fetch(idp.tokenEndpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', DPoP: this.proof('POST', idp.tokenEndpoint, undefined, nonce) },
      body: new URLSearchParams({
        grant_type: 'client_credentials',
        client_id: this.options.clientId,
        client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
        client_assertion: this.clientAssertion(idp.issuer),
        scope,
      }),
      signal: AbortSignal.timeout(10_000),
      // SEC: the form carries a client assertion - a redirect is an error, never followed.
      redirect: 'error',
    });
  }

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ The assertion's audience is the IDP's issuer EXACTLY as it states it (the IDP checks it
  // │ strictly), and the token endpoint is the one it names - read from its discovery document;
  // │ a failed discovery is not kept, so the next activity asks again.
  // └──────────────────────────────────────────────────────────────────────────────────────
  private discover(): Promise<{ issuer: string; tokenEndpoint: string }> {
    this.idp ??= fetch(`${this.options.authority}/.well-known/openid-configuration`, { signal: AbortSignal.timeout(10_000), redirect: 'error' })
      .then(async (response) => {
        const document = (response.ok ? await response.json() : {}) as { issuer?: unknown; token_endpoint?: unknown };
        if (typeof document.issuer !== 'string' || typeof document.token_endpoint !== 'string' || !document.token_endpoint.startsWith('https://')) {
          throw new Error(`IDP discovery named no issuer or https token endpoint (HTTP ${response.status}).`);
        }
        return { issuer: document.issuer, tokenEndpoint: document.token_endpoint };
      })
      .catch((error: unknown) => {
        this.idp = undefined;
        throw error;
      });
    return this.idp;
  }

  /** A one-time client assertion for ONE token request (RFC 7523, private_key_jwt). */
  private clientAssertion(issuer: string): string {
    const now = Math.floor(Date.now() / 1000);
    return signJwt(this.clientKey,
      { alg: 'ES256', typ: 'client-authentication+jwt', kid: this.clientKeyId },
      { iss: this.options.clientId, sub: this.options.clientId, aud: issuer, iat: now, nbf: now, exp: now + 60, jti: randomUUID() });
  }

  /** A DPoP proof for exactly ONE request (RFC 9449): its method and address without the query, a new jti, now, and the token's hash on an API call. */
  private proof(method: string, url: string, accessToken?: string, nonce?: string): string {
    const target = new URL(url);
    return signJwt(this.dpopKey, { typ: 'dpop+jwt', alg: 'ES256', jwk: this.dpopJwk }, {
      jti: randomUUID(),
      htm: method.toUpperCase(),
      htu: `${target.origin}${target.pathname}`,
      iat: Math.floor(Date.now() / 1000),
      ...(accessToken ? { ath: createHash('sha256').update(accessToken).digest('base64url') } : {}),
      ...(nonce ? { nonce } : {}),
    });
  }
}

/** Reads an encrypted PKCS#8 PEM with the password from its own file; EC P-256 only (ES256). */
function readKey(keyPath: string, passwordPath: string, name: string): KeyObject {
  const key = createPrivateKey({ key: readFileSync(keyPath), format: 'pem', passphrase: readFileSync(passwordPath, 'utf-8').trim() });
  if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
    throw new Error(`The worker's ${name} must be an EC P-256 key (ES256).`);
  }
  return key;
}

// RFC 7638: SHA-256 over the required members, in lexical order, without spaces.
function thumbprint(key: KeyObject): string {
  const { crv, kty, x, y } = createPublicKey(key).export({ format: 'jwk' });
  return createHash('sha256').update(JSON.stringify({ crv, kty, x, y })).digest('base64url');
}

// A compact JWS signed with ES256 (the IEEE P1363 signature form JWS asks for).
function signJwt(key: KeyObject, header: object, payload: object): string {
  const part = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const unsigned = `${part(header)}.${part(payload)}`;
  return `${unsigned}.${sign('sha256', Buffer.from(unsigned), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url')}`;
}