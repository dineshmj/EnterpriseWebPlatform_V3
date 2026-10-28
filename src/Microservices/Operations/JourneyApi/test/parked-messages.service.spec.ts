import { BadRequestException, ConflictException, ForbiddenException, Logger, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import type { JourneyOptions } from '../src/configuration/journey-options';
import { ParkedMessagesService } from '../src/recovery/parked-messages.service';
import { TokenExchangeService } from '../src/services/token-exchange.service';
import { received, resetWireMock, stub, wireMockUrl } from '../../../../../tst/node/wiremock/wiremock';

// ┌─ :: ARCHITECTURE + SECURITY :: Parked messages of three contexts, their APIs played by WireMock ─
// │ Why : The operator's one screen over Accounts, Compliance and Partner Products must stay up
// │       when one of them is down, must not hide a refusal, and must never show a domain API's
// │       internals - and every call is made FOR the operator, with a token for that one context.
// │ How : The REAL ParkedMessagesService and TokenExchangeService; WireMock plays the IDP and the
// │       three Domain APIs' outbox endpoints; each test stages the answers, then checks the result
// │       or the exception, and what WireMock received.
// │ Ref : src/recovery/parked-messages.service.ts | Features 1.6.2, 2.1.2 | RFC 8693 | Threat-Model R-5 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('ParkedMessagesService - the IDP and three Domain APIs played by WireMock', () => {
  const OPERATOR_TOKEN = 'operator-delegated-token';
  const MESSAGE = {
    messageId: 'M1', eventType: 'AccountOpened', aggregateType: 'Account', aggregateId: 'A1', occurredAt: '2026-10-28T00:00:00Z',
    attemptCount: 10, lastAttemptAt: null, lastError: null, heldBack: 0, redriveCount: 0, lastRedrivenAt: null, lastRedrivenBy: null, lastRedriveReason: null,
  };
  let service: ParkedMessagesService;

  const parked = (context: string) => `/v1/${context}/outbox/parked`;

  /** The IDP answers each scope with a token named after it ("for-accounts.read"). */
  const idpExchangesPerScope = async () => {
    for (const context of ['accounts', 'compliance', 'partner-products']) {
      for (const access of ['read', 'write']) {
        const scope = `${context}.${access}`;
        await stub({
          request: { method: 'POST', urlPath: '/connect/token', bodyPatterns: [{ contains: `scope=${scope}` }] },
          response: { status: 200, jsonBody: { access_token: `for-${scope}`, token_type: 'Bearer', expires_in: 300 } },
        });
      }
    }
  };

  const answers = (method: 'GET' | 'POST', path: string, status: number, jsonBody?: unknown) =>
    stub({ request: { method, urlPath: path }, response: { status, jsonBody } });

  /** The error a call fails with (a call that succeeds fails the test). */
  const failureOf = (call: Promise<unknown>): Promise<Error> =>
    call.then(() => { throw new Error('The call was expected to fail.'); }, (error: Error) => error);

  beforeAll(() => Logger.overrideLogger(false));   // the service's warnings are expected here

  beforeEach(async () => {
    await resetWireMock();
    const options = {
      authority: wireMockUrl(), clientId: 'Operations.JourneyApi.ClientID', clientSecret: 'test-only',
      accountsApiBaseUrl: wireMockUrl(), complianceApiBaseUrl: wireMockUrl(), partnerProductsApiBaseUrl: wireMockUrl(),
    } as JourneyOptions;
    service = new ParkedMessagesService(options, new TokenExchangeService(options));
  });

  it('Given parked messages in each context, when they are listed, then each context is asked with its own read token', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('GET', parked('accounts'), 200, [MESSAGE]);
    await answers('GET', parked('compliance'), 200, []);
    await answers('GET', parked('partner-products'), 200, []);

    // When
    const all = await service.listAll(OPERATOR_TOKEN);

    // Then
    expect(all).toEqual([
      { context: 'accounts', available: true, messages: [MESSAGE] },
      { context: 'compliance', available: true, messages: [] },
      { context: 'partner-products', available: true, messages: [] },
    ]);
    for (const context of ['accounts', 'compliance', 'partner-products']) {
      const [call] = await received('GET', parked(context));
      expect(call.headers.Authorization).toBe(`Bearer for-${context}.read`);
    }
  });

  it('Given Compliance is down (500) and Partner Products unreachable, when the messages are listed, then those two are "unavailable" and Accounts is still shown', async () => {
    // Given: Partner Products' address is a port nobody listens on.
    await idpExchangesPerScope();
    await answers('GET', parked('accounts'), 200, [MESSAGE]);
    await answers('GET', parked('compliance'), 500, { detail: 'Npgsql.PostgresException: relation "outbox" does not exist' });
    const options = {
      authority: wireMockUrl(), clientId: 'Operations.JourneyApi.ClientID', clientSecret: 'test-only',
      accountsApiBaseUrl: wireMockUrl(), complianceApiBaseUrl: wireMockUrl(), partnerProductsApiBaseUrl: 'http://127.0.0.1:9',
    } as JourneyOptions;
    service = new ParkedMessagesService(options, new TokenExchangeService(options));

    // When
    const all = await service.listAll(OPERATOR_TOKEN);

    // Then
    expect(all).toEqual([
      { context: 'accounts', available: true, messages: [MESSAGE] },
      { context: 'compliance', available: false, messages: [] },
      { context: 'partner-products', available: false, messages: [] },
    ]);
  });

  it('Given one context refuses the operator (403), when the messages are listed, then the refusal is not hidden as "unavailable"', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('GET', parked('accounts'), 200, []);
    await answers('GET', parked('compliance'), 403);
    await answers('GET', parked('partner-products'), 200, []);

    // When
    const error = await failureOf(service.listAll(OPERATOR_TOKEN));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    expect(error.message).toBe('You are not allowed to do this.');
  });

  it('Given the IDP refuses to delegate (400), when the messages are listed, then it is forbidden and no Domain API is called', async () => {
    // Given
    await stub({ request: { method: 'POST', urlPath: '/connect/token' }, response: { status: 400, jsonBody: { error: 'invalid_grant' } } });

    // When
    const error = await failureOf(service.listAll(OPERATOR_TOKEN));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    for (const context of ['accounts', 'compliance', 'partner-products']) {
      expect(await received('GET', parked(context))).toHaveLength(0);
    }
  });

  it('Given a parked message, when it is resent with a reason, then the context\'s WRITE token and the reason are sent, and the message ID is encoded', async () => {
    // Given: an ID that would otherwise change the path.
    await idpExchangesPerScope();
    await answers('POST', '/v1/compliance/outbox/M%2F..%2F1/resend', 200, { messageId: 'M/../1', redriveCount: 1 });

    // When
    const result = await service.resend(OPERATOR_TOKEN, 'compliance', 'M/../1', 'Kafka was down; it is back.');

    // Then
    expect(result).toEqual({ messageId: 'M/../1', redriveCount: 1 });
    const [call] = await received('POST', '/v1/compliance/outbox/M%2F..%2F1/resend');
    expect(call.headers.Authorization).toBe('Bearer for-compliance.write');
    expect(JSON.parse(call.body)).toEqual({ reason: 'Kafka was down; it is back.' });
  });

  it('Given the operator\'s MFA is too old (403 mfa_required), when a message is resent, then the screen is told to step up', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', '/v1/accounts/outbox/M1/resend', 403, { code: 'mfa_required', detail: 'Sign in again with MFA to resend.' });

    // When
    const error = await failureOf(service.resend(OPERATOR_TOKEN, 'accounts', 'M1', 'retry'));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    expect((error as ForbiddenException).getResponse()).toEqual({ message: 'Sign in again with MFA to resend.', code: 'mfa_required' });
  });

  it.each([
    [400, { detail: 'A reason is required.' }, BadRequestException, 'A reason is required.'],
    [409, { detail: 'M1 is no longer parked.' }, ConflictException, 'M1 is no longer parked.'],
    [404, { detail: 'No such row in accounts.outbox_messages' }, NotFoundException, 'The message does not exist.'],
    [500, { detail: 'Npgsql.PostgresException: deadlock detected' }, ServiceUnavailableException, 'The accounts service could not answer. Try again shortly.'],
  ])('Given the Domain API answers %i, when a message is resent, then the operator sees only what is meant for them', async (status, body, type, message) => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', '/v1/accounts/outbox/M1/resend', status, body);

    // When
    const error = await failureOf(service.resend(OPERATOR_TOKEN, 'accounts', 'M1', 'retry'));

    // Then (CWE-209: the domain's own refusals pass, its internals never)
    expect(error).toBeInstanceOf(type);
    expect(error.message).toBe(message);
  });

  it('Given the Domain API redirects elsewhere, when a message is resent, then the redirect is not followed and the write token goes nowhere else', async () => {
    // Given
    await idpExchangesPerScope();
    await stub({ request: { method: 'POST', urlPath: '/v1/accounts/outbox/M1/resend' }, response: { status: 307, headers: { Location: `${wireMockUrl()}/elsewhere` } } });
    await answers('POST', '/elsewhere', 200, {});

    // When
    const error = await failureOf(service.resend(OPERATOR_TOKEN, 'accounts', 'M1', 'retry'));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(await received('POST', '/elsewhere')).toHaveLength(0);
  });
});