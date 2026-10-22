import { expect, request, type APIRequestContext, type APIResponse } from '@playwright/test';
import { test } from '../../../support/fixtures';
import { claimsOf, clientCredentialsToken, clientIds, exchange, partnerWebSignInToken } from '../../../support/tokens';

/**
 * Token exchange (RFC 8693) is an allow-list at the IDP: each client may exchange only the kind of
 * token it is meant to receive, and only a token that carries a person. Plain HTTP against the
 * IDP's token endpoint with real tokens - Sophie's own sign-in token of the Partner Products web
 * BFF, and machine tokens - presented by clients whose development secrets the test holds: what
 * a stolen client secret would allow. Doc: Features 2.1.2, Authorization-Model 9.3.
 */
// ┌─ :: SECURITY :: What this test proves ─────────────────────────────────────────────────────────
// │ Why : A service holding someone's token must not turn it into a token for any other service;
// │       a machine must never obtain a token that acts for a person.
// │ How : One exchange that IS allowed (the BFF, its own sign-in token -> its Journey API) proves
// │       the set-up; every other pairing is refused: a Journey API skipping its BFF, another BFF,
// │       a client without the grant, a machine token (no person).
// │ Ref : IDP Security/TokenExchangeGrantValidator.cs | RFC 8693 | OWASP API2 / API5 |
// │       AI review round 4 (Microsoft: "Playwright 40-security negatives")
// └────────────────────────────────────────────────────────────────────────────────────────────────
async function expectRefused(response: APIResponse, error: string, what: string): Promise<void> {
  const body = await response.json() as { error?: string; access_token?: string };
  expect(response.status(), `${what}: ${JSON.stringify(body)}`).toBe(400);
  expect(body.error, what).toBe(error);
  expect(body.access_token, `${what}: no token`).toBeUndefined();
}

test.describe('Token exchange - only allow-listed exchanges, only for a person', () => {
  let http: APIRequestContext;
  test.beforeAll(async () => { http = await request.newContext({ ignoreHTTPSErrors: true }); });
  test.afterAll(async () => { await http.dispose(); });

  test('ShouldExchange_TheBffsOwnSignInToken_ForItsJourneyApi_KeepingThePerson', async ({ signedInAs }) => {
    // Arrange
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), http);

    // Act
    const response = await exchange(http, clientIds.partnerProductsWeb, signIn, 'partner-products-journey.read');

    // Assert: the person stays the subject; the BFF is named as the acting client.
    expect(response.status(), await response.text()).toBe(200);
    const claims = claimsOf((await response.json() as { access_token: string }).access_token);
    expect(claims.sub).toBe(claimsOf(signIn).sub);
    expect(claims.act).toEqual({ client_id: clientIds.partnerProductsWeb });
    expect(claims.aud).toContain('partner-products-journey-api');
  });

  test('ShouldRefuse_AJourneyApiExchangingTheSignInTokenDirectly', async ({ signedInAs }) => {
    // Arrange: the Journey API may exchange only a token its BFF exchanged for it.
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), http);

    // Act
    const response = await exchange(http, clientIds.partnerProductsJourneyApi, signIn, 'partner-products.write');

    // Assert
    await expectRefused(response, 'invalid_grant', 'the Journey API skipping its BFF');
  });

  test('ShouldRefuse_AnotherBffExchangingThisBffsSignInToken', async ({ signedInAs }) => {
    // Arrange
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), http);

    // Act: the Audit web BFF presents the Partner Products web BFF's token.
    const response = await exchange(http, clientIds.auditWeb, signIn, 'audit-journey.read');

    // Assert
    await expectRefused(response, 'invalid_grant', 'another BFF\'s sign-in token');
  });

  test('ShouldRefuse_AClientWithoutTheTokenExchangeGrant', async ({ signedInAs }) => {
    // Arrange: the Payments BFF and the Worker are not token-exchange clients at all.
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), http);

    for (const [clientId, scope] of [[clientIds.paymentsWeb, 'payments.read'], [clientIds.partnerProductsWorker, 'payments.read']]) {
      // Act
      const response = await exchange(http, clientId, signIn, scope);

      // Assert: refused before the allow-list is even consulted (Duende's answer for a grant
      // the client is not registered for).
      await expectRefused(response, 'unsupported_grant_type', `${clientId} using token exchange`);
    }
  });

  test('ShouldRefuse_ExchangingAMachineToken_ThereIsNoPersonToActFor', async () => {
    // Arrange: the Worker's own client-credentials token (no sub).
    const machine = await clientCredentialsToken(http, clientIds.partnerProductsWorker, 'payments.read partner-products.write');
    expect(claimsOf(machine).sub, 'a machine token has no person').toBeUndefined();

    for (const [clientId, scope] of [[clientIds.partnerProductsJourneyApi, 'payments.write'], [clientIds.partnerProductsWeb, 'partner-products-journey.read']]) {
      // Act
      const response = await exchange(http, clientId, machine, scope);

      // Assert
      await expectRefused(response, 'invalid_grant', `${clientId} exchanging a machine token`);
    }
  });
});