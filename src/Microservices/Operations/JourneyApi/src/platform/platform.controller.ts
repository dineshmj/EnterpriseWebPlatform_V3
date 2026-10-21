import { BadRequestException, Body, Controller, Get, Param, Put, Req, UseGuards } from '@nestjs/common';
import { DelegatedRequest, DelegatedTokenGuard, Requires } from '../auth/delegated-token.guard';
import { FaultControlService } from '../faults/fault-control.service';
import { Behaviour, SimulatorKey, simulators } from './components';
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
    private readonly faults: FaultControlService,
  ) {}

  @Get('health')
  @Requires(READ)
  async platformHealth() {
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The probes go to each component directly, never through the fault proxy: a cut API
    // │ still shows its own health, and the mark says its callers cannot reach it.
    // └──────────────────────────────────────────────────────────────────────────────────────
    const [health, marks] = await Promise.all([
      this.health.probeAll(this.faults.enabled ? [faultProxyComponent(this.faults)] : []),
      this.faults.notOpenByComponent(),
    ]);
    return {
      ...health,
      components: health.components.map((c) => ({ ...c, atProxy: marks.get(c.name) ?? null })),
      simulatorControls: this.simulators.enabled,
      faultControls: this.faults.enabled,
    };
  }

  @Get('simulators')
  @Requires(READ)
  async simulatorStates() {
    return this.simulators.list();
  }

  @Put('simulators/:key/behaviour')
  @Requires(CHANGE)
  async changeBehaviour(@Req() request: DelegatedRequest, @Param('key') key: string, @Body() body: { behaviour?: unknown }) {
    const simulator = simulators.find((s) => s.key === key);
    if (!simulator) {
      throw new BadRequestException('Unknown simulator.');
    }
    if (typeof body?.behaviour !== 'string' || !(simulator.behaviours as readonly string[]).includes(body.behaviour)) {
      throw new BadRequestException(`Behaviour must be one of ${simulator.behaviours.join(', ')}.`);
    }
    const by = request.delegation!.lanId ?? request.delegation!.sub;
    await this.simulators.change(key as SimulatorKey, body.behaviour as Behaviour, by);
    return this.simulators.list();
  }
}

/** The fault proxy itself, on Platform Health while the fault controls are on. */
function faultProxyComponent(faults: FaultControlService) {
  return { name: 'Fault proxy (Toxiproxy)', group: 'Simulators' as const, readiness: `${faults.adminUrl}/version` };
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