'use client';

import { FlaskConical, RefreshCw, Unplug } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { formatTime, MfeShell, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge } from '@/app/components/ui/feedback';
import { Select } from '@/app/components/ui/form';
import { type ActionResult, type Behaviour, type FaultRoute, type RouteState, type SimulatorState } from '@/lib/operations-types';
import { BEHAVIOUR_LABEL, BEHAVIOUR_TONE } from '@/lib/simulator-display';
import { confirmWithAuthenticator, NOT_CONFIRMED } from '@/lib/step-up';
import { changeFaultRoute, changeSimulator, loadFaultRoutes, loadSimulators } from '../../actions';

const REFRESH_MS = 10_000;

const ROUTE_TONE: Record<RouteState | 'Unknown', 'success' | 'warning' | 'danger' | 'neutral'> = {
  Open: 'success', Slow: 'warning', Hanging: 'danger', Cut: 'danger', Unknown: 'neutral',
};

const ROUTE_LABEL: Record<RouteState, string> = {
  Open: 'Open', Slow: 'Slow (+5 s on every answer)', Hanging: 'Hanging (connects, never answers)', Cut: 'Cut (connection refused)',
};

/** A change needs a recent authenticator code: confirm it in the Shell's overlay, then try once more. */
async function withStepUp<T>(attempt: () => Promise<ActionResult<T>>): Promise<ActionResult<T>> {
  const result = await attempt();
  if (result.ok || !result.stepUp) return result;
  try {
    return await confirmWithAuthenticator() ? await attempt() : { ok: false, error: NOT_CONFIRMED };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : NOT_CONFIRMED };
  }
}

// ┌─ :: ARCHITECTURE + SECURITY :: The switches live on their own page ────────────────────────────
// │ Why : Platform Health is for watching, in every environment; changing how a dependency behaves
// │       is a deliberate act that exists only in Development, Test (and, for the fault routes,
// │       UAT). Keeping the two apart makes the dashboard safe to leave open and keeps the switches
// │       out of production screens.
// │ How : This page holds every control that changes behaviour: the simulators, and the fault
// │       proxy's routes (an API "off" for its server-side callers). Whether each works is decided
// │       by the Journey API alone: where a control does not exist it answers 404 and this page
// │       says so - the menu entry cannot be hidden per environment, the API is the gate. A change
// │       needs operations-journey.write, simulator.behaviour.manage and (with MFA on) a recent
// │       authenticator code.
// │ Ref : Features 1.7.4 | Operations Journey API README | OWASP A04 (insecure design)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * The operations administrator's simulator and fault controls: switch a simulated external system,
 * or cut, slow or hang the route to one of the platform's own APIs, to see how the platform copes.
 * Every change is recorded with the person's LAN ID and undone by itself after 15 minutes.
 */
