import { BadRequestException, Controller, Get, Inject, Param, Query, Req, UseGuards } from '@nestjs/common';
import { DelegatedRequest, DelegatedTokenGuard, RequiresPermission } from '../auth/delegated-token.guard';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { DomainApiClient } from '../services/domain-api.client';

const AUDIT_READ = 'audit.read';
const PAYMENTS_READ = 'payments.read';
const SEARCH_PARAMS = ['record', 'person', 'eventType', 'from', 'to', 'kind', 'pageNumber', 'pageSize'] as const;

/**
 * The auditor's journeys. Each one is a single call for the screen, which this API turns into
 * the Domain API calls it needs - always FOR the signed-in person (token exchange), so every
 * Domain API still decides by the person's own permissions and records who asked.
 */
@Controller('v1/journeys/audit')
@UseGuards(DelegatedTokenGuard)
export class AuditJourneysController {
  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    private readonly api: DomainApiClient,
  ) {}

  /** Search the trail (Audit API). */
  @Get('entries')
  @RequiresPermission('audit.search')
  async search(@Req() request: DelegatedRequest, @Query() query: Record<string, string | undefined>) {
    const params = new URLSearchParams();
    for (const name of SEARCH_PARAMS) {
      const value = query[name];
      if (typeof value === 'string' && value.length > 0) params.set(name, value.slice(0, 100));
    }
    return this.api.get(request.delegation!.subjectToken, AUDIT_READ, `${this.options.auditApiBaseUrl}/v1/audit/entries?${params}`);
  }

  /**
   * One record, end to end: its audit timeline (Audit API) and - for a payment - where it
   * stands now (Payments API), called in parallel. If Payments cannot answer, the timeline is
   * still returned and the screen says why the current status is missing.
   */
  @Get('records/:recordRef')
  @RequiresPermission('audit.view')
  async record(@Req() request: DelegatedRequest, @Param('recordRef') recordRef: string) {
    if (!/^[A-Za-z0-9-]{1,100}$/.test(recordRef)) {
      throw new BadRequestException('Unknown record reference.');
    }
    const token = request.delegation!.subjectToken;
    const ref = encodeURIComponent(recordRef);

    const timeline = this.api.get<unknown[]>(token, AUDIT_READ, `${this.options.auditApiBaseUrl}/v1/audit/records/${ref}/timeline`);
    const current = recordRef.startsWith('PAY-')
      ? this.api
          .get(token, PAYMENTS_READ, `${this.options.paymentsApiBaseUrl}/v1/payments/by-number/${ref}`)
          .then((payment) => ({ source: 'payments', payment }))
          .catch((error: Error) => ({ source: 'payments', unavailable: error.message }))
      : Promise.resolve(null);

    const [entries, now] = await Promise.all([timeline, current]);
    return { recordRef, current: now, timeline: entries };
  }

  /** Re-verify the whole hash chain now (Audit API). */
  @Get('integrity')
  @RequiresPermission('audit.view')
  async integrity(@Req() request: DelegatedRequest) {
    return this.api.get(request.delegation!.subjectToken, AUDIT_READ, `${this.options.auditApiBaseUrl}/v1/audit/integrity`);
  }
}

/** Probes for the orchestrator, like every other component. */
@Controller('health')
export class HealthController {
  @Get('live')
  live() {
    return { status: 'Healthy' };
  }

  @Get('ready')
  async ready() {
    return { status: 'Healthy' };
  }
}