import { Inject, Injectable, ServiceUnavailableException } from '@nestjs/common';
import { Request } from 'express';
import { OidcService } from '../auth/oidc.service';
import { KycBffOptions } from '../configuration/kyc-bff-options';

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
    const token = await this.exchange(subjectToken);

    req.session.documentsToken = {
      accessToken: token.access_token!,
      expiresAt: Date.now() + Math.max(30, token.expires_in ?? 300) * 1000,
    };
    return token.access_token!;
  }

  private async exchange(subjectToken: string): Promise<TokenResponse> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= 3; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 5000);

      try {
        const response = await fetch(`${this.options.authority}/connect/token`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
          body: new URLSearchParams({
            grant_type: TOKEN_EXCHANGE,
            client_id: this.options.clientId,
            client_secret: this.options.clientSecret,
            subject_token: subjectToken,
            subject_token_type: ACCESS_TOKEN_TYPE,
            scope: 'documents-management.read',
          }),
          signal: controller.signal,
        });

        const body = await response.text();

        if (!response.ok) {
          if (attempt < 3 && [408, 429, 500, 502, 503, 504].includes(response.status)) {
            await this.delay(250 * attempt);
            continue;
          }

          // The body names the OAuth error (e.g. invalid_grant), never a token.
          throw new Error(`The Documents Management token exchange failed with HTTP ${response.status}: ${body}`);
        }

        const token = JSON.parse(body) as TokenResponse;
        if (!token.access_token) {
          throw new Error('IdentityServer did not return an access_token.');
        }

        return token;
      } catch (error) {
        lastError = error;
        if (attempt < 3) {
          await this.delay(250 * attempt);
          continue;
        }
      } finally {
        clearTimeout(timer);
      }
    }

    throw new ServiceUnavailableException(
      lastError instanceof Error ? lastError.message : 'The Documents Management token exchange failed.',
    );
  }

  private delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
  }
}