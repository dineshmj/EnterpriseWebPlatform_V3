import { Inject, Injectable, UnauthorizedException } from '@nestjs/common';
import { Issuer, Client, generators, TokenSet } from 'openid-client';
import { Request, Response } from 'express';
import { createPrivateKey, createPublicKey, KeyObject, randomBytes, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { calculateJwkThumbprint, exportJWK, SignJWT } from 'jose';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { savePendingSignIn, takePendingSignIn } from './pending-sign-in';

// ┌─ :: SECURITY :: OIDC client: code + PKCE, fresh session, single-flight refresh ────────────────
// │ Why : A session ID that survives sign-in can be fixed by an attacker; two parallel refreshes
// │       with a rotating refresh token would revoke each other.
// │ How : Authorization Code + PKCE, pushed to the IDP first (PAR) (prompt=none in the frame,
// │       prompt=login + acr_values=mfa for step-up); the BFF proves itself with private_key_jwt
// │       (no client secret); the session ID is regenerated at sign-in; refreshes for the same
// │       token share one call.
// │ Ref : Features 2.1.1, 2.3.4, 2.1.5 | RFC 7636 | RFC 9126 (PAR) |
// │       OWASP Session Fixation | RFC 9700 (rotation)
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class OidcService {
  private clientPromise?: Promise<Client>;

  /**
   * Token refreshes in flight (and just finished), by the refresh token they used. Refresh
   * tokens are one-time (rotation): requests that notice the expiring token at the same moment
   * share ONE refresh instead of each spending the same refresh token (all but the first would
   * be refused). Only the request that refreshed saves the new tokens in the session.
   */
  private readonly refreshes = new Map<string, Promise<TokenSet>>();

  /** This BFF's private_key_jwt key: read at start-up - missing or unreadable = no start-up. */
  private readonly signingKey: KeyObject;
  private readonly signingKeyId: Promise<string>;

  constructor(@Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions) {
    this.signingKey = createPrivateKey({
      key: readFileSync(options.clientKeyPath),
      format: 'pem',
      passphrase: readFileSync(options.clientKeyPasswordPath, 'utf-8').trim(),
    });
    if (this.signingKey.asymmetricKeyType !== 'ec' || this.signingKey.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
      throw new Error('The client key must be an EC P-256 key (ES256).');
    }
    this.signingKeyId = exportJWK(createPublicKey(this.signingKey)).then(jwk => calculateJwkThumbprint(jwk));
  }

  // ┌─ :: SECURITY :: private_key_jwt: this BFF's client assertions ─────────────────────────────────
  // │ Why : A shared client secret travels with every token request and the IDP knows it too;
  // │       FAPI 2.0 asks a confidential client to prove itself with an asymmetric key instead.
  // │ How : Every call to the IDP gets a new assertion: iss = sub = the client ID, aud = the IDP's
  // │       issuer only, typ client-authentication+jwt (openid-client 5 cannot set it - so this
  // │       BFF signs it and openid-client sends no client authentication of its own), 60 s, a
  // │       fresh jti (the IDP takes each one once), ES256, kid = the RFC 7638 thumbprint
  // │       New-EwpClientKeys.mjs gave the public key.
  // │ Ref : Features 2.1.1 | FAPI 2.0 Security Profile §5.3.1 | RFC 7523, 7638 |
  // │       draft-ietf-oauth-rfc7523bis | Threat-Model R-10
  // └────────────────────────────────────────────────────────────────────────────────────────────────
  /** The client authentication for ONE call to the IDP (form parameters). */
  async clientAuthentication(): Promise<{ client_assertion_type: string; client_assertion: string }> {
    const client = await this.getClient();
    const now = Math.floor(Date.now() / 1000);
    const assertion = await new SignJWT({})
      .setProtectedHeader({ alg: 'ES256', typ: 'client-authentication+jwt', kid: await this.signingKeyId })
      .setIssuer(this.options.clientId)
      .setSubject(this.options.clientId)
      .setAudience(String(client.issuer.metadata.issuer))
      .setIssuedAt(now)
      .setNotBefore(now)
      .setExpirationTime(now + 60)
      .setJti(randomUUID())
      .sign(this.signingKey);
    return { client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer', client_assertion: assertion };
  }

  private getClient(): Promise<Client> {
    this.clientPromise ??= Issuer.discover(this.options.authority)
      .then(issuer =>
        new issuer.Client({
          client_id: this.options.clientId,
          // No client authentication by openid-client: every call adds clientAuthentication().
          token_endpoint_auth_method: 'none',
          redirect_uris: [this.options.callbackUrl],
          post_logout_redirect_uris: [this.options.postLogoutRedirectUri],
          response_types: ['code']
        }),
      )
      .catch(error => {
        // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
        // │ Do not cache a failed discovery: the next request retries it.
        // └──────────────────────────────────────────────────────────────────────────────────────
        this.clientPromise = undefined;
        throw error;
      });

    return this.clientPromise;
  }

  /**
   * silent: prompt=none in the Shell's frame. stepUp: prompt=login + acr_values=mfa - the IDP asks
   * the signed-in person only for a fresh authenticator code (a risky action), in a small window.
   */
  async authorizationUrl(
    res: Response,
    returnUrl: string,
    silent: boolean = false,
    stepUp: boolean = false,
  ): Promise<string> {
    const client = await this.getClient();
    const state = generators.state();
    const nonce = generators.nonce();
    const codeVerifier = generators.codeVerifier();
    const codeChallenge = generators.codeChallenge(codeVerifier);

    // In its own cookie, not the session (pending-sign-in.ts).
    savePendingSignIn(res, this.options.sessionKey, { state, nonce, codeVerifier, returnUrl });

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────────
    // │ PAR (RFC 9126, FAPI 2.0): the request is pushed to the IDP with a client assertion; the
    // │ browser is sent only client_id + a one-time request_uri (60 s) - nothing to read or change.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    const pushed = await client.pushedAuthorizationRequest({
      ...(await this.clientAuthentication()),
      scope: 'openid profile email roles organization offline_access customer-kyc.read customer-kyc.write',
      response_mode: 'query',
      state,
      nonce,
      code_challenge: codeChallenge,
      code_challenge_method: 'S256',
      ...(silent ? { prompt: 'none' } : {}),
      ...(stepUp ? { prompt: 'login', acr_values: 'mfa' } : {})
    });
    const url = new URL(String(client.issuer.metadata.authorization_endpoint));
    url.searchParams.set('client_id', this.options.clientId);
    url.searchParams.set('request_uri', pushed.request_uri);
    return url.href;
  }

  async handleCallback(req: Request, res: Response): Promise<string> {
    const pending = takePendingSignIn(req, res, this.options.sessionKey);
    if (!pending) throw new UnauthorizedException('No pending OIDC authorization request was found.');

    const client = await this.getClient();
    const tokenSet = await client.callback(
      this.options.callbackUrl,
      client.callbackParams(req),
      {
        state: pending.state,
        nonce: pending.nonce,
        code_verifier: pending.codeVerifier
      },
      { exchangeBody: await this.clientAuthentication() },
    );

    // Session fixation protection: the authenticated session always gets a
    // new session ID, never the one that existed before sign-in.
    await new Promise<void>((resolve, reject) =>
      // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
      // │ A NEW session ID at sign-in - never the one that existed before (session fixation).
      // └──────────────────────────────────────────────────────────────────────────────────────
      req.session.regenerate(error => (error ? reject(error) : resolve())),
    );

    this.storeTokenSet(req, tokenSet);
    await this.loadUser(req, client, tokenSet);

    // The anti-forgery token is issued with the new session, so later requests only read it
    // (a request that wrote it lazily could be overwritten by another, overlapping one).
    req.session.csrfToken = randomBytes(32).toString('base64url');

    // The IDP session (sid) is stored with the session row, so back-channel logout can end it
    // on whichever instance (session-store.ts).
    const idClaims = tokenSet.claims();
    req.session.idpSid = typeof idClaims.sid === 'string' ? idClaims.sid : undefined;

    return pending.returnUrl;
  }

  async refreshIfNeeded(req: Request): Promise<string> {
    if (!req.session.accessToken) throw new UnauthorizedException('No authenticated BFF session exists.');

    const expiresAt = req.session.accessTokenExpiresAt ?? 0;
    if (expiresAt > Date.now() + 60_000) return req.session.accessToken;
    if (!req.session.refreshToken) return req.session.accessToken;

    const usedRefreshToken = req.session.refreshToken;
    const shared = this.refreshes.get(usedRefreshToken);
    if (shared) {
      // Another request is refreshing (or just refreshed) with the same refresh token: use its
      // result, and leave the session alone - that request saves the new tokens.
      return (await shared).access_token!;
    }

    const refresh = this.getClient().then(async client =>
      client.refresh(usedRefreshToken, { exchangeBody: await this.clientAuthentication() }));
    this.refreshes.set(usedRefreshToken, refresh);
    // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    // │ A success is kept for a minute: a request that loaded the session just before the new
    // │ tokens were saved still holds the old refresh token, and must not spend it again.
    // │ A failure is forgotten at once, so the next request may try again.
    // └──────────────────────────────────────────────────────────────────────────────────────
    void refresh.then(
      () => setTimeout(() => this.refreshes.delete(usedRefreshToken), 60_000),
      () => this.refreshes.delete(usedRefreshToken),
    );

    const tokenSet = await refresh;
    this.storeTokenSet(req, tokenSet);
    return req.session.accessToken!;
  }

  /**
   * Ends the local session, revokes the refresh token and returns the IDP
   * end-session URL so the browser can also end the SSO session.
   */
  async logout(req: Request): Promise<string> {
    const idToken = req.session.idToken;
    const refreshToken = req.session.refreshToken;

    let endSessionUrl = this.options.postLogoutRedirectUri;

    try {
      const client = await this.getClient();

      if (refreshToken) {
        await this.clientAuthentication()
          .then(revokeBody => client.revoke(refreshToken, 'refresh_token', { revokeBody }))
          .catch(() => undefined);
      }

      endSessionUrl = client.endSessionUrl({
        id_token_hint: idToken,
        post_logout_redirect_uri: this.options.postLogoutRedirectUri
      });
    } finally {
      await this.clearSession(req);
    }

    return endSessionUrl;
  }

  async clearSession(req: Request): Promise<void> {
    await new Promise<void>(resolve => req.session.destroy(() => resolve()));
  }

  private storeTokenSet(req: Request, tokenSet: TokenSet): void {
    if (!tokenSet.access_token) throw new UnauthorizedException('IdentityServer did not return an access token.');
    req.session.accessToken = tokenSet.access_token;
    req.session.refreshToken = tokenSet.refresh_token ?? req.session.refreshToken;
    req.session.idToken = tokenSet.id_token ?? req.session.idToken;
    req.session.accessTokenExpiresAt = tokenSet.expires_at
      ? tokenSet.expires_at * 1000
      : Date.now() + 300_000;
  }

  private async loadUser(req: Request, client: Client, tokenSet: TokenSet): Promise<void> {
    // Identity-resource claims (roles, organization) are served by userinfo,
    // not embedded in the ID token.
    const idClaims = tokenSet.claims();
    const userInfo = await client.userinfo(tokenSet.access_token!);

    const role = userInfo.role ?? idClaims.role;
    const branch = userInfo.branch;

    req.session.user = {
      subject: String(idClaims.sub),
      name: typeof userInfo.name === 'string' ? userInfo.name : undefined,
      roles: Array.isArray(role) ? role.map(String) : typeof role === 'string' ? [role] : [],
      branch: typeof branch === 'string' && branch.trim() !== '' ? branch.trim().toUpperCase() : undefined
    };
  }
}