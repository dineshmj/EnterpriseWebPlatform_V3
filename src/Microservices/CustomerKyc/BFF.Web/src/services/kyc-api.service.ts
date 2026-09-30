import { Inject, Injectable } from '@nestjs/common';
import { Request } from 'express';
import { OidcService } from '../auth/oidc.service';
import { KycBffOptions } from '../configuration/kyc-bff-options';

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

    return this.getWithRetry(
      `${this.options.apiBaseUrl}/v1/kyc/cases?${params.toString()}`,
      accessToken,
    );
  }

  async getCase(req: Request, caseId: number): Promise<Response> {
    const accessToken = await this.oidc.refreshIfNeeded(req);
    return this.getWithRetry(
      `${this.options.apiBaseUrl}/v1/kyc/cases/${caseId}`,
      accessToken,
    );
  }

  async decideStage(
    req: Request,
    caseId: number,
    stage: 'identity-verification' | 'document-verification',
    action: 'approve' | 'reject',
    remarks: string,
  ): Promise<Response> {
    const accessToken = await this.oidc.refreshIfNeeded(req);
    return this.postWithRetry(
      `${this.options.apiBaseUrl}/v1/kyc/cases/${caseId}/${stage}/${action}`,
      accessToken,
      { decisionRemarks: remarks },
    );
  }

  private async getWithRetry(url: string, accessToken: string): Promise<Response> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= 3; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 5000);
      try {
        const response = await fetch(url, {
          method: 'GET',
          headers: { Authorization: `Bearer ${accessToken}` },
          signal: controller.signal,
        });
        if (![408, 429, 500, 502, 503, 504].includes(response.status) || attempt === 3) return response;
      } catch (error) {
        lastError = error;
        if (attempt === 3) throw error;
      } finally {
        clearTimeout(timer);
      }
      await new Promise(resolve => setTimeout(resolve, 150 * attempt));
    }
    throw lastError instanceof Error ? lastError : new Error('KYC API request failed.');
  }

  private async postWithRetry(
    url: string,
    accessToken: string,
    body: unknown,
  ): Promise<Response> {
    let lastError: unknown;
    for (let attempt = 1; attempt <= 3; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 5000);
      try {
        const response = await fetch(url, {
          method: 'POST',
          headers: {
            Authorization: `Bearer ${accessToken}`,
            'Content-Type': 'application/json',
          },
          body: JSON.stringify(body),
          signal: controller.signal,
        });
        if (![408, 429, 500, 502, 503, 504].includes(response.status) || attempt === 3) return response;
      } catch (error) {
        lastError = error;
        if (attempt === 3) throw error;
      } finally {
        clearTimeout(timer);
      }
      await new Promise(resolve => setTimeout(resolve, 150 * attempt));
    }
    throw lastError instanceof Error ? lastError : new Error('KYC API decision request failed.');
  }
}
