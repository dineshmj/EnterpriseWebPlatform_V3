// After a silent sign-in the user comes back to the same page, query included (e.g. ?paymentId=).

import { confirmWithAuthenticator, needsStepUp, NOT_CONFIRMED } from './step-up';

export async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: 'include', cache: 'no-store' });
  if (response.status === 401) {
    window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search)}`;
    throw new Error('Authentication required.');
  }
  if (!response.ok) {
    throw new Error(errorMessage(await response.text(), response.status));
  }
  return response.json() as Promise<T>;
}

export async function getCsrfToken(): Promise<string> {
  const response = await fetch('/api/auth/csrf', { credentials: 'include', cache: 'no-store' });
  if (!response.ok) throw new Error(`Unable to obtain CSRF token (HTTP ${response.status}).`);
  const body = await response.json() as { token?: string };
  if (!body.token) throw new Error('CSRF token was not returned by the BFF.');
  return body.token;
}

export async function postJson<T>(url: string, body: unknown, extraHeaders: Record<string, string> = {}, afterStepUp = false): Promise<T> {
  const csrfToken = await getCsrfToken();
  const response = await fetch(url, {
    method: 'POST',
    credentials: 'include',
    cache: 'no-store',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-TOKEN': csrfToken,
      ...extraHeaders,
    },
    body: JSON.stringify(body),
  });
  if (response.status === 401) {
    window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname + window.location.search)}`;
    throw new Error('Authentication required.');
  }
  const text = await response.text();
  // A risky action whose authenticator code is too old: confirm it in a small window, then once more.
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ If the API answers 403 mfa_required, confirm with a fresh authenticator code in a
  // │ small window, then try ONCE more - never loop, never sign out.
  // └──────────────────────────────────────────────────────────────────────────────────────
  if (!afterStepUp && needsStepUp(response.status, text)) {
    if (await confirmWithAuthenticator()) return postJson<T>(url, body, extraHeaders, true);
    throw new Error(NOT_CONFIRMED);
  }
  if (!response.ok) throw new Error(errorMessage(text, response.status));
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * Extracts a human-readable reason from an error body: an API's { error }, the BFF's or
 * an API's { message }, or ProblemDetails ({ detail, title }). A 403 without a body gets
 * a clear default.
 */
function errorMessage(text: string, status: number): string {
  const fallback = status === 403
    ? 'You are not permitted to perform this action.'
    : `Request failed with HTTP ${status}.`;
  if (!text) return fallback;

  try {
    const data = JSON.parse(text) as { error?: string; detail?: string; message?: string; title?: string };
    return data.error ?? data.detail ?? data.message ?? data.title ?? fallback;
  } catch {
    return fallback;
  }
}