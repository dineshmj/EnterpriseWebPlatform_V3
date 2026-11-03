import { createHash } from 'node:crypto';
import { Inject, Injectable, UnauthorizedException } from '@nestjs/common';
import type { Request } from 'express';
import { calculateJwkThumbprint, EmbeddedJWK, JWK, jwtVerify } from 'jose';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';

/** Proof lifetime and the clock skew allowed either way - the same as the .NET APIs (Duende: 5 s + 25 s). */
const PROOF_VALIDITY_SECONDS = 5;
const CLOCK_SKEW_SECONDS = 25;
/** FAPI 2.0 algorithms only; never "none", never a symmetric key. */
const ALGORITHMS = ['ES256', 'PS256'];
/** A proof's jti is kept for as long as the proof could be accepted. */
const REPLAY_WINDOW_MS = (PROOF_VALIDITY_SECONDS + 2 * CLOCK_SKEW_SECONDS) * 1000;
const MAX_REMEMBERED = 100_000;

// ┌─ :: SECURITY :: DPoP proofs checked on every call ─────────────────────────────────────────────
// │ Why : A DPoP-bound token is worth nothing without proof that the caller holds its key; a
// │       proof that is not checked - or can be used twice - would make it a bearer token again.
// │ How : Exactly one DPoP header, typ dpop+jwt, ES256 / PS256, a public JWK in the header that
// │       verifies the signature; htm = this request's method, htu = this request's address
// │       (no query); iat within 5 s (+25 s skew either way); ath = the token's hash; the key's
// │       RFC 7638 thumbprint = the token's cnf.jkt; a jti never seen before. Any miss = 401.
// │ Ref : Features 2.1.1 | RFC 9449 §4.3, §7.1, §11.1 | FAPI 2.0 Security Profile §5.3.1 |
// │       Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
// ┌─ DEBT ─────────────────────────────────────────────────────────────────────────────────────────
// │ The jti replay cache is IN MEMORY, per instance (the same decision as the .NET APIs): with
// │ several replicas a proof could be replayed once against another replica within ~55 s. A
// │ shared cache (Redis) closes that when the APIs scale out.
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class DPoPProofVerifier {
  private readonly seen = new Map<string, number>();
  private readonly publicOrigin: string;

  constructor(@Inject(JOURNEY_OPTIONS) options: Pick<JourneyOptions, 'publicOrigin'>) {
    this.publicOrigin = new URL(options.publicOrigin).origin;
  }

  /** Throws 401 unless the request carries a valid, unused proof for `accessToken` bound to `cnfJkt`. */
  async verify(request: Request, accessToken: string, cnfJkt: string): Promise<void> {
    const header = request.headers.dpop;
    if (typeof header !== 'string' || header.length === 0 || header.includes(',')) {
      throw new UnauthorizedException('Exactly one DPoP proof is required.');
    }

    let jwk: JWK;
    let payload: Record<string, unknown>;
    try {
      const verified = await jwtVerify(header, EmbeddedJWK, { typ: 'dpop+jwt', algorithms: ALGORITHMS });
      jwk = verified.protectedHeader.jwk!;
      payload = verified.payload;
    } catch {
      throw new UnauthorizedException('The DPoP proof is not valid.');
    }

    const now = Math.floor(Date.now() / 1000);
    const iat = typeof payload.iat === 'number' ? payload.iat : Number.NaN;
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A proof for another method or address, an old (or future) one, or one for another
    // │ token is refused - each proof is good for this one request only.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (payload.htm !== request.method ||
        typeof payload.htu !== 'string' || !this.isThisRequest(payload.htu, request) ||
        !(iat >= now - PROOF_VALIDITY_SECONDS - CLOCK_SKEW_SECONDS && iat <= now + CLOCK_SKEW_SECONDS) ||
        payload.ath !== createHash('sha256').update(accessToken).digest('base64url') ||
        typeof payload.jti !== 'string' || payload.jti.length === 0) {
      throw new UnauthorizedException('The DPoP proof does not match this request.');
    }

    // The token is bound to one key: the proof must be signed with exactly that key.
    if (await calculateJwkThumbprint(jwk) !== cnfJkt) {
      throw new UnauthorizedException('The DPoP proof is not signed with the key the token is bound to.');
    }

    this.takeOnce(`${cnfJkt}|${payload.jti}`);
  }

  /**
   * htu against this request's address, no query: the path as received, and the origin either as
   * the caller addressed it (scheme + Host - the .NET APIs' rule, so it holds through the
   * development proxies) or this API's configured public origin (the in-cluster address).
   */
  private isThisRequest(htu: string, request: Request): boolean {
    try {
      const claimed = new URL(htu);
      const actual = new URL(`${request.protocol}://${request.get('host')}${request.path}`);
      return (claimed.origin === actual.origin || claimed.origin === this.publicOrigin) && claimed.pathname === actual.pathname;
    } catch {
      return false;
    }
  }

  private takeOnce(key: string): void {
    const now = Date.now();
    if (this.seen.size >= MAX_REMEMBERED) {
      for (const [k, until] of this.seen) if (until <= now) this.seen.delete(k);
    }
    const until = this.seen.get(key);
    if (until !== undefined && until > now) {
      throw new UnauthorizedException('The DPoP proof was already used.');
    }
    // SEC: fail closed - when the cache is still full of live entries, refuse rather than forget one.
    if (this.seen.size >= MAX_REMEMBERED) {
      throw new UnauthorizedException('Too many proofs right now. Try again shortly.');
    }
    this.seen.set(key, now + REPLAY_WINDOW_MS);
  }
}