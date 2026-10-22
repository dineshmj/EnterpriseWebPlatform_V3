import { randomUUID } from 'node:crypto';
import { expect, test } from '../../../support/fixtures';
import { bffSessionFor } from '../../../support/bff';
import { fundedCustomer, payees } from '../../../support/journeys/payments';
import { resetAllSimulators } from '../../../support/simulators';
import { urls } from '../../../support/services';

/**
 * Branch scope holds even when the screen is skipped: Mia (customer service, Melbourne MEL001)
 * cannot find a Sydney customer's account, and a payment she sends straight to the Payments BFF
 * from that account - with its real BSB and number - is refused by Accounts
 * (ACCOUNT_NOT_IN_BRANCH): no funds are held, the payment ends REJECTED, and Sophie (Sydney)
 * does not see Mia's payment. Doc: BSS-Requirements.md section 10, Authorization-Model (ABAC).
 */
// ┌─ :: SECURITY :: What this test proves ─────────────────────────────────────────────────────────
// │ Why : The account search is filtered by branch, but a script in the browser can send any
// │       account number; only the owner of the data (Accounts) can refuse it reliably.
// │ How : Accounts checks the INITIATOR's branch (carried by the saga from her token) against the
// │       account's branch before it holds anything - Account.RefusalToHold.
// │ Ref : AI review 1 fix R1 | OWASP API1 (BOLA) | AI review round 4 (Microsoft: "out-of-branch")
// └────────────────────────────────────────────────────────────────────────────────────────────────
interface PayerAccount { customerNumber: string; bsb: string; accountNumber: string }
interface Initiated { paymentId: number; paymentNumber: string; status: string }
interface Detail { paymentNumber: string; status: string; outcomeCode?: string | null; branchCode: string }

test.describe('Payments - branch scope at the data owner', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('ShouldRefuse_APaymentFromAnotherBranchesAccount_SentPastTheScreen', async ({ signedInAs }) => {
    // Arrange: a Sydney customer with a funded account (Sophie's branch) and its account details.
    const customer = await fundedCustomer(signedInAs, 'OutOfBranch');
    const sophie = await bffSessionFor(await signedInAs('sophie.cs'), urls.paymentsMfe);
    const sydney = await sophie.request.get(`${urls.paymentsMfe}/bff/api/payer-accounts?search=${encodeURIComponent(customer)}`);
    expect(sydney.status()).toBe(200);
    const payer = (await sydney.json() as PayerAccount[]).find(a => a.customerNumber === customer);
    expect(payer, `a paying account of ${customer}`).toBeTruthy();

    const mia = await bffSessionFor(await signedInAs('mia.cs'), urls.paymentsMfe);

    // Act + Assert 1: Mia's search does not find the Sydney customer's account.
    const search = await mia.request.get(`${urls.paymentsMfe}/bff/api/payer-accounts?search=${encodeURIComponent(customer)}`);
    expect(search.status()).toBe(200);
    expect((await search.json() as PayerAccount[]).filter(a => a.customerNumber === customer), 'Mia sees no Sydney account').toEqual([]);

    // Act 2: she sends the payment anyway, with the account's real details.
    const sent = await mia.request.post(`${urls.paymentsMfe}/bff/api/payments`, {
      data: {
        customerNumber: customer, fromBsb: payer!.bsb, fromAccountNumber: payer!.accountNumber,
        payeeName: payees.matching.name, toBsb: payees.matching.bsb, toAccountNumber: payees.matching.accountNumber,
        amount: 25, reference: 'Out of branch',
      },
      headers: { 'X-CSRF-TOKEN': await mia.csrfToken(), 'Idempotency-Key': randomUUID() },
    });
    expect(sent.status(), await sent.text()).toBe(202);
    const payment = await sent.json() as Initiated;

    // Assert 2: Accounts refuses to hold the funds - the payment ends REJECTED for that reason.
    let detail: Detail | undefined;
    await expect(async () => {
      const response = await mia.request.get(`${urls.paymentsMfe}/bff/api/payments/${payment.paymentId}`);
      expect(response.status()).toBe(200);
      detail = await response.json() as Detail;
      expect(detail.status).toBe('REJECTED');
    }).toPass({ timeout: 90_000, intervals: [1_000, 2_000, 3_000] });
    expect(detail!.outcomeCode).toBe('ACCOUNT_NOT_IN_BRANCH');
    expect(detail!.branchCode, 'the payment belongs to the initiator\'s branch').toBe('MEL001');

    // Assert 3: Sophie's branch does not see Mia's payment.
    expect((await sophie.request.get(`${urls.paymentsMfe}/bff/api/payments/${payment.paymentId}`)).status(), 'Sophie reading Mia\'s payment').toBe(404);
  });
});