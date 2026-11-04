import { createPrivateKey, createPublicKey, KeyObject, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { calculateJwkThumbprint, JWK, SignJWT } from 'jose';

// ┌─ :: SECURITY :: private_key_jwt: this API proves itself with its own key ──────────────────────
// │ Why : A shared client secret is known to the IDP too and travels with every token exchange;
// │       FAPI 2.0 asks a confidential client to prove itself with an asymmetric key instead.
// │ How : One EC P-256 key for this API's client, read at start-up from a file (encrypted PKCS#8
// │       PEM + password file; missing or unreadable = no start-up) - not the DPoP key: "I am this
// │       client" and "I hold this token" stay apart (the module refuses the same key twice). Every
// │       call to the IDP gets a new assertion: iss = sub = the client ID, aud = the IDP's issuer
// │       only (read once from its discovery document), typ client-authentication+jwt, 60 s, a
// │       fresh jti (the IDP takes each one once), ES256, kid = the RFC 7638 thumbprint
// │       New-EwpClientKeys.mjs gave the public key. The IDP registers only the public key.
// │ Ref : Features 2.1.2 | FAPI 2.0 Security Profile §5.3.1 | RFC 7523, 7638 | Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
export class ClientKeyService {
  private readonly publicJwk: JWK;
  private issuer?: Promise<string>;

  constructor(
    private readonly privateKey: KeyObject,
    private readonly clientId: string,
    private readonly authority: string,
  ) {
    if (privateKey.asymmetricKeyType !== 'ec' || privateKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
      throw new Error('The client key must be an EC P-256 key (ES256).');
    }
    // The public members only - never "d"; the thumbprint is computed when asked (no async work here).
    const { kty, crv, x, y } = createPublicKey(privateKey).export({ format: 'jwk' });
    this.publicJwk = { kty, crv, x, y };
  }

  /** Reads the key file (encrypted PKCS#8 PEM) with the password from its own file. */
  static load(keyPath: string, passwordPath: string, clientId: string, authority: string): ClientKeyService {
    return new ClientKeyService(
      createPrivateKey({ key: readFileSync(keyPath), format: 'pem', passphrase: readFileSync(passwordPath, 'utf-8').trim() }),
      clientId,
      authority,
    );
  }

  /** RFC 7638 thumbprint of the public key: its kid at the IDP. */
  thumbprint(): Promise<string> {
    return calculateJwkThumbprint(this.publicJwk);
  }

  /** The client authentication for ONE call to the IDP (form parameters). */
  async clientAuthentication(): Promise<{ client_assertion_type: string; client_assertion: string }> {
    const now = Math.floor(Date.now() / 1000);
    const assertion = await new SignJWT({})
      .setProtectedHeader({ alg: 'ES256', typ: 'client-authentication+jwt', kid: await this.thumbprint() })
      .setIssuer(this.clientId)
      .setSubject(this.clientId)
      .setAudience(await this.issuerOf())
      .setIssuedAt(now)
      .setNotBefore(now)
      .setExpirationTime(now + 60)
      .setJti(randomUUID())
      .sign(this.privateKey);
    return { client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer', client_assertion: assertion };
  }

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ The assertion's audience is the IDP's issuer EXACTLY as it states it (the IDP checks it
  // │ strictly); a failed discovery is not kept, so the next call asks again.
  // └──────────────────────────────────────────────────────────────────────────────────────
  private issuerOf(): Promise<string> {
    this.issuer ??= fetch(`${this.authority}/.well-known/openid-configuration`, { signal: AbortSignal.timeout(5000), redirect: 'error' })
      .then(async (response) => {
        const issuer = response.ok ? ((await response.json()) as { issuer?: unknown }).issuer : undefined;
        if (typeof issuer !== 'string' || issuer.length === 0) {
          throw new Error(`IDP discovery named no issuer (HTTP ${response.status}).`);
        }
        return issuer;
      })
      .catch((error: unknown) => {
        this.issuer = undefined;
        throw error;
      });
    return this.issuer;
  }
}