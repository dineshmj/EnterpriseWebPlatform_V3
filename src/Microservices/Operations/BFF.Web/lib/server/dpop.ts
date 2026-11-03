import { createHash, createPrivateKey, createPublicKey, KeyObject, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { calculateJwkThumbprint, exportJWK, JWK, SignJWT } from 'jose';
import { config } from './config';

/** This BFF's DPoP key: the private key (signs proofs), its public JWK and RFC 7638 thumbprint. */
export interface DPoPKey {
  privateKey: KeyObject;
  publicJwk: JWK;
  jkt: string;
}

let keyPromise: Promise<DPoPKey> | undefined;

// ┌─ :: SECURITY :: DPoP: every token this BFF holds is bound to its own key ──────────────────────
// │ Why : A bearer token copied from a log, a memory dump or the session table could be replayed
// │       by anyone; FAPI 2.0 asks for sender-constrained tokens.
// │ How : One EC P-256 key per app, read from a file (encrypted PKCS#8 PEM + password file) - not
// │       the client key: "I hold this token" and "I am this client" stay separate. The IDP binds
// │       every token to it (cnf.jkt) at sign-in (dpop_jkt in PAR + proof at code redemption), on
// │       refresh and on the exchange; every call to the Journey API carries "DPoP <token>" and a
// │       new proof for exactly that method and address, with the token's hash (ath).
// │ Ref : Features 2.1.1 | RFC 9449 | FAPI 2.0 Security Profile §5.3.1 | Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
/** The DPoP key, read once (retried after a failure). */
export function dpopKey(): Promise<DPoPKey> {
  keyPromise ??= loadKey().catch((error) => {
    keyPromise = undefined;
    throw error;
  });
  return keyPromise;
}

/** The headers for ONE call with a DPoP-bound token: the "DPoP" scheme and a fresh proof. */
export async function dpopAuthorization(accessToken: string, method: string, url: string): Promise<{ Authorization: string; DPoP: string }> {
  const { privateKey, publicJwk } = await dpopKey();
  const target = new URL(url);
  // SEC: htu is the address without query or fragment (RFC 9449 §4.2); ath ties the proof to this token.
  const proof = await new SignJWT({
    htm: method.toUpperCase(),
    htu: `${target.origin}${target.pathname}`,
    ath: createHash('sha256').update(accessToken).digest('base64url'),
  })
    .setProtectedHeader({ typ: 'dpop+jwt', alg: 'ES256', jwk: publicJwk })
    .setIssuedAt()
    .setJti(randomUUID())
    .sign(privateKey);
  return { Authorization: `DPoP ${accessToken}`, DPoP: proof };
}

async function loadKey(): Promise<DPoPKey> {
  const privateKey = createPrivateKey({
    key: readFileSync(config.dpopKeyPath),
    format: 'pem',
    passphrase: readFileSync(config.dpopKeyPasswordPath, 'utf-8').trim(),
  });
  if (privateKey.asymmetricKeyType !== 'ec' || privateKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
    throw new Error('The DPoP key must be an EC P-256 key (ES256).');
  }
  const { kty, crv, x, y } = await exportJWK(createPublicKey(privateKey));
  const publicJwk: JWK = { kty, crv, x, y };   // the public members only - never "d"
  return { privateKey, publicJwk, jkt: await calculateJwkThumbprint(publicJwk) };
}