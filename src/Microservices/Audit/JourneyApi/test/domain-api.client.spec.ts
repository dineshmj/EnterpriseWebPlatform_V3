import { ForbiddenException, Logger, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import type { JourneyOptions } from '../src/configuration/journey-options';
import { DomainApiClient } from '../src/services/domain-api.client';
import { TokenExchangeService } from '../src/services/token-exchange.service';
import { answersInTurn, received, resetWireMock, stub, wireMockUrl } from '../../../../../tst/node/wiremock/wiremock';

// ┌─ :: ARCHITECTURE + SECURITY :: The auditor's two neighbours, played by WireMock ───────────────
// │ Why : What the auditor sees depends on how this API turns the IDP's and the domain APIs'
// │       answers into its own: a token per downstream scope (an Audit token never replayed at
// │       Payments), one safe retry for a read, the downstream's own text never passed on.
// │ How : The REAL DomainApiClient and TokenExchangeService (no mocks inside); WireMock stands in
// │       for the IDP's token endpoint, the Audit Domain API and the Payments API; each test
// │       stages the answers, then checks the result or the exception the controller turns into
// │       Problem Details, and what WireMock received.
// │ Ref : src/services/domain-api.client.ts | Features 2.1.2, 2.4.1 | RFC 8693 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('DomainApiClient - the IDP, the Audit Domain API and the Payments API played by WireMock', () => {
  const AUDITOR_TOKEN = 'auditor-delegated-token';
  const AUDIT_EVENTS = '/v1/audit/events';
  const PAYMENTS = '/v1/payments';
  const UNAVAILABLE = 'A service behind this one is unavailable. Try again shortly.';
  let client: DomainApiClient;

  const url = (path: string) => `${wireMockUrl()}${path}`;

  const idpExchanges = () => stub({
    request: { method: 'POST', urlPath: '/connect/token' },
    response: { status: 200, jsonBody: { access_token: 'exchanged', token_type: 'Bearer', expires_in: 300 } },
  });

  const answers = (path: string, status: number, jsonBody?: unknown) =>
    stub({ request: { method: 'GET', urlPath: path }, response: { status, jsonBody } });

  /** The error a call fails with (a call that succeeds fails the test). */
  const failureOf = (call: Promise<unknown>): Promise<Error> =>
    call.then(() => { throw new Error('The call was expected to fail.'); }, (error: Error) => error);

  beforeAll(() => Logger.overrideLogger(false));   // the client's warnings are expected here

  beforeEach(async () => {
    await resetWireMock();
    const options = { authority: wireMockUrl(), clientId: 'Audit.JourneyApi.ClientID', clientSecret: 'test-only' } as JourneyOptions;
    client = new DomainApiClient(new TokenExchangeService(options));   // a new service: an empty token cache
  });

  it('Given an auditor\'s token, when Audit and then Payments are read, then each gets its own exchanged token for its own scope', async () => {
    // Given: the IDP answers each scope with a different token.
    for (const scope of ['audit.read', 'payments.read']) {
      await stub({
        request: { method: 'POST', urlPath: '/connect/token', bodyPatterns: [{ contains: `scope=${scope}` }] },
        response: { status: 200, jsonBody: { access_token: `for-${scope}`, token_type: 'Bearer', expires_in: 300 } },
      });
    }
    await answers(AUDIT_EVENTS, 200, [{ eventId: 'E1' }]);
    await answers(PAYMENTS, 200, [{ paymentId: 'P1' }]);

    // When
    const events = await client.get<unknown[]>(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS));
    const payments = await client.get<unknown[]>(AUDITOR_TOKEN, 'payments.read', url(PAYMENTS));

    // Then: one exchange per scope, the auditor as the subject ...
    expect(events).toEqual([{ eventId: 'E1' }]);
    expect(payments).toEqual([{ paymentId: 'P1' }]);
    const exchanges = (await received('POST', '/connect/token')).map(r => new URLSearchParams(r.body));
    expect(exchanges.map(f => f.get('scope'))).toEqual(['audit.read', 'payments.read']);
    expect(exchanges.every(f => f.get('subject_token') === AUDITOR_TOKEN)).toBe(true);

    // ... and neither API sees the auditor's own token, nor the other API's.
    const [auditCall] = await received('GET', AUDIT_EVENTS);
    const [paymentsCall] = await received('GET', PAYMENTS);
    expect(auditCall.headers.Authorization).toBe('Bearer for-audit.read');
    expect(paymentsCall.headers.Authorization).toBe('Bearer for-payments.read');
  });

  it('Given the Audit API is briefly unavailable (503, then 200), when it is read, then the one retry brings the answer', async () => {
    // Given
    await idpExchanges();
    await answersInTurn({ method: 'GET', urlPath: AUDIT_EVENTS }, { status: 503 }, { status: 200, jsonBody: [{ eventId: 'E1' }] });

    // When
    const events = await client.get<unknown[]>(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS));

    // Then
    expect(events).toEqual([{ eventId: 'E1' }]);
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(2);
  });

  it('Given the connection is dropped once, when the Audit API is read, then the one retry brings the answer', async () => {
    // Given
    await idpExchanges();
    await answersInTurn({ method: 'GET', urlPath: AUDIT_EVENTS }, { fault: 'CONNECTION_RESET_BY_PEER' }, { status: 200, jsonBody: [] });

    // When
    const events = await client.get<unknown[]>(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS));

    // Then
    expect(events).toEqual([]);
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(2);
  });

  it.each([502, 503, 504])('Given the Audit API keeps answering %i, when it is read, then it is tried twice only and the auditor is told to try again', async (status) => {
    // Given
    await idpExchanges();
    await answers(AUDIT_EVENTS, status);

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(error.message).toBe(UNAVAILABLE);
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(2);
  });

  it('Given the Audit API fails inside (500), when it is read, then it is not retried and its internal text never reaches the auditor', async () => {
    // Given
    await idpExchanges();
    await answers(AUDIT_EVENTS, 500, { detail: 'Npgsql.PostgresException: relation "audit_events" does not exist' });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then (CWE-209)
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(error.message).toBe(UNAVAILABLE);
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(1);
  });

  it.each([401, 403])('Given the Payments API refuses (%i), when it is read, then the auditor gets a plain 403 and nothing is retried', async (status) => {
    // Given
    await idpExchanges();
    await answers(PAYMENTS, status, { detail: 'ACCOUNT_NOT_IN_BRANCH: SYD001 cannot see MEL002' });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'payments.read', url(PAYMENTS)));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    expect(error.message).toBe('You are not allowed to see this.');
    expect(await received('GET', PAYMENTS)).toHaveLength(1);
  });

  it('Given a payment that does not exist (404), when it is read, then the auditor gets "Not found."', async () => {
    // Given
    await idpExchanges();
    await answers(`${PAYMENTS}/P404`, 404, { detail: 'No payment P404 in branch SYD001.' });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'payments.read', url(`${PAYMENTS}/P404`)));

    // Then
    expect(error).toBeInstanceOf(NotFoundException);
    expect(error.message).toBe('Not found.');
  });

  it('Given an answer that is not JSON (200), when the Audit API is read, then it is "unavailable" - never passed through', async () => {
    // Given
    await idpExchanges();
    await stub({ request: { method: 'GET', urlPath: AUDIT_EVENTS }, response: { status: 200, body: '<html>Gateway login</html>' } });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(error.message).toBe(UNAVAILABLE);
  });

  it('Given the Audit API redirects elsewhere, when it is read, then the redirect is not followed and the exchanged token goes nowhere else', async () => {
    // Given
    await idpExchanges();
    await stub({ request: { method: 'GET', urlPath: AUDIT_EVENTS }, response: { status: 302, headers: { Location: url('/elsewhere') } } });
    await answers('/elsewhere', 200, []);

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(await received('GET', '/elsewhere')).toHaveLength(0);
  });

  it('Given the IDP refuses to delegate (400), when the Audit API is read, then it is forbidden and the Audit API is never called', async () => {
    // Given
    await stub({ request: { method: 'POST', urlPath: '/connect/token' }, response: { status: 400, jsonBody: { error: 'invalid_grant' } } });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(0);
  });

  it('Given the IDP\'s token endpoint redirects (307), when a token is exchanged, then the client secret and the auditor\'s token are not sent on', async () => {
    // Given: a 307 keeps the method and the body - the form with the client secret.
    await stub({ request: { method: 'POST', urlPath: '/connect/token' }, response: { status: 307, headers: { Location: url('/elsewhere/token') } } });
    await stub({ request: { method: 'POST', urlPath: '/elsewhere/token' }, response: { status: 200, jsonBody: { access_token: 'stolen', expires_in: 300 } } });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(error.message).toBe('The identity provider is unavailable. Try again shortly.');
    expect(await received('POST', '/elsewhere/token')).toHaveLength(0);
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(0);
  });

  it('Given the IDP fails (500), when the Audit API is read, then the auditor is told it is unavailable and the Audit API is never called', async () => {
    // Given
    await stub({ request: { method: 'POST', urlPath: '/connect/token' }, response: { status: 500 } });

    // When
    const error = await failureOf(client.get(AUDITOR_TOKEN, 'audit.read', url(AUDIT_EVENTS)));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(error.message).toBe('The identity provider could not exchange the token.');
    expect(await received('GET', AUDIT_EVENTS)).toHaveLength(0);
  });
});