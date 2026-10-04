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

  if (!response.ok) {
    throw new Error(errorMessage(text, response.status));
  }

  let data: T;
  try { data = text ? JSON.parse(text) as T : ({} as T); }
  catch { data = {} as T; }

  return { status: response.status, data };
}

async function readError(response: Response): Promise<string> {
  return errorMessage(await response.text(), response.status);
}

/**
 * Extracts a human-readable reason from an error body. Handles both the BFF's
 * own { message } responses and ProblemDetails ({ detail, title }) relayed from
 * the Customer Onboarding API, e.g. the branch-scope 403.
 */
function errorMessage(text: string, status: number): string {
  const fallback = `Request failed with HTTP ${status}.`;
  if (!text) return fallback;

  try {
    const data = JSON.parse(text) as { detail?: string; title?: string; message?: string };
    return data.detail ?? data.message ?? data.title ?? fallback;
  } catch {
    return fallback;
  }
}