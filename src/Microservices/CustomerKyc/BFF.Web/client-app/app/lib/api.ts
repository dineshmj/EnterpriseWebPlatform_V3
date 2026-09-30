export async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: 'include', cache: 'no-store' });
  if (response.status === 401) {
    window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname)}`;
    throw new Error('Authentication required.');
  }
  if (!response.ok) {
    const text = await response.text();
    throw new Error(text || `Request failed with HTTP ${response.status}.`);
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
  if (!response.ok) throw new Error(text || `Request failed with HTTP ${response.status}.`);
  return (text ? JSON.parse(text) : undefined) as T;
}
