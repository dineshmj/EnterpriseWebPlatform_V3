'use server';

import { cookies } from 'next/headers';
import { config, SESSION_COOKIE } from '@/lib/server/config';
import { exchangeForJourneyApi, freshAccessToken } from '@/lib/server/oidc';
import { readSession, updateSession } from '@/lib/server/session-store';
import type { ActionResult, CustomerAtCounter, CustomerMatch, GoldApplication, GoldPurchase, PurchaseProgress } from '@/lib/partner-products-types';

/*
 * The light BFF's server actions - the browser's ajax calls in the customer's pattern
 * ("Next.js SPA --ajax--> server action --token exchange--> Journey API"). They run only on the
 * server: the browser never sees a token. Next.js accepts a server action only from this app's
 * own origin (its built-in CSRF check), and every action re-reads the session. Each exchange asks
 * for the narrowest scope: read, or write for recording a signed form.
 */

const READ = 'partner-products-journey.read';
const WRITE = 'partner-products-journey.write';
const MAX_SCAN_BYTES = 5 * 1024 * 1024;

export async function searchCustomers(search: string): Promise<ActionResult<CustomerMatch[]>> {
  const term = search.trim();
  if (term.length < 2 || term.length > 100) return { ok: false, error: 'Search with 2 to 100 characters of the customer\'s name or number.' };
  return callJourney<CustomerMatch[]>(READ, 'GET', `/v1/journeys/partner-products/customers?search=${encodeURIComponent(term)}`);
}

export async function loadCustomer(customerId: number): Promise<ActionResult<CustomerAtCounter>> {
  if (!Number.isInteger(customerId) || customerId <= 0) return { ok: false, error: 'Unknown customer.' };
  return callJourney<CustomerAtCounter>(READ, 'GET', `/v1/journeys/partner-products/customers/${customerId}`);
}

// ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
// │ The screen follows a purchase that waits for its payment with this light read: the
// │ customer's Form VII and slips from the Partner Products API only (its own database,
// │ the staff member's branch) - no Customer Onboarding, Accounts or Partner Bank call.
// │ It is not the person's activity, so it does not extend the session.
// └──────────────────────────────────────────────────────────────────────────────────────
export async function loadPurchases(customerNumber: string): Promise<ActionResult<CustomerAtCounter['digitalGold']>> {
  if (!/^CUST-\d{1,12}$/.test(customerNumber)) return { ok: false, error: 'Unknown customer.' };
  return callJourney<CustomerAtCounter['digitalGold']>(READ, 'GET', `/v1/journeys/partner-products/customers/${customerNumber}/digital-gold`, undefined, { slide: false });
}

