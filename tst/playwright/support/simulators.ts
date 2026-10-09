import { request as playwrightRequest } from '@playwright/test';

/**
 * The stand-ins for external systems, and their behaviour switch (PUT /admin/behaviour, which
 * answers loopback callers only - these tests run on the same machine as the simulators).
 *
 * The switch is GLOBAL: it changes the simulator for everybody - other tests and anybody demoing.
 * So a behaviour is only ever set through withSimulator(), which ALWAYS puts the simulator back
 * to Healthy (and clears a forced outcome) when the test step ends, passed or failed.
 */
export const simulators = {
  screeningProvider: 'https://localhost:46366',
  coreBanking: 'https://localhost:46376',
  paymentNetwork: 'https://localhost:46386',
} as const;

export type SimulatorName = keyof typeof simulators;
export type Behaviour = 'Healthy' | 'Slow' | 'Failing' | 'Down' | 'Refusing';

export interface BehaviourSettings {
  behaviour: Behaviour;
  /** Screening provider only: CLEAR, POTENTIAL_MATCH or MATCH whatever the customer number. */
  forcedOutcome?: 'CLEAR' | 'POTENTIAL_MATCH' | 'MATCH' | null;
}

async function put(simulator: SimulatorName, settings: BehaviourSettings): Promise<void> {
  const api = await playwrightRequest.newContext({ ignoreHTTPSErrors: true });
  try {
    const response = await api.put(`${simulators[simulator]}/admin/behaviour`, { data: settings, timeout: 10_000 });
    if (!response.ok()) {
      throw new Error(`The ${simulator} simulator refused ${JSON.stringify(settings)}: HTTP ${response.status()}. Is it running (Visual Studio)?`);
    }
  } finally {
    await api.dispose();
  }
}

/** Sets a behaviour for the duration of `work`, then always restores Healthy. */
export async function withSimulator<T>(simulator: SimulatorName, settings: BehaviourSettings, work: () => Promise<T>): Promise<T> {
  await put(simulator, settings);
  try {
    return await work();
  } finally {
    await put(simulator, { behaviour: 'Healthy', ...(simulator === 'screeningProvider' ? { forcedOutcome: null } : {}) });
  }
}

/** Puts every simulator back to Healthy - for a test file's afterAll, in case a run was interrupted. */
export async function resetAllSimulators(): Promise<void> {
  await put('screeningProvider', { behaviour: 'Healthy', forcedOutcome: null });
  await put('coreBanking', { behaviour: 'Healthy' });
  await put('paymentNetwork', { behaviour: 'Healthy' });
}