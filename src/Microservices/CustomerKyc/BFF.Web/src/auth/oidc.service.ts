import { Inject, Injectable, UnauthorizedException } from '@nestjs/common';
import { Issuer, Client, generators, TokenSet } from 'openid-client';
import { Request } from 'express';
import { KycBffOptions } from '../configuration/kyc-bff-options';

@Injectable()
export class OidcService {
  private clientPromise?: Promise<Client>;

  constructor(@Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions) {}

  private getClient(): Promise<Client> {
    this.clientPromise ??= Issuer.discover(this.options.authority).then(issuer =>
      new issuer.Client({
        client_id: this.options.clientId,
        client_secret: this.options.clientSecret,
        redirect_uris: [this.options.callbackUrl],
        response_types: ['code']
      }),
    );

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
      scope: 'openid profile email roles offline_access customer-kyc.read customer-kyc.write',
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

    req.session.oidc = undefined;
    this.storeTokenSet(req, tokenSet);
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

  private storeTokenSet(req: Request, tokenSet: TokenSet): void {
    if (!tokenSet.access_token) throw new UnauthorizedException('IdentityServer did not return an access token.');
    req.session.accessToken = tokenSet.access_token;
    req.session.refreshToken = tokenSet.refresh_token ?? req.session.refreshToken;
    req.session.idToken = tokenSet.id_token ?? req.session.idToken;
    req.session.accessTokenExpiresAt = tokenSet.expires_at
      ? tokenSet.expires_at * 1000
      : Date.now() + 300_000;

    const claims = tokenSet.claims();
    req.session.user = {
      subject: String(claims?.sub ?? ''),
      name: typeof claims?.name === 'string' ? claims.name : undefined,
      roles: Array.isArray(claims?.role) ? claims.role.map(String) : typeof claims?.role === 'string' ? [claims.role] : []
    };
  }

  clearSession(req: Request): void {
    req.session.destroy(() => undefined);
  }
}
