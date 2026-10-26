import { Inject, Injectable, NotFoundException, OnModuleDestroy, ServiceUnavailableException } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { Behaviour, PlatformComponent, SimulatorKey, simulators } from './components';

export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unreachable';

export interface ComponentHealth {
  name: string;
  group: string;
  status: HealthStatus;
  responseMs: number | null;
}

/**
 * Asks every component for its readiness, in parallel, with a short timeout. Only the status
 * and the response time leave this API - never the probe's details or URLs (they describe the
 * internals: database, Kafka, outbox backlog).
 */
@Injectable()
export class HealthProbeService {
  constructor(@Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions) {}

  async probeAll(extra: PlatformComponent[] = []): Promise<{ checkedAt: string; components: ComponentHealth[] }> {
    const results = await Promise.all([...this.options.platform.components, ...extra].map(async (component) => {
      const started = Date.now();
      try {
        const response = await fetch(component.readiness, { signal: AbortSignal.timeout(3000), redirect: 'manual' });
        const status = await statusOf(response);
        return { name: component.name, group: component.group, status, responseMs: Date.now() - started };
      } catch {
        return { name: component.name, group: component.group, status: 'Unreachable' as const, responseMs: null };
      }
    }));
    return { checkedAt: new Date().toISOString(), components: results };
  }
}

async function statusOf(response: Response): Promise<HealthStatus> {
  let reported: unknown;
  try {
    reported = ((await response.json()) as { status?: unknown }).status;
  } catch {
    reported = undefined;   // a page (the Audit web) or an empty body: the HTTP status decides
  }
  if (reported === 'Healthy' || reported === 'Degraded' || reported === 'Unhealthy') return reported;
  // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
  // │ A page without a health endpoint (a Next.js web app's home): any answer below 400 - a page,
  // │ or a redirect to its first screen (not followed) - means it is up.
  // └──────────────────────────────────────────────────────────────────────────────────────
  return response.status < 400 ? 'Healthy' : 'Unhealthy';
}

export interface SimulatorState {
  key: SimulatorKey;
  name: string;
  affects: string;
  behaviour: Behaviour | 'Unknown';
  /** The behaviours this simulator knows. */
  behaviours: readonly Behaviour[];
  /** When a changed behaviour goes back to Healthy by itself. */
  revertsAt: string | null;
  changedBy: string | null;
}

// ┌─ :: ARCHITECTURE + SECURITY :: The simulators' behaviour switch ───────────────────────────────
// │ Why : Switching a simulator to "Down" is a powerful demo tool that must never exist in
// │       production.
// │ How : Only when OPERATIONS_SIMULATOR_CONTROLS_ENABLED (Development / Test); called server-side
// │       on loopback-only endpoints; permission simulator.behaviour.manage + recent MFA; logged
// │       with the LAN ID; auto-revert to Healthy.
// │ Ref : Features 1.7.4 | Threat-Model (Operations) | OWASP A04 (insecure design)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * The simulators' behaviour switch, Development and test only. The simulators accept the switch
 * from loopback callers only, so it is called here - on the same machine - and never from the
 * browser. Every change is logged with who made it, and goes back to Healthy by itself, so a
 * forgotten "Down" does not break the next demonstration.
 */
@Injectable()
export class SimulatorService implements OnModuleDestroy {
  private readonly reverts = new Map<SimulatorKey, { timer: NodeJS.Timeout; at: Date; by: string }>();

  constructor(@Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions) {}

  get enabled(): boolean {
    return this.options.simulatorControlsEnabled;
  }

  async list(): Promise<SimulatorState[]> {
    this.ensureEnabled();
    return Promise.all(simulators.map(async (simulator) => {
      let behaviour: SimulatorState['behaviour'] = 'Unknown';
      try {
        const response = await fetch(`${this.options.platform.simulatorBaseUrls[simulator.key]}/admin/behaviour`, {
          headers: this.adminHeaders(), signal: AbortSignal.timeout(3000),
        });
        if (response.ok) behaviour = ((await response.json()) as { behaviour?: Behaviour }).behaviour ?? 'Unknown';
      } catch {
        behaviour = 'Unknown';
      }
      const revert = this.reverts.get(simulator.key);
      return {
        key: simulator.key, name: simulator.name, affects: simulator.affects, behaviour, behaviours: simulator.behaviours,
        revertsAt: revert?.at.toISOString() ?? null, changedBy: revert?.by ?? null,
      };
    }));
  }

  async change(key: SimulatorKey, behaviour: Behaviour, by: string): Promise<void> {
    this.ensureEnabled();
    const simulator = simulators.find((s) => s.key === key);
    if (!simulator) throw new NotFoundException('Unknown simulator.');

    await this.send(this.options.platform.simulatorBaseUrls[simulator.key], behaviour);
    this.clearRevert(key);

    if (behaviour !== 'Healthy') {
      const at = new Date(Date.now() + this.options.simulatorAutoRevertMinutes * 60_000);
      const timer = setTimeout(() => {
        this.reverts.delete(key);
        this.send(this.options.platform.simulatorBaseUrls[simulator.key], 'Healthy')
          .then(() => console.log(`SIMULATOR ${simulator.name} back to Healthy (automatic, after ${this.options.simulatorAutoRevertMinutes} min).`))
          .catch((error: Error) => console.error(`SIMULATOR ${simulator.name}: the automatic return to Healthy failed: ${error.message}`));
      }, at.getTime() - Date.now());
      timer.unref();
      this.reverts.set(key, { timer, at, by });
    }

    // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
    // │ The record of who changed what (the log is shipped like every other component's).
    // └──────────────────────────────────────────────────────────────────────────────────────
    console.log(`SIMULATOR BEHAVIOUR CHANGED: ${simulator.name} -> ${behaviour} by ${by}` +
      (behaviour === 'Healthy' ? '.' : `; back to Healthy automatically at ${this.reverts.get(key)!.at.toISOString()}.`));
  }

  onModuleDestroy(): void {
    for (const key of [...this.reverts.keys()]) this.clearRevert(key);
  }

  private ensureEnabled(): void {
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ If the switch is off, answer 404 - outside Development and Test the feature does not
    // │ exist at all.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (!this.options.simulatorControlsEnabled) {
      throw new NotFoundException();   // outside Development and test: as if it did not exist
    }
  }

  private clearRevert(key: SimulatorKey): void {
    const revert = this.reverts.get(key);
    if (revert) clearTimeout(revert.timer);
    this.reverts.delete(key);
  }

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ In a cluster the simulators are on other machines: the admin key proves this API is the
  // │ caller (server-side only - never sent to or from the browser). On a laptop it is unset
  // │ and the simulators accept the switch from loopback.
  // └──────────────────────────────────────────────────────────────────────────────────────
  private adminHeaders(): Record<string, string> {
    return this.options.simulatorAdminKey ? { 'X-Simulator-Admin-Key': this.options.simulatorAdminKey } : {};
  }

  private async send(baseUrl: string, behaviour: Behaviour): Promise<void> {
    let response: Response;
    try {
      response = await fetch(`${baseUrl}/admin/behaviour`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', ...this.adminHeaders() },
        body: JSON.stringify({ behaviour }),
        signal: AbortSignal.timeout(5000),
      });
    } catch {
      throw new ServiceUnavailableException('The simulator does not answer. Is it running (Visual Studio)?');
    }
    if (!response.ok) {
      throw new ServiceUnavailableException(`The simulator refused the change (HTTP ${response.status}).`);
    }
  }
}