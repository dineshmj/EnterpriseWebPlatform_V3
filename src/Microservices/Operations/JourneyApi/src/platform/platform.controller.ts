import { BadRequestException, Body, Controller, Get, Param, Put, Req, UseGuards } from '@nestjs/common';
import { DelegatedRequest, DelegatedTokenGuard, Requires } from '../auth/delegated-token.guard';
import { Behaviour, behaviours, SimulatorKey, simulators } from './components';
import { HealthProbeService, SimulatorService } from './platform.services';

const READ = { scope: 'operations-journey.read', permission: 'service.health.view' };
const CHANGE = { scope: 'operations-journey.write', permission: 'simulator.behaviour.manage', stepUp: true };

/**
 * The operations administrator's journeys: the platform's health at a glance and - in
 * Development and test only - the simulators' behaviour, for demonstrating resilience.
 */
@Controller('v1/journeys/operations')
@UseGuards(DelegatedTokenGuard)
export class PlatformController {
  constructor(
    private readonly health: HealthProbeService,
    private readonly simulators: SimulatorService,
  ) {}

  @Get('health')
  @Requires(READ)
  async platformHealth() {
    return { ...(await this.health.probeAll()), simulatorControls: this.simulators.enabled };
  }

  @Get('simulators')
  @Requires(READ)
  async simulatorStates() {
    return this.simulators.list();
  }

  @Put('simulators/:key/behaviour')
  @Requires(CHANGE)
  async changeBehaviour(@Req() request: DelegatedRequest, @Param('key') key: string, @Body() body: { behaviour?: unknown }) {
    if (!simulators.some((s) => s.key === key)) {
      throw new BadRequestException('Unknown simulator.');
    }
    if (typeof body?.behaviour !== 'string' || !(behaviours as readonly string[]).includes(body.behaviour)) {
      throw new BadRequestException(`Behaviour must be one of ${behaviours.join(', ')}.`);
    }
    const by = request.delegation!.lanId ?? request.delegation!.sub;
    await this.simulators.change(key as SimulatorKey, body.behaviour as Behaviour, by);
    return this.simulators.list();
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
  ready() {
    return { status: 'Healthy' };
  }
}