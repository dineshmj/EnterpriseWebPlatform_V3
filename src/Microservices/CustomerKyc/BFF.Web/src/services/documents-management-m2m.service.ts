import { Inject, Injectable, ServiceUnavailableException } from '@nestjs/common';
import { KycBffOptions } from '../configuration/kyc-bff-options';

interface TokenResponse {
  access_token?: string;
  expires_in?: number;
}

@Injectable()
export class DocumentsManagementM2mService {
  private cachedAccessToken?: string;
  private cachedExpiresAt = 0;
  private tokenPromise?: Promise<string>;

  constructor(
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  async getAccessToken(): Promise<string> {
    if (this.cachedAccessToken && this.cachedExpiresAt > Date.now() + 60_000) {
      return this.cachedAccessToken;
    }

    this.tokenPromise ??= this.requestToken().finally(() => {
      this.tokenPromise = undefined;
    });

    return this.tokenPromise;
  }

  private async requestToken(): Promise<string> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= 3; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 5000);

      try {
        const response = await fetch(
          `${this.options.documentsManagementIdpAuthority}/connect/token`,
          {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({
              grant_type: 'client_credentials',
              client_id: this.options.documentsManagementM2mClientId,
              client_secret: this.options.documentsManagementM2mClientSecret,
              scope: 'documents-management.read',
            }),
            signal: controller.signal,
          },
        );

        const body = await response.text();

        if (!response.ok) {
          if (attempt < 3 && [408, 429, 500, 502, 503, 504].includes(response.status)) {
            await this.delay(250 * attempt);
            continue;
          }

          throw new Error(
            `Documents Management M2M token request failed with HTTP ${response.status}: ${body}`,
          );
        }

        const token = JSON.parse(body) as TokenResponse;

        if (!token.access_token) {
          throw new Error('IdentityServer did not return an access_token.');
        }

        const expiresIn = Math.max(30, token.expires_in ?? 300);
        this.cachedAccessToken = token.access_token;
        this.cachedExpiresAt = Date.now() + expiresIn * 1000;

        return token.access_token;
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
      lastError instanceof Error
        ? lastError.message
        : 'Documents Management M2M authorization failed.',
    );
  }

  private delay(milliseconds: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
  }
}
