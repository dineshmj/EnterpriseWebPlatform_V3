'use server';

import { cookies } from 'next/headers';
import { config, SESSION_COOKIE } from '@/lib/server/config';
import { exchangeForJourneyApi, freshAccessToken } from '@/lib/server/oidc';
import { readSession, updateSession } from '@/lib/server/session-store';
import type { ActionResult, AuditPage, IntegrityResult, RecordJourney, SearchInput } from '@/lib/audit-types';

/*
 * The light BFF's server actions - the browser's ajax calls in the customer's pattern
 * ("Next.js SPA --ajax--> server action --M2M--> Journey API"). They run only on the server:
 * the browser never sees a token. Next.js accepts a server action only from this app's own
 * origin (its built-in CSRF check), and every action re-reads the session.
 */

export async function searchTrail(input: SearchInput): Promise<ActionResult<AuditPage>> {
  const params = new URLSearchParams();
  for (const [name, value] of Object.entries(input)) {
    if (value !== undefined && value !== null && String(value).trim() !== '') params.set(name, String(value).trim().slice(0, 100));
  }
  return callJourney<AuditPage>(`/v1/journeys/audit/entries?${params}`);
}

export async function openRecord(recordRef: string): Promise<ActionResult<RecordJourney>> {
  if (!/^[A-Za-z0-9-]{1,100}$/.test(recordRef)) return { ok: false, error: 'Unknown record reference.' };
  return callJourney<RecordJourney>(`/v1/journeys/audit/records/${encodeURIComponent(recordRef)}`);
}

export async function verifyIntegrity(): Promise<ActionResult<IntegrityResult>> {
  return callJourney<IntegrityResult>('/v1/journeys/audit/integrity');
}

async function callJourney<T>(path: string): Promise<ActionResult<T>> {
  const cookieStore = await cookies();
  const cookieValue = cookieStore.get(SESSION_COOKIE)?.value;
  const session = await readSession(cookieValue);
  if (!session || !cookieValue) return { ok: false, signedOut: true, error: 'Your session has ended.' };

  let journeyToken: string;
  try {
    const { token, refreshed } = await freshAccessToken(session);
    if (refreshed) await updateSession(cookieValue, refreshed);
    // Hop 1: the person's token -> a delegated token for the Journey API (act = this BFF).
    journeyToken = await exchangeForJourneyApi(token);
  } catch {
    return { ok: false, signedOut: true, error: 'Your sign-in could not be renewed.' };
  }

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 15_000);
  try {
    const response = await fetch(`${config.journeyApiBaseUrl}${path}`, {
      headers: { Authorization: `Bearer ${journeyToken}`, Accept: 'application/json' },
      cache: 'no-store',
      signal: controller.signal,
    });
    if (response.ok) return { ok: true, data: (await response.json()) as T };
    if (response.status === 401) return { ok: false, signedOut: true, error: 'Your session has ended.' };
    if (response.status === 403) return { ok: false, error: 'You are not allowed to see the audit trail.' };
    if (response.status === 404) return { ok: false, error: 'Nothing was found for this record.' };
    return { ok: false, error: 'The audit service is unavailable. Try again shortly.' };
  } catch {
    return { ok: false, error: 'The audit service is unavailable. Try again shortly.' };
  } finally {
    clearTimeout(timer);
  }
}