export async function loadApplications(): Promise<ActionResult<GoldApplication[]>> {
  return callJourney<GoldApplication[]>(READ, 'GET', '/v1/journeys/partner-products/applications');
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Only an EWP application number (DGA-yyMMdd-XXXXXX) goes into the path. The keys that
// │ reach the Partner Bank never pass through here: the Partner Products API holds them.
// └──────────────────────────────────────────────────────────────────────────────────────
export async function sendFormVii(applicationNumber: string): Promise<ActionResult<GoldApplication>> {
  if (!/^DGA-\d{6}-[A-Z0-9]{6}$/.test(applicationNumber)) return { ok: false, error: 'Unknown application.' };
  return callJourney<GoldApplication>(WRITE, 'POST', `/v1/journeys/partner-products/applications/${applicationNumber}/send`);
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Only an EWP purchase number goes into the path: the amount, the paying account and the
// │ payee are never sent from the browser - they are EWP's record of the signed slip.
// └──────────────────────────────────────────────────────────────────────────────────────
export async function buyGold(purchaseNumber: string): Promise<ActionResult<PurchaseProgress>> {
  if (!/^DGP-\d{6}-[A-Z0-9]{6}$/.test(purchaseNumber)) return { ok: false, error: 'Unknown purchase.' };
  return callJourney<PurchaseProgress>(WRITE, 'POST', `/v1/journeys/partner-products/purchases/${purchaseNumber}/buy`);
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The address comes back from EWP's own servers (the configured Partner Bank page + a
// │ one-time request_uri); it is opened only if it is https - never an address from the
// │ browser.
// └──────────────────────────────────────────────────────────────────────────────────────
export async function openPartnerView(applicationNumber: string): Promise<ActionResult<{ url: string; expiresInSeconds: number }>> {
  if (!/^DGA-\d{6}-[A-Z0-9]{6}$/.test(applicationNumber)) return { ok: false, error: 'Unknown application.' };
  const result = await callJourney<{ url: string; expiresInSeconds: number }>(READ, 'POST', `/v1/journeys/partner-products/applications/${applicationNumber}/partner-view`);
  if (result.ok && !result.data.url.startsWith('https://')) return { ok: false, error: 'The Partner Bank\'s address is not usable.' };
  return result;
}

export async function recordFormVii(customerId: number, form: FormData): Promise<ActionResult<GoldApplication>> {
  return recordSigned<GoldApplication>(customerId, 'form-vii', form, ['formNumber', 'formSignedOn', 'dateOfBirth', 'identityChecked', 'signatureChecked']);
}

export async function recordTransferSlip(customerId: number, form: FormData): Promise<ActionResult<GoldPurchase>> {
  return recordSigned<GoldPurchase>(customerId, 'transfer-slips', form, ['slipNumber', 'slipSignedOn', 'amount', 'debtorAccountNumber', 'mobileOnSlip', 'signatureChecked']);
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Only the known fields (strings, at most 100 characters) and ONE scan (at most 5 MB)
// │ are passed on - never the browser's form as it came: whatever else it carries is
// │ dropped here. The Journey API and the Domain API check every field again.
// └──────────────────────────────────────────────────────────────────────────────────────
async function recordSigned<T>(customerId: number, kind: 'form-vii' | 'transfer-slips', form: FormData, fields: string[]): Promise<ActionResult<T>> {
  if (!Number.isInteger(customerId) || customerId <= 0) return { ok: false, error: 'Unknown customer.' };
  const scan = form.get('scan');
  if (!(scan instanceof File) || scan.size === 0) return { ok: false, error: 'Attach the scan of the signed document.' };
  if (scan.size > MAX_SCAN_BYTES) return { ok: false, error: 'The scan is larger than 5 MB.' };

  const outgoing = new FormData();
  for (const name of fields) {
    const value = form.get(name);
    if (typeof value === 'string' && value.length <= 100) outgoing.append(name, value);
  }
  outgoing.append('scan', scan, scan.name.slice(-100));
  return callJourney<T>(WRITE, 'POST', `/v1/journeys/partner-products/customers/${customerId}/${kind}`, outgoing);
}

// ┌─ :: ARCHITECTURE + SECURITY :: Server actions: two hops, no token in the browser ──────────────
// │ Why : The screen needs data from domain APIs, yet must never hold a token or reach them
// │       directly.
// │ How : A server action reads the session, exchanges the person's token for the Journey API,
// │       which exchanges again for each domain API; Problem Details (detail) come back to the
// │       screen, written for the counter.
// │ Ref : Features 3.3, 2.1.2 | Customer pattern (Next.js BFF -> NestJS Journey API -> domain API)
// └────────────────────────────────────────────────────────────────────────────────────────────────
async function callJourney<T>(
  scope: typeof READ | typeof WRITE, method: 'GET' | 'POST', path: string, body?: FormData, { slide = true }: { slide?: boolean } = {},
): Promise<ActionResult<T>> {
  const cookieStore = await cookies();
  const cookieValue = cookieStore.get(SESSION_COOKIE)?.value;
  const session = await readSession(cookieValue, { slide });
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
      // A FormData body sets its own multipart Content-Type (with the boundary).
      headers: { Authorization: `Bearer ${journeyToken}`, Accept: 'application/json' },
      body,
      cache: 'no-store',
      // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
      // │ Calls on to the Partner Bank (sending, paying, a staff view) may take the Journey API up to 40 s: wait for its own answer.
      // └──────────────────────────────────────────────────────────────────────────────────────
      signal: AbortSignal.timeout(scope === WRITE || method === 'POST' ? 60_000 : 30_000),
    });
    if (response.ok) return { ok: true, data: (await response.json()) as T };
    if (response.status === 401) return { ok: false, signedOut: true, error: 'Your session has ended.' };
    if (response.status === 403) return { ok: false, error: 'You are not allowed to do this.' };
    if (response.status === 400 || response.status === 404 || response.status === 409 || response.status === 413) {
      // Our own refusal texts (a form signed too long ago, a slip already used) - shown as they are.
      const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
      return { ok: false, error: problem?.detail ?? (response.status === 413 ? 'The scan is too large.' : 'The request was refused.') };
    }
    if (response.status === 429) return { ok: false, error: 'Too many requests. Wait a minute, then try again.' };
    // 503 with our own text, e.g. "The Partner Bank could not be reached. Form VII stays SENT: send it again later".
    const problem = (await response.json().catch(() => null)) as { detail?: string } | null;
    return { ok: false, error: problem?.detail ?? 'The Partner Products service is unavailable. Try again shortly.' };
  } catch {
    return { ok: false, error: 'The Partner Products service is unavailable. Try again shortly.' };
  }
}