export default function FaultControlsPage() {
  const [simulators, setSimulators] = useState<SimulatorState[] | null>(null);
  const [routes, setRoutes] = useState<FaultRoute[] | null>(null);
  const [simulatorsAvailable, setSimulatorsAvailable] = useState(true);
  const [routesAvailable, setRoutesAvailable] = useState(true);
  const [simulatorError, setSimulatorError] = useState<string | null>(null);
  const [routeError, setRouteError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    const [simulatorResult, routeResult] = await Promise.all([loadSimulators(), loadFaultRoutes()]);
    if ((!simulatorResult.ok && simulatorResult.signedOut) || (!routeResult.ok && routeResult.signedOut)) { signInAgain(); return; }
    if (simulatorResult.ok) { setSimulators(simulatorResult.data); setSimulatorError(null); }
    else if (simulatorResult.notAvailable) setSimulatorsAvailable(false);
    else setSimulatorError(simulatorResult.error);
    if (routeResult.ok) { setRoutes(routeResult.data); setRouteError(null); }
    else if (routeResult.notAvailable) setRoutesAvailable(false);
    else setRouteError(routeResult.error);
  }, []);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [refresh]);

  const changeBehaviour = async (simulator: SimulatorState, behaviour: Behaviour) => {
    setBusy(simulator.key);
    setSimulatorError(null);
    const result = await withStepUp(() => changeSimulator(simulator.key, behaviour));
    setBusy(null);
    if (result.ok) { setSimulators(result.data); return; }
    if (result.signedOut) { signInAgain(); return; }
    setSimulatorError(result.error);
  };

  const changeRoute = async (route: FaultRoute, state: RouteState) => {
    setBusy(route.key);
    setRouteError(null);
    const result = await withStepUp(() => changeFaultRoute(route.key, state));
    setBusy(null);
    if (result.ok) { setRoutes(result.data); return; }
    if (result.signedOut) { signInAgain(); return; }
    setRouteError(result.error);
  };

  return (
    <MfeShell title="Simulator & fault controls">
      <div className="space-y-6">
        {!simulatorsAvailable && !routesAvailable && (
          <Alert tone="info">
            These controls exist only in Development and test. In this environment nothing here can change how a
            dependency behaves; Platform Health shows how the platform is doing.
          </Alert>
        )}

        {/* NOTE: the fault proxy (Toxiproxy) runs on a developer's machine only; a cluster's callers use direct addresses. */}
        {simulatorsAvailable && !routesAvailable && (
          <Alert tone="info" title="Platform APIs (fault proxy): not in this environment">
            Cutting, slowing or hanging the route to one of the platform's own APIs needs the fault proxy, which runs on a
            developer's machine only. Here the simulated external systems below can still be switched.
          </Alert>
        )}

        {routesAvailable && (
          <Card>
            <CardHeader
              icon={<Unplug />}
              title="Platform APIs (fault proxy)"
              description="Cut, slow or hang the route to one of the platform's own APIs: every server-side caller listed goes through it, so to them the API is down - while the API itself keeps running and Platform Health still probes it directly. Each change is recorded with your LAN ID and opens again by itself after 15 minutes."
              actions={<Button variant="ghost" size="sm" onClick={() => void refresh()}><RefreshCw aria-hidden="true" />Refresh</Button>}
            />
            <CardContent className="space-y-4">
              {routeError && <Alert tone="danger">{routeError}</Alert>}
              {!routes && !routeError && <p className="text-sm text-ink-muted">Reading the fault proxy…</p>}
              {routes?.some((r) => r.state === 'Unknown') && (
                <Alert tone="warning">The fault proxy does not answer, or does not carry these routes. Start it with .\ps\toxiproxy\Start-Toxiproxy.ps1.</Alert>
              )}
              {routes && (
                <Table aria-label="Fault routes">
                  <thead>
                    <tr><Th>API</Th><Th>Its callers</Th><Th>Now</Th><Th>Make it</Th><Th>Open again</Th></tr>
                  </thead>
                  <tbody>
                    {routes.map((route) => (
                      <tr key={route.key}>
                        <Td className="font-medium">{route.name}</Td>
                        <Td className="text-ink-muted">{route.usedBy}</Td>
                        <Td><Badge tone={ROUTE_TONE[route.state]} dot>{route.state}</Badge></Td>
                        <Td>
                          <Select
                            aria-label={`Route to ${route.name}`}
                            value={route.state === 'Unknown' ? '' : route.state}
                            disabled={busy !== null || route.state === 'Unknown'}
                            onChange={(event) => void changeRoute(route, event.target.value as RouteState)}
                          >
                            {route.state === 'Unknown' && <option value="">Not carried</option>}
                            {route.states.map((s) => <option key={s} value={s}>{ROUTE_LABEL[s]}</option>)}
                          </Select>
                        </Td>
                        <Td className="text-ink-muted">
                          {route.revertsAt ? `${formatTime(route.revertsAt)} (set by ${route.changedBy})` : '—'}
                        </Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              )}
            </CardContent>
          </Card>
        )}

        {simulatorsAvailable && (
          <Card>
            <CardHeader
              icon={<FlaskConical />}
              title="Simulated external systems"
              description="Switch a simulator to see how the platform copes; every change is recorded with your LAN ID and goes back to Healthy by itself after 15 minutes. Platform Health shows the effect."
            />
            <CardContent className="space-y-4">
              {simulatorError && <Alert tone="danger">{simulatorError}</Alert>}
              {!simulators && !simulatorError && <p className="text-sm text-ink-muted">Reading the simulators…</p>}
              {simulators?.some((s) => s.behaviour === 'Unknown') && (
                <Alert tone="warning">A simulator does not answer, or refuses this API: is it running, and (in a cluster) does it have the same admin key as the Operations Journey API?</Alert>
              )}
              {simulators && (
                <Table aria-label="Simulators">
                  <thead>
                    <tr><Th>Simulator</Th><Th>Used for</Th><Th>Now</Th><Th>Switch to</Th><Th>Back to Healthy</Th></tr>
                  </thead>
                  <tbody>
                    {simulators.map((simulator) => (
                      <tr key={simulator.key}>
                        <Td className="font-medium">{simulator.name}</Td>
                        <Td className="text-ink-muted">{simulator.affects}</Td>
                        <Td><Badge tone={BEHAVIOUR_TONE[simulator.behaviour]} dot>{simulator.behaviour}</Badge></Td>
                        <Td>
                          <Select
                            aria-label={`Behaviour of ${simulator.name}`}
                            value={simulator.behaviour === 'Unknown' ? '' : simulator.behaviour}
                            disabled={busy !== null || simulator.behaviour === 'Unknown'}
                            onChange={(event) => void changeBehaviour(simulator, event.target.value as Behaviour)}
                          >
                            {simulator.behaviour === 'Unknown' && <option value="">Not answering</option>}
                            {simulator.behaviours.map((b) => <option key={b} value={b}>{BEHAVIOUR_LABEL[b]}</option>)}
                          </Select>
                        </Td>
                        <Td className="text-ink-muted">
                          {simulator.revertsAt ? `${formatTime(simulator.revertsAt)} (set by ${simulator.changedBy})` : '—'}
                        </Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              )}
            </CardContent>
          </Card>
        )}
      </div>
    </MfeShell>
  );
}