import { expect, request as playwrightRequest, test } from '@playwright/test';
import type { ShellPage } from './pages/ShellPage';

/**
 * The fault proxy's routes: in Development every server-side caller of an EWP API reaches it
 * through Toxiproxy (ps\toxiproxy\toxiproxy.json). An operations administrator makes a route
 * Slow, Hanging or Cut on Operations > Simulator & Fault Controls - so to its callers the API is
 * down, while it keeps running and Platform Health still probes it directly.
 *
 * A route is GLOBAL, like a simulator's behaviour: it changes the API for everybody. So a route
 * is only ever changed through withRoute(), which ALWAYS opens it again when the step ends, passed
 * or failed - and openAllRoutes() runs before and after every spec file that uses it, in case an
 * earlier run was interrupted before it could clean up (the Journey API's 15-minute auto-open is
 * the last safety net, for people, not for tests).
 */

/** The APIs behind a route, as the controls page and Platform Health name them. */
export type RoutedApi =
  | 'Customer Onboarding API' | 'Customer KYC API' | 'Documents Management API' | 'Compliance API'
  | 'Accounts API' | 'Payments API' | 'Notifications API' | 'Audit API' | 'Partner Products API'
  | 'Audit Journey API' | 'Partner Products Journey API';

export type RouteState = 'Open' | 'Slow' | 'Hanging' | 'Cut';

/** Platform Health's mark for a route that is not Open. */
export const AT_PROXY: Record<Exclude<RouteState, 'Open'>, string> = {
  Slow: 'Slowed at the proxy', Hanging: 'Hanging at the proxy', Cut: 'Cut at the proxy',
};

/** Operations > Simulator & Fault Controls, as the operations administrator (daniel.ops). */
export class FaultControls {
  static readonly title = 'Simulator & fault controls';

  constructor(private readonly operator: ShellPage) {}

  async open(): Promise<void> {
    await this.operator.openMenuItem('Operations', 'Simulator & Fault Controls');
    await this.operator.waitForScreen(FaultControls.title);
  }

  /** The "Make it" switch of one API's route. */
  routeSwitch(api: RoutedApi) {
    return this.operator.workspace().getByLabel(`Route to ${api}`, { exact: true });
  }

  /** Makes the route do `state`, and waits until the page reports it (read back from the proxy). */
  async set(api: RoutedApi, state: RouteState): Promise<void> {
    const routeSwitch = this.routeSwitch(api);
    await expect(routeSwitch, 'the route is carried by the fault proxy (is it running?)').toBeEnabled({ timeout: 30_000 });
    if ((await routeSwitch.inputValue()) !== state) await routeSwitch.selectOption(state);
    await expect(routeSwitch).toHaveValue(state, { timeout: 30_000 });
    // The "Now" cell (the third): what the proxy reports - not the switch, whose options name every state.
    const now = this.operator.workspace().getByRole('row').filter({ has: routeSwitch }).getByRole('cell').nth(2);
    await expect(now).toHaveText(state, { timeout: 30_000 });
  }
}

/** Operations > Platform Health, as the operations administrator: status and the proxy's mark per component. */
export class PlatformHealth {
  static readonly title = 'Platform health';

  constructor(private readonly operator: ShellPage) {}

  async open(): Promise<void> {
    await this.operator.openMenuItem('Operations', 'Platform Health');
    await this.operator.waitForScreen(PlatformHealth.title);
  }

  /** One component's row (its name cell matches exactly: "Accounts API" is not "Accounts BFF"). */
  row(component: string) {
    return this.operator.workspace().getByRole('row').filter({
      has: this.operator.workspace().getByRole('cell', { name: component, exact: true }),
    });
  }

  /** The page refreshes every 10 seconds: wait for the row to say `text`. */
  async expectRow(component: string, text: string | RegExp, timeout = 60_000): Promise<void> {
    await expect(this.row(component)).toContainText(text, { timeout });
  }
}

/** Sets a route for the duration of `work`, as the operator on the controls page; ALWAYS opens it again. */
export async function withRoute<T>(operator: ShellPage, api: RoutedApi, state: Exclude<RouteState, 'Open'>, work: () => Promise<T>): Promise<T> {
  const controls = new FaultControls(operator);
  await test.step(`Daniel: the route to the ${api} -> ${state}`, async () => {
    await controls.open();
    await controls.set(api, state);
  });
  try {
    return await work();
  } finally {
    await test.step(`Daniel: the route to the ${api} -> Open`, async () => {
      try {
        await controls.open();
        await controls.set(api, 'Open');
      } catch {
        // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
        // │ The page could not do it (a failed test may have left the operator's window anywhere):
        // │ open every route at the proxy itself, so the next test starts clean.
        // └──────────────────────────────────────────────────────────────────────────────────────
        await openAllRoutes();
      }
    });
  }
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The proxy's admin API answers on 127.0.0.1 only and has no authentication - these
// │ tests run on the same machine, like the simulators' loopback-only switch. It refuses
// │ browser-like user agents (its CSRF guard), so the request names itself.
// └──────────────────────────────────────────────────────────────────────────────────────
/** Opens every route at the proxy (Toxiproxy's reset) - for a spec file's beforeAll / afterAll. */
export async function openAllRoutes(): Promise<void> {
  const api = await playwrightRequest.newContext({ userAgent: 'ewp-playwright' });
  try {
    const response = await api.post('http://127.0.0.1:8474/reset', { timeout: 10_000 });
    if (!response.ok()) {
      throw new Error(`The fault proxy refused to open its routes: HTTP ${response.status()}. Is it running (.\\ps\\toxiproxy\\Start-Toxiproxy.ps1)?`);
    }
  } finally {
    await api.dispose();
  }
}