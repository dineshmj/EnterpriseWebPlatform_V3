import { Inject, Injectable, UnauthorizedException } from '@nestjs/common';
import { Issuer, Client, generators, TokenSet } from 'openid-client';
import { Request } from 'express';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { registerSession } from './session-registry';

@Injectable()
export class OidcService {
  private clientPromise?: Promise<Client>;

  constructor(@Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions) {}

  private getClient(): Promise<Client> {
    this.clientPromise ??= Issuer.discover(this.options.authority)
      .then(issuer =>
        new issuer.Client({
          client_id: this.options.clientId,
          client_secret: this.options.clientSecret,
          redirect_uris: [this.options.callbackUrl],
          post_logout_redirect_uris: [this.options.postLogoutRedirectUri],
          response_types: ['code']
        }),
      )
      .catch(error => {
        // Do not cache a failed discovery: the next request retries it.
        this.clientPromise = undefined;
        throw error;
      });

    return this.clientPromise;
  }

  async authorizationUrl(
    req: Request,
    returnUrl: string,
    silent: boolean = false,
  ): Promise<string> {
    const client = await this.getClient();
    const state = generators.state();
    const nonce = generators.nonce();
    const codeVerifier = generators.codeVerifier();
    const codeChallenge = generators.codeChallenge(codeVerifier);

    req.session.oidc = { state, nonce, codeVerifier, returnUrl };

    return client.authorizationUrl({
      scope: 'openid profile email roles organization offline_access customer-kyc.read customer-kyc.write',
      response_mode: 'query',
      state,
      nonce,
      code_challenge: codeChallenge,
      code_challenge_method: 'S256',
      ...(silent ? { prompt: 'none' } : {})
    });
  }

  async handleCallback(req: Request): Promise<string> {
    const pending = req.session.oidc;
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
    );

    // Session fixation protection: the authenticated session always gets a
    // new session ID, never the one that existed before sign-in.
    await new Promise<void>((resolve, reject) =>
      req.session.regenerate(error => (error ? reject(error) : resolve())),
    );

    this.storeTokenSet(req, tokenSet);
    await this.loadUser(req, client, tokenSet);

    // Index the session by the IDP session (sid) so back-channel logout can end it.
    const idClaims = tokenSet.claims();
    registerSession(req.sessionID, String(idClaims.sub), typeof idClaims.sid === 'string' ? idClaims.sid : undefined);

    return pending.returnUrl;
  }

  async refreshIfNeeded(req: Request): Promise<string> {
    if (!req.session.accessToken) throw new UnauthorizedException('No authenticated BFF session exists.');

    const expiresAt = req.session.accessTokenExpiresAt ?? 0;
    if (expiresAt > Date.now() + 60_000) return req.session.accessToken;
    if (!req.session.refreshToken) return req.session.accessToken;

    const client = await this.getClient();
    const tokenSet = await client.refresh(req.session.refreshToken);
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
        await client.revoke(refreshToken, 'refresh_token').catch(() => undefined);
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