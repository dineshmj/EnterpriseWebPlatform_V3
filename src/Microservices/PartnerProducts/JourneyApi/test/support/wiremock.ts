/**
 * WireMock's admin API, as the tests use it: forget everything, stage an answer, read what
 * arrived. Plain fetch - the same calls any language makes (a client library such as
 * wiremock-captain wraps exactly these).
 */

/** What WireMock recorded about one request it received. */
export interface LoggedRequest {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string;
}

/** A stub: when a request matches `request`, WireMock answers with `response`. */
export interface StubMapping {
  request: { method: string; urlPath: string };
  response: { status: number; jsonBody?: unknown; fixedDelayMilliseconds?: number };
}

export function wireMockUrl(): string {
  const url = process.env.WIREMOCK_URL;
  if (!url) throw new Error('WIREMOCK_URL is not set: run the tests with "pnpm test" (Jest starts WireMock).');
  return url;
}

/** Removes every stub and every recorded request - each test starts from nothing. */
export async function resetWireMock(): Promise<void> {
  await admin('POST', '/__admin/reset');
}

export async function stub(mapping: StubMapping): Promise<void> {
  await admin('POST', '/__admin/mappings', mapping);
}

/** The requests WireMock received for this method and path, in order. */
export async function received(method: string, urlPath: string): Promise<LoggedRequest[]> {
  const found = await admin('POST', '/__admin/requests/find', { method, urlPath }) as { requests: LoggedRequest[] };
  return found.requests;
}

async function admin(method: 'POST' | 'GET', path: string, body?: unknown): Promise<unknown> {
  const response = await fetch(`${wireMockUrl()}${path}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) throw new Error(`WireMock ${method} ${path}: HTTP ${response.status} ${await response.text()}`);
  const text = await response.text();
  return text ? JSON.parse(text) : undefined;
}