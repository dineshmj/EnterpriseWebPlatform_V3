import { createHash } from 'node:crypto';
import { ForbiddenException, Inject, Injectable, Logger, ServiceUnavailableException } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';

interface TokenResponse {
  access_token?: string;
  expires_in?: number;
  error?: string;
  error_description?: string;
}

/**
 * OAuth 2.0 Token Exchange (RFC 8693): swaps the token this API received (the person, with the
 * web BFF acting) for one aimed at ONE downstream API - the person stays the subject, and this
 * API is added to the "act" chain. One token per downstream scope (audit.read, payments.read),
 * so a token meant for the Audit API can never be replayed at the Payments API.
 * Exchanged tokens are cached until a minute before they expire.
 */
@Injectable()
export class TokenExchangeService {
  private readonly logger = new Logger(TokenExchangeService.name);
  private readonly cache = new Map<string, { token: string; expiresAt: number }>();

  constructor(@Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions) {}

  async exchange(subjectToken: string, scope: string): Promise<string> {
    const key = `${scope}|${createHash('sha256').update(subjectToken).digest('hex')}`;
    const cached = this.cache.get(key);
    if (cached && cached.expiresAt > Date.now() + 60_000) {
      return cached.token;
    }

    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 5000);
    let response: Response;
    try {
      response = await fetch(`${this.options.authority}/connect/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
          grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
          client_id: this.options.clientId,
          client_secret: this.options.clientSecret,
          subject_token: subjectToken,
          subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
          scope,
        }),
        signal: controller.signal,
      });
    } catch (error) {
      throw new ServiceUnavailableException(`The identity provider is unavailable: ${(error as Error).message}`);
    } finally {
      clearTimeout(timer);
    }

    const body = (await response.json().catch(() => ({}))) as TokenResponse;
    if (!response.ok || !body.access_token) {
      this.logger.warn(`Token exchange for ${scope} refused: HTTP ${response.status} ${body.error ?? ''} ${body.error_description ?? ''}`);
      if (response.status === 400) {
        throw new ForbiddenException('The identity provider refused to delegate this request.');
      }
      throw new ServiceUnavailableException('The identity provider could not exchange the token.');
    }

    this.cache.set(key, { token: body.access_token, expiresAt: Date.now() + Math.max(30, body.expires_in ?? 300) * 1000 });
    if (this.cache.size > 500) {
      for (const [k, v] of this.cache) if (v.expiresAt <= Date.now()) this.cache.delete(k);
    }
    return body.access_token;
  }
}