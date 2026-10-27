'use client';

import { Activity, FlaskConical, RefreshCw } from 'lucide-react';
import { unstable_isUnrecognizedActionError } from 'next/navigation';
import { useCallback, useEffect, useState } from 'react';
import { formatTime, MfeShell, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge } from '@/app/components/ui/feedback';
import { type ComponentHealth, type HealthStatus, type PlatformHealth, type RouteState, type SimulatorState } from '@/lib/operations-types';
import { BEHAVIOUR_TONE } from '@/lib/simulator-display';
import { loadPlatformHealth, loadSimulators } from '../../actions';
import { ParkedMessagesCard } from './ParkedMessagesCard';

const REFRESH_MS = 10_000;

const AT_PROXY: Record<RouteState, string> = {
  Open: '', Slow: 'Slowed at the proxy', Hanging: 'Hanging at the proxy', Cut: 'Cut at the proxy',
};

const TONE: Record<HealthStatus, 'success' | 'warning' | 'danger' | 'neutral'> = {
  Healthy: 'success', Degraded: 'warning', Unhealthy: 'danger', Unreachable: 'neutral',
};

// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ Read-only for the platform's behaviour: nothing on this page changes how a component or
// │ dependency behaves (the switches are on Simulator & fault controls, Development and test
// │ only). The one action kept here is the parked-message resend - recovery work that
// │ production needs too, with its own reason, permission and step-up.
// └──────────────────────────────────────────────────────────────────────────────────────
/**
 * The operations administrator's view of the platform: every component's readiness (refreshed
 * every 10 seconds), in Development and test what the simulators are doing now (read-only), and
 * the parked Outbox messages.
 */
export default function PlatformHealthPage() {
  const [health, setHealth] = useState<PlatformHealth | null>(null);
  const [simulators, setSimulators] = useState<SimulatorState[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    // ┌─ RESIL + OBS ────────────────────────────────────────────────────────────────────────
    // │ If this app's own server cannot be reached, the action THROWS (no result at all).
    // │ Say so, rather than leaving the last answer on screen as if it were current - a
    // │ health page must never show HEALTHY from a check it could not make. A call from an
    // │ older build is rethrown: MfeShell reloads the page for that.
    // └──────────────────────────────────────────────────────────────────────────────────────
    let result: Awaited<ReturnType<typeof loadPlatformHealth>>;
    try {
      result = await loadPlatformHealth();
    } catch (e) {
      if (unstable_isUnrecognizedActionError(e)) throw e;
      setError('The Operations service could not be reached. The readiness below is from the last successful check - it is not current.');
      return;
    }
    if (!result.ok) {
      if (result.signedOut) { signInAgain(); return; }
      setError(result.error);
      return;
    }
    setHealth(result.data);
    setError(null);
    if (result.data.simulatorControls) {
      const states = await loadSimulators();
      if (states.ok) setSimulators(states.data);
    }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = window.setInterval(() => void refresh(), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [refresh]);

  const groups = health ? [...new Set(health.components.map((c) => c.group))] : [];
  const counts = health ? countBy(health.components) : null;

  return (
    <MfeShell title="Platform health">
      <div className="space-y-6">
        {error && <Alert tone="danger">{error}</Alert>}

        <Card>
          <CardHeader
            icon={<Activity />}
            title="Components"
            description={health
              ? `Readiness of every deployable part, checked ${formatTime(health.checkedAt)}; refreshed every 10 seconds.`
              : 'Checking every component…'}
            actions={
              <>
                {counts && (
                  <span className="flex gap-2">
                    <Badge tone="success" dot>{counts.Healthy} healthy</Badge>
                    {counts.Degraded > 0 && <Badge tone="warning" dot>{counts.Degraded} degraded</Badge>}
                    {counts.Unhealthy + counts.Unreachable > 0 && <Badge tone="danger" dot>{counts.Unhealthy + counts.Unreachable} down</Badge>}
                  </span>
                )}
                <Button variant="ghost" size="sm" onClick={() => void refresh()}><RefreshCw aria-hidden="true" />Refresh</Button>
              </>
            }
          />
          {health && (
            <div className="grid gap-0 lg:grid-cols-2">
              {groups.map((group) => (
                <Table key={group} aria-label={group}>
                  <thead>
                    <tr><Th>{group}</Th><Th>Status</Th><Th className="text-right">Response</Th></tr>
                  </thead>
                  <tbody>
                    {health.components.filter((c) => c.group === group).map((component) => (
                      <tr key={component.name}>
                        <Td className="font-medium">{component.name}</Td>
                        <Td>
                          <span className="flex flex-wrap items-center gap-2">
                            <Badge tone={TONE[component.status]} dot>{component.status}</Badge>
                            {/* The probe goes direct; this says its callers cannot reach it (Simulator & Fault Controls). */}
                            {component.atProxy && <Badge tone={component.atProxy === 'Slow' ? 'warning' : 'danger'}>{AT_PROXY[component.atProxy]}</Badge>}
                          </span>
                        </Td>
                        <Td className="text-right font-mono text-[13px] text-ink-muted">
                          {component.responseMs === null ? '—' : `${component.responseMs} ms`}
                        </Td>
                      </tr>
                    ))}
                  </tbody>
                </Table>
              ))}
            </div>
          )}
        </Card>

        {health?.simulatorControls && (
          <Card>
            <CardHeader
              icon={<FlaskConical />}
              title="Simulated external systems"
              description="Development and test only. What each simulator is doing now; switch them on Simulator & fault controls."
            />
            <CardContent>
              {!simulators && <p className="text-sm text-ink-muted">Reading the simulators…</p>}
              {simulators && (
                <Table aria-label="Simulators">
                  <thead>
                    <tr><Th>Simulator</Th><Th>Used for</Th><Th>Now</Th><Th>Back to Healthy</Th></tr>
                  </thead>
                  <tbody>
                    {simulators.map((simulator) => (
                      <tr key={simulator.key}>
                        <Td className="font-medium">{simulator.name}</Td>
                        <Td className="text-ink-muted">{simulator.affects}</Td>
                        <Td><Badge tone={BEHAVIOUR_TONE[simulator.behaviour]} dot>{simulator.behaviour}</Badge></Td>
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

        <ParkedMessagesCard />
      </div>
    </MfeShell>
  );
}

function countBy(components: ComponentHealth[]): Record<HealthStatus, number> {
  const counts: Record<HealthStatus, number> = { Healthy: 0, Degraded: 0, Unhealthy: 0, Unreachable: 0 };
  for (const component of components) counts[component.status]++;
  return counts;
}