import { Inject, Injectable, ServiceUnavailableException } from '@nestjs/common';
import { Request } from 'express';
import { OidcService } from '../auth/oidc.service';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { tokenExchanges } from '../observability/metrics';
import { breakers } from '../resilience/breakers';
import { resilientFetch } from '../resilience/resilient-fetch';

interface TokenResponse {
  access_token?: string;
  expires_in?: number;
}

const TOKEN_EXCHANGE = 'urn:ietf:params:oauth:grant-type:token-exchange';
const ACCESS_TOKEN_TYPE = 'urn:ietf:params:oauth:token-type:access_token';

/**
 * OAuth 2.0 Token Exchange (RFC 8693): swaps the signed-in officer's access token for a
 * short-lived Documents Management token (read only). The officer stays the subject, so
 * Documents Management reads THEIR branch from the token, issued by the IDP; this BFF is
 * named as the acting client ("act"). The token is kept in the officer's own session only,
 * never shared between people, and renewed a minute before it expires.
 */
@Injectable()
export class DocumentsManagementTokenService {
  constructor(
    private readonly oidc: OidcService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  async getToken(req: Request): Promise<string> {
    const cached = req.session.documentsToken;
    if (cached && cached.expiresAt > Date.now() + 60_000) {
      return cached.accessToken;
    }

    const subjectToken = await this.oidc.refreshIfNeeded(req);
    let token: TokenResponse;
    try {
      token = await this.exchange(subjectToken);
      tokenExchanges.inc({ outcome: 'issued' });
    } catch (error) {
      tokenExchanges.inc({ outcome: 'failed' });
      throw error;
    }

    req.session.documentsToken = {
      accessToken: token.access_token!,
      expiresAt: Date.now() + Math.max(30, token.expires_in ?? 300) * 1000,
    };
    return token.access_token!;
  }

  /**
   * One POST to the token endpoint, through the IDP's circuit breaker. Not retried: a lost
   * answer only costs the officer a reload, and a failing IDP must not be hammered.
   */
  private async exchange(subjectToken: string): Promise<TokenResponse> {
    let response: globalThis.Response;
    try {
      response = await resilientFetch(
        breakers.identityProvider,
        `${this.options.authority}/connect/token`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams({
            grant_type: TOKEN_EXCHANGE,
            client_id: this.options.clientId,
            client_secret: this.options.clientSecret,
            subject_token: subjectToken,
            subject_token_type: ACCESS_TOKEN_TYPE,
            scope: 'documents-management.read',
          }).toString(),
        },
        { timeoutMs: 5000 },
      );
    } catch (error) {
      if (error instanceof ServiceUnavailableException) throw error;
      throw new ServiceUnavailableException('Documents cannot be shown right now: the sign-in service did not answer. Please try again shortly.');
    }

    const body = await response.text();
    if (!response.ok) {
      // The body names the OAuth error (e.g. invalid_grant), never a token.
      throw new ServiceUnavailableException(`The Documents Management token exchange failed with HTTP ${response.status}: ${body}`);
    }

    const token = JSON.parse(body) as TokenResponse;
    if (!token.access_token) {
      throw new ServiceUnavailableException('IdentityServer did not return an access_token.');
    }
    return token;
  }
}