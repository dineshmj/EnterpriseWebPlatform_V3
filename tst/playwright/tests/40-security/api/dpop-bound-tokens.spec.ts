import { randomUUID } from 'node:crypto';
import { expect, test } from '../../../support/fixtures';
import { urls } from '../../../support/services';
import {
  accountsWebSignInToken, authorizationFor, claimsOf, clientAuthenticationOf, clientCredentialsToken, clientIds, exchanged,
  partnerWebSignInToken,
} from '../../../support/tokens';

/**
 * ┌─ :: SECURITY :: A DPoP-bound token is useless without its key ────────────────────────────────
 * │ Why : The staff web apps' tokens are bound to their DPoP key (RFC 9449, FAPI 2.0). A token
 * │       copied from a log, a memory dump or a proxy must not work on its own - and neither may a
 * │       copied proof.
 * │ How : Jack's Accounts web app token (bound). With a fresh proof the Accounts API answers; the
 * │       same token as Bearer is refused (401); the same proof sent again is refused (replay).
 * │       The same three checks at a NestJS Journey API, with Sophie's delegated token - and for a
 * │       machine: a Kafka subscriber's own token (client credentials, private_key_jwt) is bound
 * │       too; the IDP issues none without a proof, and a proof made for another address fails.
 * │ Ref : Features 2.1.1, 2.1.2 | API READMEs (DPoP row) | RFC 9449 §4.3, §11.1 | Threat-Model R-9
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
test.describe('DPoP - a bound token needs its key, and a proof works once', () => {
  test('ShouldRefuse_ABoundTokenAsBearer_AndAReplayedProof', async ({ signedInAs, request }) => {
    // Arrange
    const token = await accountsWebSignInToken(await signedInAs('jack.accounts'), request);
    const accounts = `${urls.accountsApi}/v1/accounts?pageSize=1`;
    const withProof = authorizationFor(token, 'GET', accounts);
    expect(withProof.Authorization, 'the Accounts web app\'s token is DPoP-bound').toMatch(/^DPoP /);

    // Act + Assert 1: with its proof, the token works.
    expect((await request.get(accounts, { headers: withProof })).status(), 'token + fresh proof').toBe(200);

    // Act + Assert 2: the same token as a bearer token is refused - and the API says why.
    const asBearer = await request.get(accounts, { headers: { Authorization: `Bearer ${token}` } });
    expect(asBearer.status(), 'a bound token sent as Bearer').toBe(401);
    expect(asBearer.headers()['www-authenticate'] ?? '').toContain('cnf');

    // Act + Assert 3: the proof already used is refused (its jti is remembered).
    expect((await request.get(accounts, { headers: withProof })).status(), 'the same proof again').toBe(401);
  });

  test('JourneyApi ShouldAnswer_ABoundTokenWithItsProof_AndRefuseItAsBearer_AndAReplayedProof', async ({ signedInAs, request }) => {
    // Arrange: Sophie's token for the Partner Products Journey API, exchanged by its web BFF -
    // bound to the BFF's key (the NestJS guard checks the proof itself).
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), request);
    const token = await exchanged(request, clientIds.partnerProductsWeb, signIn, 'partner-products-journey.read');
    expect(claimsOf(token).cnf, 'the delegated token is DPoP-bound').toBeTruthy();
    const applications = `${urls.partnerProductsJourneyApi}/v1/journeys/partner-products/applications`;
    const withProof = authorizationFor(token, 'GET', applications);

    // Act + Assert 1: with its proof the Journey API answers - after its own exchange and its own
    // DPoP-bound call to the Partner Products API (the whole chain is sender-constrained).
    expect((await request.get(applications, { headers: withProof })).status(), 'token + fresh proof').toBe(200);

    // Act + Assert 2: the same token as Bearer is refused - the guard takes the DPoP scheme only.
    expect((await request.get(applications, { headers: { Authorization: `Bearer ${token}` } })).status(), 'a bound token sent as Bearer').toBe(401);

    // Act + Assert 3: the proof already used is refused (its jti is remembered).
    expect((await request.get(applications, { headers: withProof })).status(), 'the same proof again').toBe(401);
  });

  test('Machine ShouldBindASubscribersToken_AndRefuseItAsBearer_AReplayedProof_AndAProofForAnotherAddress', async ({ request }) => {
    // Arrange: the Payments saga reply subscriber's own token, obtained as the subscriber does -
    // a client assertion signed with its key (no secret) and a DPoP proof.
    const clientId = clientIds.paymentsSagaReplySubscriber;
    const tokenEndpoint = `${urls.idp}/connect/token`;
    const token = await clientCredentialsToken(request, clientId, 'payments.write');
    expect(claimsOf(token).cnf, 'the machine token is DPoP-bound').toBeTruthy();
    // An internal endpoint pinned to ANOTHER machine: 403 proves the token and proof were accepted
    // (authenticated, then refused by the policy), 401 that they were not.
    const outcome = `${urls.paymentsApi}/internal/v1/payments/by-idempotency-key/${randomUUID()}`;
    const withProof = authorizationFor(token, 'GET', outcome);

    // Act + Assert 1: the IDP issues no token to this client without a proof.
    const unbound = await request.post(tokenEndpoint, {
      form: { grant_type: 'client_credentials', client_id: clientId, ...await clientAuthenticationOf(request, clientId), scope: 'payments.write' },
    });
    expect(unbound.status(), 'a token request without a DPoP proof').toBe(400);

    // Act + Assert 2: with its proof, the token is accepted.
    expect((await request.get(outcome, { headers: withProof })).status(), 'token + fresh proof: authenticated').toBe(403);

    // Act + Assert 3: the same token as Bearer is refused.
    expect((await request.get(outcome, { headers: { Authorization: `Bearer ${token}` } })).status(), 'a bound token sent as Bearer').toBe(401);

    // Act + Assert 4: the proof already used is refused (its jti is remembered).
    expect((await request.get(outcome, { headers: withProof })).status(), 'the same proof again').toBe(401);

    // Act + Assert 5: a fresh proof made for another address is refused (htu).
    const forElsewhere = authorizationFor(token, 'GET', `${urls.paymentsApi}/v1/payments`);
    expect((await request.get(outcome, { headers: forElsewhere })).status(), 'a proof for another address').toBe(401);
  });
});