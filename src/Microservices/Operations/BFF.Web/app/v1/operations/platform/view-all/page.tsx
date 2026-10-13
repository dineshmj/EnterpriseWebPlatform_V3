'use client';

import { Activity, FlaskConical, RefreshCw } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { formatTime, MfeShell, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge } from '@/app/components/ui/feedback';
import { Select } from '@/app/components/ui/form';
import {
  type Behaviour, type ComponentHealth, type HealthStatus, type PlatformHealth, type SimulatorState,
} from '@/lib/operations-types';
import { confirmWithAuthenticator, NOT_CONFIRMED } from '@/lib/step-up';
import { changeSimulator, loadPlatformHealth, loadSimulators } from '../../actions';

const REFRESH_MS = 10_000;

const TONE: Record<HealthStatus, 'success' | 'warning' | 'danger' | 'neutral'> = {
  Healthy: 'success', Degraded: 'warning', Unhealthy: 'danger', Unreachable: 'neutral',
};

const BEHAVIOUR_TONE: Record<Behaviour | 'Unknown', 'success' | 'warning' | 'danger' | 'neutral'> = {
  Healthy: 'success', Slow: 'warning', Failing: 'danger', Down: 'danger', Refusing: 'warning', LosingAnswers: 'danger', Unknown: 'neutral',
};

const BEHAVIOUR_LABEL: Record<Behaviour, string> = {
  Healthy: 'Healthy', Slow: 'Slow', Failing: 'Failing', Down: 'Down', Refusing: 'Refusing',
  LosingAnswers: 'Losing answers (sends, never replies)',
};

/**
 * The operations administrator's view of the platform: every component's readiness (refreshed
 * every 10 seconds) and - in Development and test only - the simulators' behaviour switch, for
 * demonstrating how the platform copes when an external system misbehaves.
 */
export default function PlatformHealthPage() {
  const [health, setHealth] = useState<PlatformHealth | null>(null);
  const [simulators, setSimulators] = useState<SimulatorState[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [simulatorError, setSimulatorError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    const result = await loadPlatformHealth();
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

  const change = async (simulator: SimulatorState, behaviour: Behaviour) => {
    setBusy(simulator.key);
    setSimulatorError(null);
    let result = await changeSimulator(simulator.key, behaviour);
    // A change needs a recent authenticator code: confirm it in a small window, then once more.
    if (!result.ok && result.stepUp) {
      try {
        result = await confirmWithAuthenticator()
          ? await changeSimulator(simulator.key, behaviour)
          : { ok: false, error: NOT_CONFIRMED };
      } catch (e) {
        result = { ok: false, error: e instanceof Error ? e.message : NOT_CONFIRMED };
      }
    }
    setBusy(null);
    if (result.ok) { setSimulators(result.data); void refresh(); return; }
    if (result.signedOut) { signInAgain(); return; }
    setSimulatorError(result.error);
  };

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
                        <Td><Badge tone={TONE[component.status]} dot>{component.status}</Badge></Td>
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
              description="Development and test only. Switch a simulator to see how the platform copes; every change is recorded with your LAN ID and goes back to Healthy by itself after 15 minutes."
            />
            <CardContent className="space-y-4">
              {simulatorError && <Alert tone="danger">{simulatorError}</Alert>}
              {!simulators && <p className="text-sm text-ink-muted">Reading the simulators…</p>}
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
                            onChange={(event) => void change(simulator, event.target.value as Behaviour)}
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

function countBy(components: ComponentHealth[]): Record<HealthStatus, number> {
  const counts: Record<HealthStatus, number> = { Healthy: 0, Degraded: 0, Unhealthy: 0, Unreachable: 0 };
  for (const component of components) counts[component.status]++;
  return counts;
}