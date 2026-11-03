import { Inject, Injectable } from '@nestjs/common';
import { createHash, createPrivateKey, createPublicKey, KeyObject, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { calculateJwkThumbprint, JWK, SignJWT } from 'jose';
import { KycBffOptions } from '../configuration/kyc-bff-options';

// ┌─ :: SECURITY :: DPoP: every token this BFF holds is bound to its own key ──────────────────────
// │ Why : A bearer token copied from a log, a memory dump or the session table could be replayed
// │       by anyone; FAPI 2.0 asks for sender-constrained tokens.
// │ How : One EC P-256 key for this app, read at start-up from a file (encrypted PKCS#8 PEM +
// │       password file; missing or unreadable = no start-up) - not the client key: "I hold this
// │       token" and "I am this client" stay separate. The IDP binds every token to it (cnf.jkt)
// │       at sign-in (dpop_jkt in PAR + proof at code redemption), on refresh and on the exchange;
// │       every call to the KYC API or Documents Management carries "DPoP <token>" and a
// │       new proof for exactly that method and address, with the token's hash (ath).
// │ Ref : Features 2.1.1 | RFC 9449 | FAPI 2.0 Security Profile §5.3.1 | Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class DPoPKeyService {
  /** Signs the proofs; openid-client takes it as its DPoP option. */
  readonly privateKey: KeyObject;
  private readonly publicJwk: JWK;
  /** RFC 7638 thumbprint of the public key: dpop_jkt in PAR, cnf.jkt in every token. */
  readonly jkt: Promise<string>;

  constructor(@Inject('KYC_BFF_OPTIONS') options: KycBffOptions) {
    this.privateKey = createPrivateKey({
      key: readFileSync(options.dpopKeyPath),
      format: 'pem',
      passphrase: readFileSync(options.dpopKeyPasswordPath, 'utf-8').trim(),
    });
    if (this.privateKey.asymmetricKeyType !== 'ec' || this.privateKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
      throw new Error('The DPoP key must be an EC P-256 key (ES256).');
    }
    // The public members only - never "d".
    const { kty, crv, x, y } = createPublicKey(this.privateKey).export({ format: 'jwk' });
    this.publicJwk = { kty, crv, x, y };
    this.jkt = calculateJwkThumbprint(this.publicJwk);
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