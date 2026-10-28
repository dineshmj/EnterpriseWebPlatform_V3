import {
  BadRequestException,
  ConflictException,
  ForbiddenException,
  Logger,
  ServiceUnavailableException,
} from '@nestjs/common';
import type { JourneyOptions } from '../src/configuration/journey-options';
import { DomainApiClient } from '../src/services/domain-api.client';
import { TokenExchangeService } from '../src/services/token-exchange.service';
import { received, resetWireMock, stub, wireMockUrl } from '../../../../../tst/node/wiremock/wiremock';

// ┌─ :: ARCHITECTURE + SECURITY :: The Journey API's two neighbours, played by WireMock ───────────
// │ Why : What the counter sees depends on how this API turns the IDP's and the Domain API's
// │       answers into its own - the person's token exchanged for exactly one scope, the domain's
// │       own refusals passed on, its internals never.
// │ How : The REAL DomainApiClient and TokenExchangeService (no mocks inside); WireMock stands in
// │       for the IDP's token endpoint and the Partner Products Domain API; each test stages the
// │       answers, then checks the exception the controller would turn into Problem Details, and
// │       what WireMock received.
// │ Ref : src/services/domain-api.client.ts | Features 2.1.2 | RFC 8693 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('DomainApiClient - the IDP and the Partner Products Domain API played by WireMock', () => {
  const PERSON_TOKEN = 'person-delegated-token';
  const WHAT = 'Partner Products';
  let client: DomainApiClient;

  const domainUrl = (path: string) => `${wireMockUrl()}/v1/partner-products${path}`;

  const idpExchangesFor = (accessToken: string) => stub({
    request: { method: 'POST', urlPath: '/connect/token' },
    response: { status: 200, jsonBody: { access_token: accessToken, token_type: 'Bearer', expires_in: 300 } },
  });

  const domainAnswers = (method: 'GET' | 'POST', path: string, status: number, jsonBody?: unknown) => stub({
    request: { method, urlPath: `/v1/partner-products${path}` },
    response: { status, jsonBody },
  });

  /** The error a call fails with (a call that succeeds fails the test). */
  const failureOf = (call: Promise<unknown>): Promise<Error> =>
    call.then(() => { throw new Error('The call was expected to fail.'); }, (error: Error) => error);

  beforeAll(() => Logger.overrideLogger(false));   // the client's warnings are expected here

  beforeEach(async () => {
    await resetWireMock();
    const options = { authority: wireMockUrl(), clientId: 'PartnerProducts.JourneyApi.ClientID', clientSecret: 'test-only' } as JourneyOptions;
    client = new DomainApiClient(new TokenExchangeService(options));   // a new service: an empty token cache
  });

  it('Given a person\'s token, when the Domain API is read, then the token is exchanged for that one scope and the exchanged token is sent', async () => {
    // Given
    await idpExchangesFor('exchanged-for-partner-products-read');
    await domainAnswers('GET', '/gold-account-applications', 200, [{ number: 'DGA-261021-TEST01' }]);

    // When
    const applications = await client.get<unknown[]>(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT);

    // Then: RFC 8693 for exactly this scope ...
    expect(applications).toEqual([{ number: 'DGA-261021-TEST01' }]);
    const [exchange] = await received('POST', '/connect/token');
    const form = new URLSearchParams(exchange.body);
    expect(form.get('grant_type')).toBe('urn:ietf:params:oauth:grant-type:token-exchange');
    expect(form.get('subject_token')).toBe(PERSON_TOKEN);
    expect(form.get('scope')).toBe('partner-products.read');

    // ... and the Domain API sees the exchanged token, never the person's own.
    const [call] = await received('GET', '/v1/partner-products/gold-account-applications');
    expect(call.headers.Authorization).toBe('Bearer exchanged-for-partner-products-read');
  });

  it('Given a token already exchanged, when the same scope is used again and then another, then the IDP is asked once per scope', async () => {
    // Given
    await idpExchangesFor('exchanged');
    await domainAnswers('GET', '/gold-account-applications', 200, []);
    await domainAnswers('POST', '/gold-purchases', 201, { number: 'DGP-261021-TEST01' });

    // When
    await client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT);
    await client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT);
    await client.post(PERSON_TOKEN, 'partner-products.write', domainUrl('/gold-purchases'), { amount: 20 }, WHAT);

    // Then: a token for one scope is never reused for another.
    const scopes = (await received('POST', '/connect/token')).map(r => new URLSearchParams(r.body).get('scope'));
    expect(scopes).toEqual(['partner-products.read', 'partner-products.write']);
  });

  it('Given the IDP refuses to delegate, when the Domain API is called, then it is forbidden and the Domain API is never called', async () => {
    // Given
    await stub({ request: { method: 'POST', urlPath: '/connect/token' }, response: { status: 400, jsonBody: { error: 'invalid_grant' } } });

    // When
    const call = client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT);

    // Then
    await expect(call).rejects.toBeInstanceOf(ForbiddenException);
    expect(await received('GET', '/v1/partner-products/gold-account-applications')).toHaveLength(0);
  });

  it('Given the Domain API refuses with its own reason (409), when a purchase is recorded, then that reason reaches the counter', async () => {
    // Given
    await idpExchangesFor('exchanged');
    await domainAnswers('POST', '/gold-purchases', 409, { title: 'Conflict', detail: 'This slip was already recorded as DGP-261021-TEST01.' });

    // When
    const call = client.post(PERSON_TOKEN, 'partner-products.write', domainUrl('/gold-purchases'), { amount: 20 }, WHAT);

    // Then
    await expect(call).rejects.toBeInstanceOf(ConflictException);
    await expect(call).rejects.toThrow('This slip was already recorded as DGP-261021-TEST01.');
  });

  it('Given a validation problem (400), when a purchase is recorded, then the first message reaches the counter', async () => {
    // Given
    await idpExchangesFor('exchanged');
    await domainAnswers('POST', '/gold-purchases', 400, { title: 'Invalid', errors: { amount: ['The amount must be at least AUD 10.'] } });

    // When
    const call = client.post(PERSON_TOKEN, 'partner-products.write', domainUrl('/gold-purchases'), { amount: 5 }, WHAT);

    // Then
    await expect(call).rejects.toBeInstanceOf(BadRequestException);
    await expect(call).rejects.toThrow('The amount must be at least AUD 10.');
  });

  it('Given the Domain API fails inside (500), when it is called, then the counter gets a plain "try again" - never the internal text', async () => {
    // Given
    await idpExchangesFor('exchanged');
    await domainAnswers('GET', '/gold-account-applications', 500, { detail: 'Npgsql.PostgresException: relation "gold_purchases" does not exist' });

    // When
    const error = await failureOf(client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT));

    // Then (CWE-209)
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(error.message).toBe('Partner Products could not answer. Try again shortly.');
    expect(error.message).not.toContain('Npgsql');
  });

  it('Given the Domain API says what to do (503), when a Form VII is sent, then its own instruction reaches the counter', async () => {
    // Given: the Partner Products API could not reach the Partner Bank.
    await idpExchangesFor('exchanged');
    await domainAnswers('POST', '/gold-account-applications/DGA-261021-TEST01/send', 503,
      { detail: 'The Partner Bank could not be reached. The Form VII stays SENT: send it again shortly.' });

    // When
    const call = client.postViaPartnerBank(PERSON_TOKEN, 'partner-products.write', domainUrl('/gold-account-applications/DGA-261021-TEST01/send'), WHAT);

    // Then
    await expect(call).rejects.toBeInstanceOf(ServiceUnavailableException);
    await expect(call).rejects.toThrow('The Form VII stays SENT: send it again shortly.');
  });

  it('Given the Domain API refuses this person (403), when it is called, then the counter gets a plain "not allowed"', async () => {
    // Given
    await idpExchangesFor('exchanged');
    await domainAnswers('GET', '/gold-account-applications', 403, { detail: 'ACCOUNT_NOT_IN_BRANCH: SYD001 cannot see MEL002' });

    // When
    const error = await failureOf(client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    expect(error.message).toBe('You are not allowed to do this.');
  });

  it('Given the Domain API is unreachable, when it is called, then the counter is told it is unavailable', async () => {
    // Given: a port nobody listens on.
    await idpExchangesFor('exchanged');

    // When
    const call = client.get(PERSON_TOKEN, 'partner-products.read', 'http://127.0.0.1:9/v1/partner-products/gold-account-applications', WHAT);

    // Then
    await expect(call).rejects.toBeInstanceOf(ServiceUnavailableException);
    await expect(call).rejects.toThrow('Partner Products is unavailable. Try again shortly.');
  });

  it('Given the Domain API redirects elsewhere, when it is called, then the redirect is not followed and the exchanged token goes nowhere else', async () => {
    // Given
    await idpExchangesFor('exchanged');
    await stub({ request: { method: 'GET', urlPath: '/v1/partner-products/gold-account-applications' }, response: { status: 302, headers: { Location: `${wireMockUrl()}/elsewhere` } } });
    await stub({ request: { method: 'GET', urlPath: '/elsewhere' }, response: { status: 200, jsonBody: [] } });

    // When
    const call = client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT);

    // Then
    await expect(call).rejects.toBeInstanceOf(ServiceUnavailableException);
    expect(await received('GET', '/elsewhere')).toHaveLength(0);
  });

  it('Given the IDP\'s token endpoint redirects (307), when a token is exchanged, then the client secret and the person\'s token are not sent on', async () => {
    // Given: a 307 keeps the method and the body - the form with the client secret.
    await stub({ request: { method: 'POST', urlPath: '/connect/token' }, response: { status: 307, headers: { Location: `${wireMockUrl()}/elsewhere/token` } } });
    await stub({ request: { method: 'POST', urlPath: '/elsewhere/token' }, response: { status: 200, jsonBody: { access_token: 'stolen', expires_in: 300 } } });

    // When
    const call = client.get(PERSON_TOKEN, 'partner-products.read', domainUrl('/gold-account-applications'), WHAT);

    // Then
    await expect(call).rejects.toThrow('The identity provider is unavailable. Try again shortly.');
    expect(await received('POST', '/elsewhere/token')).toHaveLength(0);
    expect(await received('GET', '/v1/partner-products/gold-account-applications')).toHaveLength(0);
  });
});