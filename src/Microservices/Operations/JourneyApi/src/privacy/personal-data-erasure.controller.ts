import { BadRequestException, Body, Controller, Get, Param, Post, Req, UseGuards } from '@nestjs/common';
import { DelegatedRequest, DelegatedTokenGuard, Requires } from '../auth/delegated-token.guard';
import { ErasureReason, erasureReasons, PersonalDataErasureService } from './personal-data-erasure.service';

const VIEW = { scope: 'operations-journey.read', permission: ['personal-data.erasure.request', 'personal-data.erasure.approve'] };
const REQUEST = { scope: 'operations-journey.write', permission: 'personal-data.erasure.request', stepUp: true };
const DECIDE = { scope: 'operations-journey.write', permission: 'personal-data.erasure.approve', stepUp: true };
const CUSTOMER_NUMBER = /^[A-Za-z0-9-]{1,30}$/;
const REQUEST_ID = /^[1-9][0-9]{0,17}$/;

/** Personal-data erasure: see the requests, ask for one, and - another operator - approve or reject it. */
@Controller('v1/journeys/operations/personal-data-erasures')
@UseGuards(DelegatedTokenGuard)
export class PersonalDataErasureController {
  constructor(private readonly erasure: PersonalDataErasureService) {}

  @Get()
  @Requires(VIEW)
  list(@Req() request: DelegatedRequest) {
    return this.erasure.list(request.delegation!.subjectToken);
  }

  @Get('policy')
  @Requires(VIEW)
  policy(@Req() request: DelegatedRequest) {
    return this.erasure.policy(request.delegation!.subjectToken);
  }

  @Post()
  @Requires(REQUEST)
  create(@Req() request: DelegatedRequest, @Body() body: { customerNumber?: unknown; reasonCode?: unknown }) {
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only a customer-number-shaped value and one of the two reason codes are forwarded:
    // │ no free text reaches the Customer Onboarding API, its events or the audit trail.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (typeof body?.customerNumber !== 'string' || !CUSTOMER_NUMBER.test(body.customerNumber.trim())) throw new BadRequestException('Give the customer number.');
    if (typeof body.reasonCode !== 'string' || !(erasureReasons as readonly string[]).includes(body.reasonCode)) throw new BadRequestException('Choose the reason.');
    return this.erasure.request(request.delegation!.subjectToken, body.customerNumber.trim(), body.reasonCode as ErasureReason);
  }

  @Post(':id/approve')
  @Requires(DECIDE)
  approve(@Req() request: DelegatedRequest, @Param('id') id: string) {
    return this.erasure.decide(request.delegation!.subjectToken, requestId(id), true);
  }

  @Post(':id/reject')
  @Requires(DECIDE)
  reject(@Req() request: DelegatedRequest, @Param('id') id: string, @Body() body: { remarks?: unknown }) {
    if (typeof body?.remarks !== 'string') throw new BadRequestException('Say why the erasure is refused.');
    return this.erasure.decide(request.delegation!.subjectToken, requestId(id), false, body.remarks);
  }
}

/** The path to the Domain API is never built from free input: a positive whole number only. */
function requestId(id: string): number {
  if (!REQUEST_ID.test(id)) throw new BadRequestException('Unknown erasure request.');
  return Number(id);
}