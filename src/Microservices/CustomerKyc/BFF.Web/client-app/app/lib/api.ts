export async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: 'include', cache: 'no-store' });
  if (response.status === 401) {
    window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname)}`;
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

export async function postJson<T>(url: string, body: unknown): Promise<T> {
  const csrfToken = await getCsrfToken();
  const response = await fetch(url, {
    method: 'POST',
    credentials: 'include',
    cache: 'no-store',
    headers: {
      'Content-Type': 'application/json',
      'X-CSRF-Token': csrfToken,
    },
    body: JSON.stringify(body),
  });
  if (response.status === 401) {
    window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname)}`;
    throw new Error('Authentication required.');
  }
  const text = await response.text();
  if (!response.ok) throw new Error(errorMessage(text, response.status));
  return (text ? JSON.parse(text) : undefined) as T;
}

/**
 * Extracts a human-readable reason from an error body: the KYC API's { error },
 * the BFF's { message }, or ProblemDetails ({ detail, title }). A 403 without a
 * body (authorization denied, e.g. the case is assigned to another officer)
 * gets a clear default.
 */
function errorMessage(text: string, status: number): string {
  const fallback = status === 403
    ? 'You are not permitted to perform this action on this case (for example, it is assigned to another officer).'
    : `Request failed with HTTP ${status}.`;
  if (!text) return fallback;

  try {
    const data = JSON.parse(text) as { error?: string; detail?: string; message?: string; title?: string };
    return data.error ?? data.detail ?? data.message ?? data.title ?? fallback;
  } catch {
    return fallback;
  }
}