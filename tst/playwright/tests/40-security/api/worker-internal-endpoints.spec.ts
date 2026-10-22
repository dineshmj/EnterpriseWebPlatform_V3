import { randomUUID } from 'node:crypto';
import { expect, request, type APIRequestContext } from '@playwright/test';
import { test } from '../../../support/fixtures';
import { clientCredentialsToken, clientIds, exchanged, partnerWebSignInToken } from '../../../support/tokens';

/**
 * The Partner Products Worker's internal endpoints (the purchase workflow's follow-up: purchase
 * state, payment advice, cancellation; a payment's outcome by its Idempotency-Key) accept ONLY the
 * Worker's own machine token. A staff member's token - even one for the right API, with the right
 * scope, obtained through the real delegation chain - and another machine's token are refused.
 * Plain HTTP to the APIs' own addresses. Doc: Partner Products API README (internal endpoints),
 * Payments API README.
 */
// ┌─ :: SECURITY :: What this test proves ─────────────────────────────────────────────────────────
// │ Why : The internal endpoints skip the staff checks (branch, permission) because a machine calls
// │       them; any other caller reaching them would bypass those checks (OWASP API5, BFLA).
// │ How : The client ID is pinned (policies DigitalGoldWorker / PaymentOutcomeForPartnerProducts),
// │       and on the Partner Products API a token with a person (sub / act) never passes. The
// │       Worker's own token gets 404 for a purchase / payment that does not exist - proof that it
// │       passed the check and that the refusals below are the policy, not a broken set-up.
// │ Ref : Threat-Model TB6 | OWASP API5 | AI review round 4 (Microsoft: "worker internal endpoints")
// └────────────────────────────────────────────────────────────────────────────────────────────────
const partnerProductsApi = 'https://partner-products-api.dev.localhost:46387';
const paymentsApi = 'https://payments-api.dev.localhost:44488';

test.describe('Worker internal endpoints - the Worker only', () => {
  let http: APIRequestContext;
  let worker: string;
  test.beforeAll(async () => {
    http = await request.newContext({ ignoreHTTPSErrors: true });
    worker = await clientCredentialsToken(http, clientIds.partnerProductsWorker, 'payments.read partner-products.write');
  });
  test.afterAll(async () => { await http.dispose(); });

  const bearer = (token: string) => ({ Authorization: `Bearer ${token}` });

  test('PartnerProductsApi ShouldRefuse_AStaffMembersDelegatedToken_AndNoToken', async ({ signedInAs }) => {
    // Arrange: Sophie's token for the Partner Products API, through the real chain
    // (web BFF -> Journey API -> Partner Products API, scope partner-products.write).
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), http);
    const forJourneyApi = await exchanged(http, clientIds.partnerProductsWeb, signIn, 'partner-products-journey.write');
    const staff = await exchanged(http, clientIds.partnerProductsJourneyApi, forJourneyApi, 'partner-products.write');
    const purchase = `${partnerProductsApi}/internal/v1/gold-purchases/DGP-000000-NONE0`;

    // Act + Assert
    expect((await http.get(purchase)).status(), 'no token').toBe(401);
    expect((await http.get(purchase, { headers: bearer(staff) })).status(), 'a staff member\'s delegated token').toBe(403);
    expect((await http.post(`${purchase}/cancel`, { headers: bearer(staff), data: { reason: 'Playwright' } })).status(),
      'a staff member cancelling through the Worker\'s endpoint').toBe(403);
    expect((await http.post(`${purchase}/payment-advice`, {
      headers: bearer(staff), data: { paymentRef: randomUUID(), amount: 20 },
    })).status(), 'a staff member advising a payment through the Worker\'s endpoint').toBe(403);
    expect((await http.get(purchase, { headers: bearer(worker) })).status(), 'the Worker itself: passes, nothing found').toBe(404);
  });

  test('PaymentsApi ShouldRefuse_AStaffMembersDelegatedToken_AnotherMachine_AndNoToken', async ({ signedInAs }) => {
    // Arrange: Sophie's delegated token for the Payments API (the Journey API pays for the
    // purchase as her) and the saga reply subscriber's own token for the same API.
    const signIn = await partnerWebSignInToken(await signedInAs('sophie.cs'), http);
    const forJourneyApi = await exchanged(http, clientIds.partnerProductsWeb, signIn, 'partner-products-journey.write');
    const staff = await exchanged(http, clientIds.partnerProductsJourneyApi, forJourneyApi, 'payments.write');
    const otherMachine = await clientCredentialsToken(http, clientIds.paymentsSagaReplySubscriber, 'payments.write');
    const outcome = `${paymentsApi}/internal/v1/payments/by-idempotency-key/${randomUUID()}`;

    // Act + Assert
    expect((await http.get(outcome)).status(), 'no token').toBe(401);
    expect((await http.get(outcome, { headers: bearer(staff) })).status(), 'a staff member\'s delegated token').toBe(403);
    expect((await http.get(outcome, { headers: bearer(otherMachine) })).status(), 'another machine client of the Payments API').toBe(403);
    expect((await http.get(outcome, { headers: bearer(worker) })).status(), 'the Worker itself: passes, nothing found').toBe(404);
  });
});