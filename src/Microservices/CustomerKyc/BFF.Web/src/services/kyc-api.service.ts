import { Inject, Injectable, ServiceUnavailableException } from '@nestjs/common';
import { Request } from 'express';
import { OidcService } from '../auth/oidc.service';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { breakers } from '../resilience/breakers';
import { resilientFetch } from '../resilience/resilient-fetch';

@Injectable()
export class KycApiService {
  constructor(
    private readonly oidc: OidcService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  async getCases(req: Request, pageNumber: number, pageSize: number, status?: string, stage?: string): Promise<Response> {
    const accessToken = await this.oidc.refreshIfNeeded(req);
    const params = new URLSearchParams({ pageNumber: String(pageNumber), pageSize: String(pageSize) });
    if (status) params.set('status', status);
    if (stage) params.set('stage', stage);

    return this.get(`${this.options.apiBaseUrl}/v1/kyc/cases?${params.toString()}`, accessToken);
  }

  async getCase(req: Request, caseId: number): Promise<Response> {
    const accessToken = await this.oidc.refreshIfNeeded(req);
    return this.get(`${this.options.apiBaseUrl}/v1/kyc/cases/${caseId}`, accessToken);
  }

  async decideStage(
    req: Request,
    caseId: number,
    stage: 'identity-verification' | 'document-verification',
    action: 'approve' | 'reject',
    remarks: string,
  ): Promise<Response> {
    const accessToken = await this.oidc.refreshIfNeeded(req);
    return this.postOnce(
      `${this.options.apiBaseUrl}/v1/kyc/cases/${caseId}/${stage}/${action}`,
      accessToken,
      { decisionRemarks: remarks },
    );
  }

  /** GETs: 5-second attempts, up to three, through the KYC API's circuit breaker. */
  private async get(url: string, accessToken: string): Promise<Response> {
    try {
      return await resilientFetch(
        breakers.kycApi,
        url,
        { method: 'GET', headers: { Authorization: `Bearer ${accessToken}` } },
        { timeoutMs: 5000, attempts: 3 },
      );
    } catch (error) {
      if (error instanceof ServiceUnavailableException) throw error;
      throw new ServiceUnavailableException('The KYC service is temporarily unavailable. Please try again shortly.');
    }
  }

  /**
   * A decision is sent exactly once - never retried. If the first attempt reached the API and
   * committed but its answer was lost (timeout, 5xx on the way back), a retry would be refused
   * with a misleading 409 ("already decided"). The officer instead reloads the case and sees
   * whether the decision was applied (the same rule as the .NET BFFs: GETs retry, writes do not).
   * While the circuit is open the decision is not sent at all (ServiceUnavailableException).
   */
  private postOnce(url: string, accessToken: string, body: unknown): Promise<Response> {
    return resilientFetch(
      breakers.kycApi,
      url,
      {
        method: 'POST',
        headers: { Authorization: `Bearer ${accessToken}`, 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
      },
      { timeoutMs: 10_000 },
    );
  }
}