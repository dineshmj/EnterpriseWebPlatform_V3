import { BadRequestException, ConflictException, ForbiddenException, Inject, Injectable, Logger, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { TokenExchangeService } from '../services/token-exchange.service';

/** The contexts whose parked Outbox messages operations may see and resend. */
export const recoveryContexts = ['accounts', 'compliance', 'partner-products'] as const;
export type RecoveryContext = (typeof recoveryContexts)[number];

/** A parked message as the Domain API lists it - no payload. */
export interface ParkedMessage {
  messageId: string;
  eventType: string;
  aggregateType: string;
  aggregateId: string;
  occurredAt: string;
  attemptCount: number;
  lastAttemptAt: string | null;
  lastError: string | null;
  heldBack: number;
  redriveCount: number;
  lastRedrivenAt: string | null;
  lastRedrivenBy: string | null;
  lastRedriveReason: string | null;
}

export interface ContextParkedMessages {
  context: RecoveryContext;
  /** False when the context could not be asked (its API down): the others are still shown. */
  available: boolean;
  messages: ParkedMessage[];
}

// ┌─ :: ARCHITECTURE + SECURITY :: Parked messages of other contexts, for the operator ────────────
// │ Why : Operations need one place to see and resend parked Outbox messages; the Journey API must
// │       not act as itself - only for the signed-in operator.
// │ How : For each call the operator's token is exchanged (RFC 8693) for that context's read or
// │       write scope; the Domain API checks the operator's permission, MFA freshness and that this
// │       API is the acting client. Downstream errors are translated, never passed through raw.
// │ Ref : Features 1.6.2, 2.1.2 | RFC 8693 | Threat-Model R-5 | OWASP API5
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class ParkedMessagesService {
  private readonly logger = new Logger(ParkedMessagesService.name);

  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    private readonly exchange: TokenExchangeService,
  ) {}

  async listAll(subjectToken: string): Promise<ContextParkedMessages[]> {
    return Promise.all(recoveryContexts.map(async (context) => {
      try {
        const token = await this.exchange.exchange(subjectToken, `${context}.read`);
        const messages = (await this.call(context, token, 'GET', '/parked')) as ParkedMessage[];
        return { context, available: true, messages };
      } catch (error) {
        // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
        // │ If one context cannot be asked, show the others and mark it unavailable - one API
        // │ down must not hide every parked message. A refusal (403) is not hidden this way.
        // └──────────────────────────────────────────────────────────────────────────────────────
        if (error instanceof ForbiddenException) throw error;
        this.logger.warn(`Parked messages of ${context} unavailable: ${(error as Error).message}`);
        return { context, available: false, messages: [] };
      }
    }));
  }

  async resend(subjectToken: string, context: RecoveryContext, messageId: string, reason: string): Promise<unknown> {
    const token = await this.exchange.exchange(subjectToken, `${context}.write`);
    return this.call(context, token, 'POST', `/${encodeURIComponent(messageId)}/resend`, { reason });
  }

  private async call(context: RecoveryContext, token: string, method: 'GET' | 'POST', path: string, body?: unknown): Promise<unknown> {
    const base: Record<RecoveryContext, string> = {
      accounts: this.options.accountsApiBaseUrl,
      compliance: this.options.complianceApiBaseUrl,
      'partner-products': this.options.partnerProductsApiBaseUrl,
    };
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10_000);
    let response: Response;
    try {
      response = await fetch(`${base[context]}/v1/${context}/outbox${path}`, {
        method,
        headers: { Authorization: `Bearer ${token}`, ...(body ? { 'Content-Type': 'application/json' } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        signal: controller.signal,
      });
    } catch (error) {
      this.logger.warn(`${method} ${context} outbox failed: ${(error as Error).message}`);
      throw new ServiceUnavailableException(`The ${context} service is unavailable. Try again shortly.`);
    } finally {
      clearTimeout(timer);
    }

    const payload = (await response.json().catch(() => ({}))) as { detail?: string; code?: string };
    if (response.ok) return payload;
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Translate the Domain API's answer: our own refusal texts (validation, conflict, MFA)
    // │ pass; anything unexpected becomes a plain "unavailable" - no internals reach the screen.
    // └──────────────────────────────────────────────────────────────────────────────────────
    switch (response.status) {
      case 400: throw new BadRequestException(payload.detail ?? 'The request was not valid.');
      case 401:
      case 403:
        if (payload.code === 'mfa_required') throw new ForbiddenException({ message: payload.detail, code: 'mfa_required' });
        throw new ForbiddenException('You are not allowed to do this.');
      case 404: throw new NotFoundException('The message does not exist.');
      case 409: throw new ConflictException(payload.detail ?? 'Nothing to resend.');
      default: throw new ServiceUnavailableException(`The ${context} service could not answer. Try again shortly.`);
    }
  }
}