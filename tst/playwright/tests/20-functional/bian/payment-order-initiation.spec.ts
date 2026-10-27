import { randomUUID } from 'node:crypto';
import type { APIRequestContext } from '@playwright/test';
import { expect, test } from '../../../support/fixtures';
import { bffSessionFor } from '../../../support/bff';
import { fundedCustomer, payees } from '../../../support/journeys/payments';
import { resetAllSimulators } from '../../../support/simulators';
import { urls } from '../../../support/services';
import { paymentsWebSignInToken } from '../../../support/tokens';

/**
 * ┌─ :: ARCHITECTURE :: The Payments API speaks BIAN - shown live, next to its own /v1 ───────────
 * │ Why : /v2 claims BIAN's PaymentOrderInitiation contract (release 14.0.0). The unit tests check
 * │       every body against BIAN's published file; this shows the running API doing a whole BIAN
 * │       conversation - and that /v2 is a second contract on the SAME business path, not a copy.
 * │ How : Sophie initiates a payment above the approval threshold through BIAN's Initiate; /v1
 * │       and /v2 then show the same payment; the same Idempotency-Key through /v1 finds that
 * │       payment (one path); Sophie may not use BIAN's Exchange to approve (403); Emily approves
 * │       through it and the payment completes. Also the demo script for doc/bian/README.md.
 * │ Ref : doc/bian/README.md | Payments API README (/v2) | doc/BSS-Requirements.md section 10
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
interface PayerAccount { customerNumber: string; bsb: string; accountNumber: string }
interface V1Payment { paymentId: number; paymentNumber: string; status: string; amount: number; payeeName: string; toBsb: string; toAccountNumber: string }
interface BianTransaction {
  PaymentTransaction: {
    PaymentTransactionIdentification: { PaymentIdentification: { IdentifierValue: string } };
    PaymentTransactionStatus: { PaymentTransactionStatus: { StatusReason: string }; PaymentTransactionStatusType: string };
  };
  PayeeReference: { PayeeIdentifier: string };
  PayeeBankReference: { PayeeBankIdentifier: string };
  PayeeProductInstanceReference: { ProductAgreementIdentification: { IdentifierValue: string } };
  Amount: { AmountValue: string; AmountCurrency: { Currencycode: string } };
}

const v2 = `${urls.paymentsApi}/v2/PaymentOrderInitiation`;

async function retrieve(http: APIRequestContext, token: string, paymentId: number): Promise<BianTransaction> {
  const response = await http.get(`${v2}/${paymentId}/Retrieve`, { headers: { Authorization: `Bearer ${token}` } });
  expect(response.status(), await response.text()).toBe(200);
  return await response.json() as BianTransaction;
}

const statusOf = (t: BianTransaction) => t.PaymentTransaction.PaymentTransactionStatus;

test.describe('BIAN - Payment Order Initiation (/v2)', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('ShouldCarryAPaymentThroughBiansContract_OnTheSameBusinessPathAsV1', async ({ signedInAs, request }) => {
    // Arrange: a funded customer, Sophie's and Emily's Payments tokens, the paying account.
    const customer = await fundedCustomer(signedInAs, 'BIAN payment order');
    const sophie = await signedInAs('sophie.cs');
    const sophieToken = await paymentsWebSignInToken(sophie, request);
    const emilyToken = await paymentsWebSignInToken(await signedInAs('emily.payments'), request);
    const bff = await bffSessionFor(sophie, urls.paymentsMfe);
    const accounts = await bff.request.get(`${urls.paymentsMfe}/bff/api/payer-accounts?search=${encodeURIComponent(customer)}`);
    expect(accounts.status()).toBe(200);
    const payer = (await accounts.json() as PayerAccount[]).find(a => a.customerNumber === customer);
    expect(payer, `a paying account of ${customer}`).toBeTruthy();

    // Act 1: BIAN Initiate - BIAN's property names, AUD 1,100 (above the approval threshold).
    const key = randomUUID();
    const order = {
      PayerReference: { PayerIdentifier: customer, PaymentInstructionInvolvementType: 'PayerOrDebtor' },
      PayerBankReference: { PayerBankIdentifier: payer!.bsb },
      PayerProductInstanceReference: { ProductAgreementIdentification: { IdentifierValue: payer!.accountNumber } },
      PayeeReference: { PayeeIdentifier: payees.matching.name, PaymentInstructionInvolvementType: 'PayeeOrCreditor' },
      PayeeBankReference: { PayeeBankIdentifier: payees.matching.bsb },
      PayeeProductInstanceReference: { ProductAgreementIdentification: { IdentifierValue: payees.matching.accountNumber } },
      Amount: { AmountValue: '1100.00', AmountCurrency: { Currencycode: 'AUD' } },
      PaymentPurpose: 'BIAN demo',
    };
    const initiated = await request.post(`${v2}/Initiate`, { data: order, headers: { Authorization: `Bearer ${sophieToken}`, 'Idempotency-Key': key } });
    expect(initiated.status(), await initiated.text()).toBe(202);
    const location = initiated.headers()['location'] ?? '';
    expect(location).toMatch(/^\/v2\/PaymentOrderInitiation\/\d+\/Retrieve$/);
    const paymentId = Number(location.split('/')[3]);

    // Assert 1: it waits for approval - in BIAN's words, with EWP's own status alongside.
    await expect.poll(async () => statusOf(await retrieve(request, sophieToken, paymentId)).PaymentTransactionStatus.StatusReason,
      { timeout: 60_000 }).toBe('PENDING_APPROVAL');
    const pending = await retrieve(request, sophieToken, paymentId);
    expect(statusOf(pending).PaymentTransactionStatusType).toBe('Pending');

    // Assert 2: /v1 shows the same payment - one payment, two contracts.
    const v1 = await request.get(`${urls.paymentsApi}/v1/payments/${paymentId}`, { headers: { Authorization: `Bearer ${sophieToken}` } });
    expect(v1.status()).toBe(200);
    const same = await v1.json() as V1Payment;
    expect(same.paymentNumber).toBe(pending.PaymentTransaction.PaymentTransactionIdentification.PaymentIdentification.IdentifierValue);
    expect(same.amount.toFixed(2)).toBe(pending.Amount.AmountValue);
    expect(same.payeeName).toBe(pending.PayeeReference.PayeeIdentifier);
    expect(same.toBsb).toBe(pending.PayeeBankReference.PayeeBankIdentifier);
    expect(same.toAccountNumber).toBe(pending.PayeeProductInstanceReference.ProductAgreementIdentification.IdentifierValue);

    // Assert 3: the same Idempotency-Key through /v1 finds THIS payment (the same business path).
    const resent = await request.post(`${urls.paymentsApi}/v1/payments`, {
      data: {
        customerNumber: customer, fromBsb: payer!.bsb, fromAccountNumber: payer!.accountNumber,
        payeeName: payees.matching.name, toBsb: payees.matching.bsb, toAccountNumber: payees.matching.accountNumber,
        amount: 1100, reference: 'BIAN demo',
      },
      headers: { Authorization: `Bearer ${sophieToken}`, 'Idempotency-Key': key },
    });
    expect(resent.status(), await resent.text()).toBe(200);
    expect((await resent.json() as { paymentId: number }).paymentId).toBe(paymentId);

    // Act + Assert 4: BIAN's Exchange keeps /v1's rules - Sophie may not approve.
    const decision = (task: string) => ({ OrderInitiationTaskResult: { Task: task }, PaymentOrderProcedureInstanceStatus: { StatusReason: 'Confirmed with the customer by phone' } });
    const bySophie = await request.put(`${v2}/${paymentId}/OrderInitiation/approval/Exchange`, { data: decision('Approve'), headers: { Authorization: `Bearer ${sophieToken}` } });
    expect(bySophie.status(), 'a customer service agent approving through /v2').toBe(403);

    // Act + Assert 5: Emily approves through BIAN's Exchange; the payment completes.
    const byEmily = await request.put(`${v2}/${paymentId}/OrderInitiation/approval/Exchange`, { data: decision('Approve'), headers: { Authorization: `Bearer ${emilyToken}` } });
    expect(byEmily.status(), await byEmily.text()).toBe(200);
    expect((await byEmily.json() as { OrderInitiationTaskResult: { Task: string } }).OrderInitiationTaskResult.Task).toBe('Approved');
    await expect.poll(async () => statusOf(await retrieve(request, emilyToken, paymentId)).PaymentTransactionStatusType,
      { timeout: 90_000 }).toBe('Acceptedsettlementcompleted');
  });
});