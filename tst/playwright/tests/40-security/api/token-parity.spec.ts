import { expect, request, test, type APIRequestContext } from '@playwright/test';
import { authorizationFor, clientCredentialsToken, clientIds } from '../../../support/tokens';

/**
 * Token parity: the .NET domain APIs and the NestJS journey APIs refuse the same bad bearer tokens
 * (401) - none, malformed, and a REAL token issued by the IDP for another API (wrong audience).
 * Plain HTTP; the IDP's keys, issuer and audience are checked on both stacks.
 *
 * The wrong-audience token is a client-credentials token of the KYC case-opening subscriber
 * (audience: the Customer KYC API), obtained with that subscriber's DEVELOPMENT-ONLY key
 * (support/tokens.ts) and sent the way the subscriber sends it - DPoP-bound, with a proof - so
 * only its audience is wrong.
 */
const apis = [
  { name: 'Payments API (.NET)', url: 'https://payments-api.dev.localhost:44488/v1/payments' },
  { name: 'Operations Journey API (NestJS)', url: 'https://operations-journey.dev.localhost:46382/v1/journeys/operations/parked-messages' },
];

async function wrongAudienceToken(http: APIRequestContext): Promise<string> {
  return clientCredentialsToken(http, clientIds.kycCaseOpeningSubscriber, 'customer-kyc.write');
}

test.describe('Token parity - .NET and NestJS refuse the same tokens', () => {
  let http: APIRequestContext;
  let otherApisToken: string;

  test.beforeAll(async () => {
    http = await request.newContext({ ignoreHTTPSErrors: true });
    otherApisToken = await wrongAudienceToken(http);
  });
  test.afterAll(async () => { await http.dispose(); });

  for (const api of apis) {
    test(`${api.name} ShouldRefuse_NoTokenAMalformedTokenAndAnotherApisToken`, async () => {
      // Arrange
      const attempts: Record<string, Record<string, string>> = {
        'no token': {},
        'a malformed token': { Authorization: 'Bearer not.a.jwt' },
        'a token for another API': authorizationFor(otherApisToken, 'GET', api.url),
      };

      for (const [label, headers] of Object.entries(attempts)) {
        // Act
        const response = await http.get(api.url, { headers });

        // Assert
        expect(response.status(), `${api.name} with ${label}`).toBe(401);
      }
    });
  }
});