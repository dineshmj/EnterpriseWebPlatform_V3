import { BadRequestException, Body, Controller, Get, Param, Put, Req, UseGuards } from '@nestjs/common';
import { DelegatedRequest, DelegatedTokenGuard, Requires } from '../auth/delegated-token.guard';
import { RouteState, routeStates } from './fault-controller';
import { FaultControlService } from './fault-control.service';
import { FaultRouteKey, faultRoutes } from './fault-routes';

const READ = { scope: 'operations-journey.read', permission: 'service.health.view' };
const CHANGE = { scope: 'operations-journey.write', permission: 'simulator.behaviour.manage', stepUp: true };

/** The fault proxy's routes (Development, Test and UAT only): see them, and open, slow, hang or cut one. */
@Controller('v1/journeys/operations/fault-routes')
@UseGuards(DelegatedTokenGuard)
export class FaultRoutesController {
  constructor(private readonly faults: FaultControlService) {}

  @Get()
  @Requires(READ)
  list() {
    return this.faults.list();
  }

  @Put(':key/state')
  @Requires(CHANGE)
  async change(@Req() request: DelegatedRequest, @Param('key') key: string, @Body() body: { state?: unknown }) {
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only a known route and a known state reach the proxy - its admin path is never
    // │ built from free input.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (!faultRoutes.some((r) => r.key === key)) throw new BadRequestException('Unknown route.');
    if (typeof body?.state !== 'string' || !(routeStates as readonly string[]).includes(body.state)) {
      throw new BadRequestException(`State must be one of ${routeStates.join(', ')}.`);
    }
    const by = request.delegation!.lanId ?? request.delegation!.sub;
    await this.faults.change(key as FaultRouteKey, body.state as RouteState, by);
    return this.faults.list();
  }
}