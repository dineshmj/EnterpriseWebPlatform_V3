import { createHash, createPrivateKey, createPublicKey, KeyObject, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { calculateJwkThumbprint, JWK, SignJWT } from 'jose';

// ┌─ :: SECURITY :: DPoP: the tokens this API obtains are bound to its own key ────────────────────
// │ Why : The tokens this API gets by exchange could be replayed by anyone who copied them from
// │       its memory or logs; FAPI 2.0 asks for sender-constrained tokens.
// │ How : One EC P-256 key for this API, read at start-up from a file (encrypted PKCS#8 PEM +
// │       password file; missing or unreadable = no start-up) - a key of its own, not the BFF's.
// │       The exchange carries a proof (the IDP binds the new token to the key: cnf.jkt), and
// │       every Domain API call carries "DPoP <token>" plus a new proof for exactly that method
// │       and address, with the token's hash (ath).
// │ Ref : Features 2.1.1 | RFC 9449 | FAPI 2.0 Security Profile §5.3.1 | Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
export class DPoPKeyService {
  private readonly publicJwk: JWK;

  constructor(private readonly privateKey: KeyObject) {
    if (privateKey.asymmetricKeyType !== 'ec' || privateKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
      throw new Error('The DPoP key must be an EC P-256 key (ES256).');
    }
    // The public members only - never "d".
    const { kty, crv, x, y } = createPublicKey(privateKey).export({ format: 'jwk' });
    this.publicJwk = { kty, crv, x, y };
  }

  /** Reads the key file (encrypted PKCS#8 PEM) with the password from its own file. */
  static load(keyPath: string, passwordPath: string): DPoPKeyService {
    return new DPoPKeyService(createPrivateKey({
      key: readFileSync(keyPath),
      format: 'pem',
      passphrase: readFileSync(passwordPath, 'utf-8').trim(),
    }));
  }

  /** RFC 7638 thumbprint of the public key: the cnf.jkt of every token bound to it. */
  thumbprint(): Promise<string> {
    return calculateJwkThumbprint(this.publicJwk);
  }

  /** A proof for ONE request: its method and address (no query), a new jti, now, and the token's hash. */
  async proof(method: string, url: string, accessToken?: string): Promise<string> {
    const target = new URL(url);
    return new SignJWT({
      htm: method.toUpperCase(),
      htu: `${target.origin}${target.pathname}`,
      ...(accessToken ? { ath: createHash('sha256').update(accessToken).digest('base64url') } : {}),
    })
      .setProtectedHeader({ typ: 'dpop+jwt', alg: 'ES256', jwk: this.publicJwk })
      .setIssuedAt()
      .setJti(randomUUID())
      .sign(this.privateKey);
  }

  /** The headers for ONE call with a DPoP-bound token: the "DPoP" scheme and a fresh proof. */
  async authorization(accessToken: string, method: string, url: string): Promise<{ Authorization: string; DPoP: string }> {
    return { Authorization: `DPoP ${accessToken}`, DPoP: await this.proof(method, url, accessToken) };
  }
}