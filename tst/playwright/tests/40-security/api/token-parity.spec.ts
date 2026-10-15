import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, request, test, type APIRequestContext } from '@playwright/test';
import { urls } from '../../../support/services';

/**
 * Token parity: the .NET domain APIs and the NestJS journey APIs refuse the same bad bearer tokens
 * (401) - none, malformed, and a REAL token issued by the IDP for another API (wrong audience).
 * Plain HTTP; the IDP's keys, issuer and audience are checked on both stacks.
 *
 * The wrong-audience token is a client-credentials token of the KYC case-opening subscriber
 * (audience: the Customer KYC API). Its DEVELOPMENT-ONLY secret is read from that subscriber's
 * appsettings.Development.json at run time - never copied into the tests.
 */
const apis = [
  { name: 'Payments API (.NET)', url: 'https://payments-api.dev.localhost:44488/v1/payments' },
  { name: 'Operations Journey API (NestJS)', url: 'https://operations-journey.dev.localhost:46382/v1/journeys/operations/parked-messages' },
];

const subscriberSettings = resolve(__dirname, '../../../../../src/AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/appsettings.Development.json');
const subscriberClientId = 'CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID';

async function wrongAudienceToken(http: APIRequestContext): Promise<string> {
  const secret = (JSON.parse(readFileSync(subscriberSettings, 'utf-8')) as { KycCaseOpeningSubscriber?: { ClientSecret?: string } })
    .KycCaseOpeningSubscriber?.ClientSecret;
  expect(secret, 'the subscriber\'s development secret').toBeTruthy();
  const response = await http.post(`${urls.idp}/connect/token`, {
    form: { grant_type: 'client_credentials', client_id: subscriberClientId, client_secret: secret!, scope: 'customer-kyc.write' },
  });
  expect(response.status(), 'a token for the Customer KYC API').toBe(200);
  return (await response.json() as { access_token: string }).access_token;
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
        'a token for another API': { Authorization: `Bearer ${otherApisToken}` },
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