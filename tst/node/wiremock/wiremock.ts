/**
 * WireMock's admin API, as the tests use it: forget everything, stage an answer, read what
 * arrived. Plain fetch - the same calls any language makes (a client library such as
 * wiremock-captain wraps exactly these). Shared by every Node.js service's tests: Jest's
 * global set-up and tear-down (wiremock-start.ts, wiremock-stop.ts) live next to it.
 */

/** What WireMock recorded about one request it received. */
export interface LoggedRequest {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string;
}

/** How a request must look to match: method and path, and optionally headers and body. */
export interface RequestPattern {
  method: string;
  urlPath: string;
  /** e.g. { 'X-Simulator-Admin-Key': { equalTo: 'key' } } or { 'X-Simulator-Admin-Key': { absent: true } } */
  headers?: Record<string, { equalTo: string } | { absent: true }>;
  /** e.g. [{ contains: 'scope=audit.read' }] */
  bodyPatterns?: { contains: string }[];
}

/** What WireMock answers: a status with a JSON or plain body, or a broken connection (fault). */
export interface StubResponse {
  status?: number;
  jsonBody?: unknown;
  /** A body that is not JSON (a page, a truncated answer). */
  body?: string;
  headers?: Record<string, string>;
  fixedDelayMilliseconds?: number;
  /** The connection itself fails: the caller gets no HTTP answer at all. */
  fault?: 'CONNECTION_RESET_BY_PEER' | 'EMPTY_RESPONSE';
}

/** A stub: when a request matches `request`, WireMock answers with `response`. */
export interface StubMapping {
  request: RequestPattern;
  response: StubResponse;
  scenarioName?: string;
  requiredScenarioState?: string;
  newScenarioState?: string;
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

/**
 * The same request answered differently each time it arrives (a WireMock scenario): the first
 * answer, then the second, ...; the last one repeats. For a retry: "503, then 200".
 */
export async function answersInTurn(request: RequestPattern, ...responses: StubResponse[]): Promise<void> {
  const scenarioName = `${request.method} ${request.urlPath}`;
  for (const [index, response] of responses.entries()) {
    const last = index === responses.length - 1;
    await stub({
      request, response, scenarioName,
      requiredScenarioState: index === 0 ? 'Started' : `answer-${index + 1}`,
      ...(last ? {} : { newScenarioState: `answer-${index + 2}` }),
    });
  }
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