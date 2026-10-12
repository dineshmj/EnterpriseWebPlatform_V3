'use server';

import { cookies } from 'next/headers';
import { config, SESSION_COOKIE } from '@/lib/server/config';
import { exchangeForJourneyApi, freshAccessToken } from '@/lib/server/oidc';
import { readSession, updateSession } from '@/lib/server/session-store';
import { BEHAVIOURS, type ActionResult, type Behaviour, type PlatformHealth, type SimulatorState } from '@/lib/operations-types';

/*
 * The light BFF's server actions - the browser's ajax calls in the customer's pattern
 * ("Next.js SPA --ajax--> server action --token exchange--> Journey API"). They run only on the
 * server: the browser never sees a token. Next.js accepts a server action only from this app's
 * own origin (its built-in CSRF check), and every action re-reads the session. Each exchange asks
 * for the narrowest scope: read, or write for a simulator change.
 */

export async function loadPlatformHealth(): Promise<ActionResult<PlatformHealth>> {
  return callJourney<PlatformHealth>('operations-journey.read', 'GET', '/v1/journeys/operations/health');
}

export async function loadSimulators(): Promise<ActionResult<SimulatorState[]>> {
  return callJourney<SimulatorState[]>('operations-journey.read', 'GET', '/v1/journeys/operations/simulators');
}

export async function changeSimulator(key: string, behaviour: Behaviour): Promise<ActionResult<SimulatorState[]>> {
  if (!/^[a-z-]{1,40}$/.test(key) || !BEHAVIOURS.includes(behaviour)) return { ok: false, error: 'Unknown simulator or behaviour.' };
  return callJourney<SimulatorState[]>('operations-journey.write', 'PUT', `/v1/journeys/operations/simulators/${key}/behaviour`, { behaviour });
}

async function callJourney<T>(
  scope: 'operations-journey.read' | 'operations-journey.write', method: 'GET' | 'PUT', path: string, body?: unknown,
): Promise<ActionResult<T>> {
  const cookieStore = await cookies();
  const cookieValue = cookieStore.get(SESSION_COOKIE)?.value;
  const session = await readSession(cookieValue);
  if (!session || !cookieValue) return { ok: false, signedOut: true, error: 'Your session has ended.' };

  let journeyToken: string;
  try {
    const { token, refreshed } = await freshAccessToken(session);
    if (refreshed) await updateSession(cookieValue, refreshed);
    // The person's token -> a delegated token for the Journey API (act = this BFF).
    journeyToken = await exchangeForJourneyApi(token, scope);
  } catch {
    return { ok: false, signedOut: true, error: 'Your sign-in could not be renewed.' };
  }

  try {
    const response = await fetch(`${config.journeyApiBaseUrl}${path}`, {
      method,
      headers: { Authorization: `Bearer ${journeyToken}`, Accept: 'application/json', ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined,
      cache: 'no-store',
      signal: AbortSignal.timeout(15_000),
    });
    if (response.ok) return { ok: true, data: (await response.json()) as T };
    if (response.status === 401) return { ok: false, signedOut: true, error: 'Your session has ended.' };
    if (response.status === 403) {
      const problem = (await response.json().catch(() => null)) as { code?: string; message?: string } | null;
      return { ok: false, error: problem?.code === 'mfa_required' && problem.message ? problem.message : 'You are not allowed to do this.' };
    }
    if (response.status === 404) return { ok: false, error: 'Not available in this environment.' };
    if (response.status === 503) {
      const problem = (await response.json().catch(() => null)) as { message?: string } | null;
      return { ok: false, error: problem?.message ?? 'The operations service is unavailable. Try again shortly.' };
    }
    return { ok: false, error: 'The operations service is unavailable. Try again shortly.' };
  } catch {
    return { ok: false, error: 'The operations service is unavailable. Try again shortly.' };
  }
}