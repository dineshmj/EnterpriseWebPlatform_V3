import { Inject, Injectable, Logger, NotFoundException, OnModuleDestroy, OnModuleInit } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { FAULT_CONTROLLER, FaultController, RouteState, routeStates } from './fault-controller';
import { FaultRouteKey, faultRoutes } from './fault-routes';

export interface FaultRouteView {
  key: FaultRouteKey;
  name: string;
  usedBy: string;
  /** Unknown: the proxy does not carry this route, or does not answer. */
  state: RouteState | 'Unknown';
  states: readonly RouteState[];
  revertsAt: string | null;
  changedBy: string | null;
}

// ┌─ :: RESILIENCE + SECURITY :: Turning an API "off" for a demonstration ─────────────────────────
// │ Why : Resilience is only believable when you can watch a caller meet a dead API - yet a switch
// │       that takes an API off the network must never exist where real customers are.
// │ How : Only when OPERATIONS_FAULT_CONTROLS_ENABLED, which the API refuses outside Development,
// │       Test and UAT (otherwise 404: the feature does not exist). The routes are cut in the fault
// │       proxy, never in the API: the API keeps running and its own probe stays direct. Permission
// │       simulator.behaviour.manage + recent MFA; every change is logged with the LAN ID; each
// │       route opens again by itself after OPERATIONS_FAULT_AUTO_REVERT_MINUTES, and every route
// │       is opened at start-up, so a restart never leaves an API cut with nobody knowing why.
// │ Ref : Features 1.7.4 | Threat-Model (Operations) | Chaos engineering (fault injection) | OWASP A04
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class FaultControlService implements OnModuleInit, OnModuleDestroy {
  private readonly logger = new Logger('FaultControls');
  private readonly reverts = new Map<FaultRouteKey, { timer: NodeJS.Timeout; at: Date; by: string }>();

  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    @Inject(FAULT_CONTROLLER) private readonly controller: FaultController,
  ) {}

  get enabled(): boolean {
    return this.options.faultControlsEnabled;
  }

  get adminUrl(): string {
    return this.options.faultProxyAdminUrl;
  }

  async onModuleInit(): Promise<void> {
    if (!this.enabled) return;
    try {
      await this.controller.reopenAll();
      this.logger.log('FAULT ROUTES: every route opened (start-up).');
    } catch {
      this.logger.warn('FAULT ROUTES: the fault proxy does not answer at start-up; start it with .\\ps\\toxiproxy\\Start-Toxiproxy.ps1.');
    }
  }

  onModuleDestroy(): void {
    for (const revert of this.reverts.values()) clearTimeout(revert.timer);
    this.reverts.clear();
  }

  async list(): Promise<FaultRouteView[]> {
    this.ensureEnabled();
    const states = await this.controller.states().catch(() => new Map<string, RouteState>());
    return faultRoutes.map((route) => {
      const revert = this.reverts.get(route.key);
      return {
        key: route.key, name: route.component, usedBy: route.usedBy,
        state: states.get(route.key) ?? 'Unknown', states: routeStates,
        revertsAt: revert?.at.toISOString() ?? null, changedBy: revert?.by ?? null,
      };
    });
  }

  /** What each Platform Health component's route does, when it is not Open (for the "at the proxy" mark). */
  async notOpenByComponent(): Promise<Map<string, RouteState>> {
    const marks = new Map<string, RouteState>();
    if (!this.enabled) return marks;
    const states = await this.controller.states().catch(() => new Map<string, RouteState>());
    for (const route of faultRoutes) {
      const state = states.get(route.key);
      if (state && state !== 'Open') marks.set(route.component, state);
    }
    return marks;
  }

  async change(key: FaultRouteKey, state: RouteState, by: string): Promise<void> {
    this.ensureEnabled();
    const route = faultRoutes.find((r) => r.key === key);
    if (!route) throw new NotFoundException('Unknown route.');

    await this.controller.set(key, state);
    this.clearRevert(key);

    if (state !== 'Open') {
      const minutes = this.options.faultAutoRevertMinutes;
      const at = new Date(Date.now() + minutes * 60_000);
      const timer = setTimeout(() => {
        this.reverts.delete(key);
        this.controller.set(key, 'Open')
          .then(() => this.logger.log(`FAULT ROUTE ${route.component} open again (automatic, after ${minutes} min).`))
          .catch((error: Error) => this.logger.error(`FAULT ROUTE ${route.component}: the automatic reopening failed: ${error.message}`));
      }, at.getTime() - Date.now());
      timer.unref();
      this.reverts.set(key, { timer, at, by });
    }

    // ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
    // │ The record of who changed what (the log is shipped like every other component's).
    // └──────────────────────────────────────────────────────────────────────────────────────
    this.logger.log(`FAULT ROUTE CHANGED: ${route.component} -> ${state} by ${by}` +
      (state === 'Open' ? '.' : `; open again automatically at ${this.reverts.get(key)!.at.toISOString()}.`));
  }

  private ensureEnabled(): void {
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Off = 404: outside Development, Test and UAT the routes cannot even be listed.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (!this.enabled) throw new NotFoundException();
  }

  private clearRevert(key: FaultRouteKey): void {
    const revert = this.reverts.get(key);
    if (revert) clearTimeout(revert.timer);
    this.reverts.delete(key);
  }
}