export async function getJson<T>(url: string): Promise<T> {
  const response = await fetch(url, { credentials: 'include', cache: 'no-store' });
  if (response.status === 401) {
    window.location.href = `/api/auth/silent-login?returnUrl=${encodeURIComponent(window.location.pathname)}`;
    throw new Error('Authentication required.');
  }
  if (!response.ok) {
    throw new Error(await readError(response));
  }
  return response.json() as Promise<T>;
}

export async function getCsrfToken(): Promise<string> {
  const response = await fetch('/api/auth/csrf', { credentials: 'include', cache: 'no-store' });
  if (!response.ok) throw new Error('Unable to establish the BFF CSRF token.');
  const data = await response.json() as { token: string };
  return data.token;
}

export async function postForm<T>(url: string, form: FormData, csrfToken: string): Promise<{ status: number; data: T }> {
  const response = await fetch(url, {
    method: 'POST',
    body: form,
    credentials: 'include',
    headers: { 'X-CSRF-TOKEN': csrfToken },
  });

  const text = await response.text();
  let data: T;
  try { data = text ? JSON.parse(text) as T : ({} as T); }
  catch { data = {} as T; }

  if (!response.ok) {
    const message = typeof data === 'object' && data !== null && 'message' in data
      ? String((data as { message?: unknown }).message)
      : `Request failed with HTTP ${response.status}.`;
    throw new Error(message);
  }

  return { status: response.status, data };
}

async function readError(response: Response): Promise<string> {
  const text = await response.text();
  try {
    const data = JSON.parse(text) as { detail?: string; title?: string; message?: string };
    return data.detail ?? data.message ?? data.title ?? `Request failed with HTTP ${response.status}.`;
  } catch {
    return text || `Request failed with HTTP ${response.status}.`;
  }
}
