import { BadRequestException, ConflictException, ForbiddenException, Inject, Injectable, Logger, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import { DPoPKeyService } from '../auth/dpop-key.service';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { TokenExchangeService } from '../services/token-exchange.service';

/** The two reason codes - never free text, so no personal data travels with a request. */
export const erasureReasons = ['CUSTOMER_REQUEST', 'RETENTION_ENDED'] as const;
export type ErasureReason = (typeof erasureReasons)[number];

/** An erasure request as the Customer Onboarding API lists it: identifiers, LAN IDs and outcome only. */
export interface ErasureRequest {
  erasureRequestId: number;
  customerNumber: string;
  reasonCode: ErasureReason;
  status: 'PENDING' | 'ERASED' | 'REJECTED' | 'EXPIRED';
  requestedBy: string;
  requestedAt: string;
  expiresAt: string;
  decidedBy: string | null;
  decidedAt: string | null;
  decisionRemarks: string | null;
  /** False for the requester (four eyes); the Customer Onboarding API refuses their decision anyway. */
  mayDecide: boolean;
}

/** The retention the Customer Onboarding API applies, and the law's (days). */
export interface ErasurePolicy {
  retentionDays: number;
  legalRetentionDays: number;
  isShorterThanLegal: boolean;
}

export interface ErasureResult {
  erasureRequestId: number;
  customerNumber: string;
  status: string;
}

// ┌─ :: ARCHITECTURE + SECURITY :: Personal-data erasure, for the operator ────────────────────────
// │ Why : Erasing a customer cannot be undone: the Journey API must never act as itself, and must
// │       pass the Customer Onboarding API's refusals (not eligible, four eyes, already decided)
// │       to the operator in their own words - and nothing else of its internals.
// │ How : For each call the operator's token is exchanged (RFC 8693) for customer-onboarding.read
// │       or .write; the Customer Onboarding API checks the permission, MFA freshness, that this
// │       API is the acting client, and decides eligibility and four eyes in its aggregates.
// │ Ref : doc/Crypto-Shredding.md | RFC 8693 | OWASP API5 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class PersonalDataErasureService {
  private readonly logger = new Logger(PersonalDataErasureService.name);

  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    private readonly exchange: TokenExchangeService,
    private readonly dpop: DPoPKeyService,
  ) {}

  async list(subjectToken: string): Promise<ErasureRequest[]> {
    const token = await this.exchange.exchange(subjectToken, 'customer-onboarding.read');
    return (await this.call(token, 'GET', '')) as ErasureRequest[];
  }

  async policy(subjectToken: string): Promise<ErasurePolicy> {
    const token = await this.exchange.exchange(subjectToken, 'customer-onboarding.read');
    return (await this.call(token, 'GET', '/policy')) as ErasurePolicy;
  }

  async request(subjectToken: string, customerNumber: string, reasonCode: ErasureReason): Promise<ErasureResult> {
    const token = await this.exchange.exchange(subjectToken, 'customer-onboarding.write');
    return (await this.call(token, 'POST', '', { customerNumber, reasonCode })) as ErasureResult;
  }

  async decide(subjectToken: string, erasureRequestId: number, approve: boolean, remarks?: string): Promise<ErasureResult> {
    const token = await this.exchange.exchange(subjectToken, 'customer-onboarding.write');
    return (await this.call(token, 'POST', `/${erasureRequestId}/${approve ? 'approve' : 'reject'}`, approve ? {} : { remarks })) as ErasureResult;
  }

  private async call(token: string, method: 'GET' | 'POST', path: string, body?: unknown): Promise<unknown> {
    const url = `${this.options.customerOnboardingApiBaseUrl}/v1/customers/personal-data-erasures${path}`;
    let response: Response;
    try {
      // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
      // │ A domain API never redirects: a redirect is refused, never followed - the exchanged
      // │ token goes to the configured API and nowhere else. "DPoP <token>" + a new proof.
      // └──────────────────────────────────────────────────────────────────────────────────────
      response = await fetch(url, {
        method,
        headers: { ...(await this.dpop.authorization(token, method, url)), ...(body ? { 'Content-Type': 'application/json' } : {}) },
        body: body ? JSON.stringify(body) : undefined,
        signal: AbortSignal.timeout(10_000),
        redirect: 'error',
      });
    } catch (error) {
      this.logger.warn(`${method} personal-data erasure failed: ${(error as Error).message}`);
      throw new ServiceUnavailableException('The Customer Onboarding service is unavailable. Try again shortly.');
    }

    const payload = (await response.json().catch(() => ({}))) as { detail?: string; code?: string };
    if (response.ok) return payload;
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Translate the Domain API's answer: its own refusal texts (not eligible, four eyes,
    // │ already decided, MFA) pass; anything unexpected becomes "unavailable" - no internals.
    // └──────────────────────────────────────────────────────────────────────────────────────
    switch (response.status) {
      case 400:
      case 422: throw new BadRequestException(payload.detail ?? 'The request was not valid.');
      case 401:
      case 403:
        if (payload.code === 'mfa_required') throw new ForbiddenException({ message: payload.detail, code: 'mfa_required' });
        if (payload.code === 'separation_of_duties') throw new ForbiddenException({ message: payload.detail, code: 'separation_of_duties' });
        throw new ForbiddenException('You are not allowed to do this.');
      case 404: throw new NotFoundException('No such customer or erasure request.');
      case 409: throw new ConflictException(payload.detail ?? 'Not possible now.');
      default: throw new ServiceUnavailableException('The Customer Onboarding service could not answer. Try again shortly.');
    }
  }
}