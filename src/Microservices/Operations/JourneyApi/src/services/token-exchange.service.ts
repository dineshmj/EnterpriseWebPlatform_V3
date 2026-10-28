import { createHash } from 'node:crypto';
import { ForbiddenException, Inject, Injectable, Logger, ServiceUnavailableException } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';

interface TokenResponse {
  access_token?: string;
  expires_in?: number;
  error?: string;
  error_description?: string;
}

// ┌─ :: SECURITY :: The second token exchange: one token per downstream scope ─────────────────────
// │ Why : Each domain API must see the person, and the chain of applications acting for them.
// │ How : The delegated token is exchanged again (RFC 8693) for each downstream scope (audit.read,
// │       payments.read); this API is added to the "act" chain; an IDP refusal is a 403, an IDP
// │       outage a 503.
// │ Ref : Features 2.1.2 | RFC 8693
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * OAuth 2.0 Token Exchange (RFC 8693): swaps the token this API received (the person, with the
 * web BFF acting) for one aimed at ONE downstream API - the person stays the subject, and this
 * API is added to the "act" chain. One token per downstream scope (accounts.read, compliance.write…),
 * so a token meant for the Accounts API can never be replayed at the Compliance API.
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
      // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
      // │ The form carries this API's client secret and the person's token: a redirect is
      // │ refused (an error, so "IDP unavailable"), never followed - a 307 would send the form on.
      // └──────────────────────────────────────────────────────────────────────────────────────
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
        redirect: 'error',
      });
    } catch (error) {
      // The reason stays in this log: the caller learns only that the IDP is unavailable.
      this.logger.warn(`Token exchange failed: ${(error as Error).message}`);
      throw new ServiceUnavailableException('The identity provider is unavailable. Try again shortly.');
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