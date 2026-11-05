import { generateKeyPairSync } from 'node:crypto';
import { BadRequestException, ConflictException, ForbiddenException, Logger, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
import { ClientKeyService } from '../src/auth/client-key.service';
import { DPoPKeyService } from '../src/auth/dpop-key.service';
import type { JourneyOptions } from '../src/configuration/journey-options';
import { PersonalDataErasureService } from '../src/privacy/personal-data-erasure.service';
import { TokenExchangeService } from '../src/services/token-exchange.service';
import { received, resetWireMock, stub, wireMockUrl } from '../../../../../tst/node/wiremock/wiremock';

/** This API's DPoP key, for the test run (the real one is read from a file). */
const DPOP = new DPoPKeyService(generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey);
/** This API's client key (private_key_jwt), for the test run; the IDP's discovery names its issuer. */
const clientKeyOf = (options: JourneyOptions) =>
  new ClientKeyService(generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey, options.clientId, options.authority);

// ┌─ :: ARCHITECTURE + SECURITY :: Personal-data erasure, the Customer Onboarding API played by WireMock
// │ Why : An erasure cannot be undone: every call must be made FOR the operator with the narrowest
// │       token, the Customer Onboarding API's own refusals (not eligible, four eyes, already decided,
// │       MFA) must reach the operator as they are - and its internals never.
// │ How : The REAL PersonalDataErasureService and TokenExchangeService; WireMock plays the IDP and the
// │       Customer Onboarding API; each test stages the answers, then checks the result or the
// │       exception, and what WireMock received.
// │ Ref : src/privacy/personal-data-erasure.service.ts | doc/Crypto-Shredding.md | RFC 8693 | CWE-209
// └────────────────────────────────────────────────────────────────────────────────────────────────
describe('PersonalDataErasureService - the IDP and the Customer Onboarding API played by WireMock', () => {
  const OPERATOR_TOKEN = 'operator-delegated-token';
  const ERASURES = '/v1/customers/personal-data-erasures';
  const PENDING = {
    erasureRequestId: 7, customerNumber: 'CUS-000123', reasonCode: 'CUSTOMER_REQUEST', status: 'PENDING', requestedBy: 'daniel.ops',
    requestedAt: '2026-11-04T10:00:00Z', expiresAt: '2026-11-11T10:00:00Z', decidedBy: null, decidedAt: null, decisionRemarks: null, mayDecide: true,
  };
  let service: PersonalDataErasureService;

  /** The IDP answers each scope with a token named after it ("for-customer-onboarding.read"). */
  const idpExchangesPerScope = async () => {
    for (const scope of ['customer-onboarding.read', 'customer-onboarding.write']) {
      await stub({
        request: { method: 'POST', urlPath: '/connect/token', bodyPatterns: [{ contains: `scope=${scope}` }] },
        response: { status: 200, jsonBody: { access_token: `for-${scope}`, token_type: 'Bearer', expires_in: 300 } },
      });
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
    await stub({ request: { method: 'GET', urlPath: '/.well-known/openid-configuration' }, response: { status: 200, jsonBody: { issuer: 'https://idp.ewp.example' } } });
    const options = { authority: wireMockUrl(), clientId: 'Operations.JourneyApi.ClientID', customerOnboardingApiBaseUrl: wireMockUrl() } as JourneyOptions;
    service = new PersonalDataErasureService(options, new TokenExchangeService(options, DPOP, clientKeyOf(options)), DPOP);
  });

  it('Given erasure requests, when they are listed, then the Customer Onboarding API is asked with a READ token bound to this API', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('GET', ERASURES, 200, [PENDING]);

    // When
    const requests = await service.list(OPERATOR_TOKEN);

    // Then
    expect(requests).toEqual([PENDING]);
    const [call] = await received('GET', ERASURES);
    expect(call.headers.Authorization).toBe('DPoP for-customer-onboarding.read');
    expect(call.headers.DPoP).toBeDefined();
  });

  it('Given a customer number and a reason code, when an erasure is requested, then only those two go out, with a WRITE token', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', ERASURES, 201, { erasureRequestId: 7, customerNumber: 'CUS-000123', status: 'PENDING' });

    // When
    const result = await service.request(OPERATOR_TOKEN, 'CUS-000123', 'RETENTION_ENDED');

    // Then
    expect(result).toEqual({ erasureRequestId: 7, customerNumber: 'CUS-000123', status: 'PENDING' });
    const [call] = await received('POST', ERASURES);
    expect(call.headers.Authorization).toBe('DPoP for-customer-onboarding.write');
    expect(JSON.parse(call.body)).toEqual({ customerNumber: 'CUS-000123', reasonCode: 'RETENTION_ENDED' });
  });

  it('Given a pending request, when it is approved, then the approve address is called with no remarks', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', `${ERASURES}/7/approve`, 200, { erasureRequestId: 7, customerNumber: 'CUS-000123', status: 'ERASED' });

    // When
    const result = await service.decide(OPERATOR_TOKEN, 7, true);

    // Then
    expect(result.status).toBe('ERASED');
    const [call] = await received('POST', `${ERASURES}/7/approve`);
    expect(call.headers.Authorization).toBe('DPoP for-customer-onboarding.write');
    expect(JSON.parse(call.body)).toEqual({});
  });

  it('Given a pending request, when it is rejected, then the remarks are sent to the reject address', async () => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', `${ERASURES}/7/reject`, 200, { erasureRequestId: 7, customerNumber: 'CUS-000123', status: 'REJECTED' });

    // When
    await service.decide(OPERATOR_TOKEN, 7, false, 'The customer withdrew the request.');

    // Then
    const [call] = await received('POST', `${ERASURES}/7/reject`);
    expect(JSON.parse(call.body)).toEqual({ remarks: 'The customer withdrew the request.' });
  });

  it.each([
    ['mfa_required', 'Confirm with your authenticator code.'],
    ['separation_of_duties', 'Four eyes: the person who requested the erasure cannot decide it.'],
  ])('Given the Customer Onboarding API refuses with %s (403), when the requester decides, then the reason and its code reach the screen', async (code, detail) => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', `${ERASURES}/7/approve`, 403, { code, detail });

    // When
    const error = await failureOf(service.decide(OPERATOR_TOKEN, 7, true));

    // Then
    expect(error).toBeInstanceOf(ForbiddenException);
    expect((error as ForbiddenException).getResponse()).toEqual({ message: detail, code });
  });

  it.each([
    [422, { detail: 'Only a prospect\'s personal data can be erased: this customer is ACTIVE.' }, BadRequestException, 'Only a prospect\'s personal data can be erased: this customer is ACTIVE.'],
    [409, { detail: 'The request was already decided (ERASED).' }, ConflictException, 'The request was already decided (ERASED).'],
    [404, { detail: 'Customer \'CUS-999\' was not found in customers.' }, NotFoundException, 'No such customer or erasure request.'],
    [403, { detail: 'policy PersonalDataErasureRequest failed' }, ForbiddenException, 'You are not allowed to do this.'],
    [500, { detail: 'Npgsql.PostgresException: ck_personal_data_erasure_requests_four_eyes' }, ServiceUnavailableException, 'The Customer Onboarding service could not answer. Try again shortly.'],
  ])('Given the Customer Onboarding API answers %i, when an erasure is requested, then the operator sees only what is meant for them', async (status, body, type, message) => {
    // Given
    await idpExchangesPerScope();
    await answers('POST', ERASURES, status, body);

    // When
    const error = await failureOf(service.request(OPERATOR_TOKEN, 'CUS-999', 'CUSTOMER_REQUEST'));

    // Then (CWE-209: the domain's own refusals pass, its internals never)
    expect(error).toBeInstanceOf(type);
    expect(error.message).toBe(message);
  });

  it('Given the Customer Onboarding API redirects elsewhere, when an erasure is approved, then the redirect is not followed and the write token goes nowhere else', async () => {
    // Given
    await idpExchangesPerScope();
    await stub({ request: { method: 'POST', urlPath: `${ERASURES}/7/approve` }, response: { status: 307, headers: { Location: `${wireMockUrl()}/elsewhere` } } });
    await answers('POST', '/elsewhere', 200, {});

    // When
    const error = await failureOf(service.decide(OPERATOR_TOKEN, 7, true));

    // Then
    expect(error).toBeInstanceOf(ServiceUnavailableException);
    expect(await received('POST', '/elsewhere')).toHaveLength(0);
  });
});