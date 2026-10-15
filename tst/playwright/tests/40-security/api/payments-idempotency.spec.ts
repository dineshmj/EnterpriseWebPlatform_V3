import { randomUUID } from 'node:crypto';
import { expect, test } from '../../../support/fixtures';
import { bffSessionFor } from '../../../support/bff';
import { fundedCustomer, payees } from '../../../support/journeys/payments';
import { resetAllSimulators } from '../../../support/simulators';
import { urls } from '../../../support/services';

/**
 * Starting a payment is safe to repeat: the Idempotency-Key (one GUID per payment form) makes a
 * resend - a double click, a retry after a lost answer - find the SAME payment, never pay twice;
 * the same key with different details is refused (409). Checked through the Payments BFF, exactly
 * as the screen sends it. Doc: BSS-Requirements.md section 10.
 */
interface PayerAccount { customerNumber: string; bsb: string; accountNumber: string }
interface Initiated { paymentId: number; paymentNumber: string; status: string }

test.describe('Payments - idempotency', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('ShouldReturnTheSamePayment_ForTheSameIdempotencyKey_AndRefuseOtherDetails', async ({ signedInAs }) => {
    // Arrange: Sophie's Payments BFF session and the customer's paying account.
    const customer = await fundedCustomer(signedInAs, 'Idempotency');
    const bff = await bffSessionFor(await signedInAs('sophie.cs'), urls.paymentsMfe);
    const accounts = await bff.request.get(`${urls.paymentsMfe}/bff/api/payer-accounts?search=${encodeURIComponent(customer)}`);
    expect(accounts.status()).toBe(200);
    const payer = (await accounts.json() as PayerAccount[]).find(a => a.customerNumber === customer);
    expect(payer, `a paying account of ${customer}`).toBeTruthy();

    const key = randomUUID();
    const payment = {
      customerNumber: customer, fromBsb: payer!.bsb, fromAccountNumber: payer!.accountNumber,
      payeeName: payees.matching.name, toBsb: payees.matching.bsb, toAccountNumber: payees.matching.accountNumber,
      amount: 42.5, reference: 'Idempotency',
    };
    const initiate = async (body: object, idempotencyKey?: string) => bff.request.post(`${urls.paymentsMfe}/bff/api/payments`, {
      data: body,
      headers: { 'X-CSRF-TOKEN': await bff.csrfToken(), ...(idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {}) },
    });

    // Act + Assert 1: no key, no payment.
    expect((await initiate(payment)).status(), 'without an Idempotency-Key').toBe(400);

    // Act + Assert 2: the first request creates the payment (202)...
    const first = await initiate(payment, key);
    expect(first.status(), await first.text()).toBe(202);
    const created = await first.json() as Initiated;

    // ...and the resend finds it (200): the same payment, not a second one.
    const again = await initiate(payment, key);
    expect(again.status(), await again.text()).toBe(200);
    const found = await again.json() as Initiated;
    expect(found.paymentId).toBe(created.paymentId);
    expect(found.paymentNumber).toBe(created.paymentNumber);

    // Act + Assert 3: the same key with another amount is a conflict, not a new payment.
    const changed = await initiate({ ...payment, amount: 43.5 }, key);
    expect(changed.status(), 'the same key, other details').toBe(409);
  });
});