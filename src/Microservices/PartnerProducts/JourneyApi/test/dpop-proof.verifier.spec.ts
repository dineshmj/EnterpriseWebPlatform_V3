import { createHash, generateKeyPairSync, KeyObject, randomUUID } from 'node:crypto';
import { UnauthorizedException } from '@nestjs/common';
import type { Request } from 'express';
import { calculateJwkThumbprint, JWK, SignJWT } from 'jose';
import { DPoPProofVerifier } from '../src/auth/dpop-proof.verifier';

// ┌─ :: SECURITY :: DPoP proofs: only the token's own key, for this one request, once ─────────────
// │ Why : A DPoP-bound token is a bearer token again if a proof is accepted for another key,
// │       another request, a stale moment or a second time.
// │ How : The REAL verifier with real ES256 proofs (no mocks); each test changes ONE thing in an
// │       otherwise valid proof and expects a 401 - the first test shows the valid one passes.
// │ Ref : src/auth/dpop-proof.verifier.ts | RFC 9449 §4.3, §11.1 | Features 2.1.1
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('DPoPProofVerifier - a DPoP-bound token is honoured only with a fresh proof of its own key', () => {
  const PUBLIC_ORIGIN = 'https://journey.ewp.example';
  const PATH = '/v1/journeys/something';
  const TOKEN = 'the-delegated-access-token';

  const newKey = () => {
    const { privateKey, publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
    const { kty, crv, x, y } = publicKey.export({ format: 'jwk' });
    return { privateKey, jwk: { kty, crv, x, y } as JWK };
  };

  let key: { privateKey: KeyObject; jwk: JWK };
  let jkt: string;
  let verifier: DPoPProofVerifier;

  beforeEach(async () => {
    key = newKey();
    jkt = await calculateJwkThumbprint(key.jwk);
    verifier = new DPoPProofVerifier({ publicOrigin: PUBLIC_ORIGIN });
  });

  /** A valid proof for GET PUBLIC_ORIGIN + PATH and TOKEN, with `changes` applied. */
  const proof = (changes: { claims?: Record<string, unknown>; header?: Record<string, unknown>; signWith?: KeyObject } = {}) =>
    new SignJWT({
      htm: 'GET',
      htu: `${PUBLIC_ORIGIN}${PATH}`,
      iat: Math.floor(Date.now() / 1000),
      jti: randomUUID(),
      ath: createHash('sha256').update(TOKEN).digest('base64url'),
      ...changes.claims,
    })
      .setProtectedHeader({ typ: 'dpop+jwt', alg: 'ES256', jwk: key.jwk, ...changes.header })
      .sign(changes.signWith ?? key.privateKey);

  /** The request as Express gives it to the guard: the caller addressed this API by `host`. */
  const request = (dpop: string | string[] | undefined, method = 'GET', host = 'journey.ewp.example') =>
    ({ headers: { dpop }, method, protocol: 'https', path: PATH, get: (name: string) => (name === 'host' ? host : undefined) }) as unknown as Request;

  /** The error the check fails with (a check that passes fails the test). */
  const refusalOf = (check: Promise<void>): Promise<Error> =>
    check.then(() => { throw new Error('The proof was expected to be refused.'); }, (error: Error) => error);

  it('Given a proof of the token\'s own key for this request, when it is checked, then it is accepted', async () => {
    await expect(verifier.verify(request(await proof()), TOKEN, jkt)).resolves.toBeUndefined();
  });

  it('Given a proof addressed to the development proxy\'s host, when it is checked, then it is accepted (the caller\'s own address)', async () => {
    const viaProxy = await proof({ claims: { htu: `https://journey.dev.localhost:47110${PATH}` } });
    await expect(verifier.verify(request(viaProxy, 'GET', 'journey.dev.localhost:47110'), TOKEN, jkt)).resolves.toBeUndefined();
  });

  it('Given no DPoP header, when the token is presented, then it is refused', async () => {
    expect(await refusalOf(verifier.verify(request(undefined), TOKEN, jkt))).toBeInstanceOf(UnauthorizedException);
  });

  it('Given two DPoP headers, when they are checked, then they are refused', async () => {
    const twice = `${await proof()},${await proof()}`;
    expect(await refusalOf(verifier.verify(request(twice), TOKEN, jkt))).toBeInstanceOf(UnauthorizedException);
  });

  it.each([
    ['another method', async () => request(await proof({ claims: { htm: 'POST' } }))],
    ['another address', async () => request(await proof({ claims: { htu: `${PUBLIC_ORIGIN}/v1/journeys/elsewhere` } }))],
    ['another server', async () => request(await proof({ claims: { htu: `https://evil.example${PATH}` } }))],
    ['a minute ago', async () => request(await proof({ claims: { iat: Math.floor(Date.now() / 1000) - 60 } }))],
    ['a minute ahead', async () => request(await proof({ claims: { iat: Math.floor(Date.now() / 1000) + 60 } }))],
    ['another token (ath)', async () => request(await proof({ claims: { ath: createHash('sha256').update('another-token').digest('base64url') } }))],
    ['no jti', async () => request(await proof({ claims: { jti: undefined } }))],
    ['the wrong typ', async () => request(await proof({ header: { typ: 'JWT' } }))],
  ])('Given a proof made for %s, when it is checked, then it is refused', async (_, build) => {
    expect(await refusalOf(verifier.verify(await build(), TOKEN, jkt))).toBeInstanceOf(UnauthorizedException);
  });

  it('Given a valid proof of ANOTHER key (a copied token), when it is checked, then it is refused', async () => {
    const thief = newKey();
    const stolen = await proof({ header: { jwk: thief.jwk }, signWith: thief.privateKey });
    expect(await refusalOf(verifier.verify(request(stolen), TOKEN, jkt))).toBeInstanceOf(UnauthorizedException);
  });

  it('Given a proof whose header names the token\'s key but is signed by another, when it is checked, then it is refused', async () => {
    const forged = await proof({ signWith: newKey().privateKey });
    expect(await refusalOf(verifier.verify(request(forged), TOKEN, jkt))).toBeInstanceOf(UnauthorizedException);
  });

  it('Given a proof already accepted once, when it is presented again, then the replay is refused', async () => {
    const once = await proof();
    await verifier.verify(request(once), TOKEN, jkt);
    expect(await refusalOf(verifier.verify(request(once), TOKEN, jkt))).toBeInstanceOf(UnauthorizedException);
  });
});