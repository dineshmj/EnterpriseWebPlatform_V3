import { expect, test } from '@playwright/test';
import { lanIds, type ExpectedEntry } from '../audit';
import { newSydneyCustomer } from '../data';
import { PaymentApprovalsMfe, PaymentsMfe, type Payee, type PaymentOutcome } from '../pages/mfes';
import type { ShellPage } from '../pages/ShellPage';
import type { PersonaName } from '../personas';
import { withSimulator } from '../simulators';
import { accountDecision, complianceDecision, expectOnboardingStatus, kycDecision, submitApplication } from './onboarding';

/**
 * The steps of a payment, each as the person who does it. Payments need a customer with an
 * OPEN, FUNDED account of the Sydney branch: every new account starts with the demo opening
 * deposit (Accounts:DemoOpeningDeposit, AUD 5,000), so a spec file onboards ONE customer and
 * its payments share that account (the file's tests run in order, in one worker).
 */
type SignIn = (username: PersonaName) => Promise<ShellPage>;

/** Payees the payment network simulator knows (BSB directory; Confirmation of Payee by the account's last digit). */
export const payees = {
  /** NAB, NSW: the name matches. */
  matching: { bsb: '083-004', accountNumber: '12345678', name: 'Harbour Plumbing Pty Ltd' } satisfies Payee,
  /** Account ending 9: the bank holds a slightly different name (close match). */
  closeMatch: { bsb: '083-004', accountNumber: '12345679', name: 'Olivia Martin' } satisfies Payee,
  /** Account ending 0: the name does not match (no match). */
  noMatch: { bsb: '083-004', accountNumber: '12345670', name: 'Harbour Plumbing Pty Ltd' } satisfies Payee,
  /** BSB 999: the demo "closed bank" - the network refuses the payment (the payee's bank returned it). */
  closedBank: { bsb: '999-001', accountNumber: '12345678', name: 'Old Account Holder' } satisfies Payee,
};

const fundedCustomers = new Map<string, Promise<string>>();

/**
 * A customer with an open, funded account, onboarded once per `key` (the spec file) and reused by
 * its tests. Screening is forced CLEAR so the onboarding cannot stop at a high-risk decision.
 */
export function fundedCustomer(signedInAs: SignIn, key: string): Promise<string> {
  let customer = fundedCustomers.get(key);
  if (!customer) {
    customer = test.step('A funded customer: onboarded end to end (the account opens with the demo deposit)', () =>
      withSimulator('screeningProvider', { behaviour: 'Healthy', forcedOutcome: 'CLEAR' }, async () => {
        const sophie = await signedInAs('sophie.cs');
        const { customerNumber, applicationNumber } = await submitApplication(sophie, newSydneyCustomer('Payer', key));
        const ethan = await signedInAs('ethan.kyc');
        await kycDecision(ethan, applicationNumber, 'Identity verification', 'Approve');
        await kycDecision(ethan, applicationNumber, 'Document verification', 'Approve');
        await complianceDecision(await signedInAs('olivia.compliance'), applicationNumber, 'Approve');
        await accountDecision(await signedInAs('jack.accounts'), applicationNumber, 'Approve');
        await expectOnboardingStatus(sophie, applicationNumber, 'Completed');
        return customerNumber;
      }));
    // A failed onboarding is not cached: the next test tries again.
    customer.catch(() => fundedCustomers.delete(key));
    fundedCustomers.set(key, customer);
  }
  return customer;
}

/** Sophie captures a payment from the customer's account; returns its number (her screen is then on the payment's page). */
export async function startPayment(agent: ShellPage, customerNumber: string, payee: Payee, amount: string, reference?: string): Promise<{ payments: PaymentsMfe; paymentNumber: string }> {
  return test.step(`Sophie pays ${amount} to ${payee.name} (${payee.bsb})`, async () => {
    const payments = new PaymentsMfe(agent);
    await payments.openNew();
    await payments.choosePayer(customerNumber);
    await payments.enterPayee(payee);
    await payments.enterAmount(amount, reference);
    const paymentNumber = await payments.transfer();
    return { payments, paymentNumber };
  });
}

export async function expectPaymentOutcome(payments: PaymentsMfe, outcome: PaymentOutcome, timeout?: number): Promise<void> {
  await test.step(`The payment's page shows "${outcome}"`, () => payments.expectOutcome(outcome, timeout));
}

/** Emily (payments officer) decides a payment above the approval tier. */
export async function paymentDecision(officer: ShellPage, paymentNumber: string, action: 'Approve' | 'Reject'): Promise<void> {
  await test.step(`Payments officer: ${action} ${paymentNumber}`, async () => {
    const approvals = new PaymentApprovalsMfe(officer);
    await approvals.openPayment(paymentNumber);
    if (action === 'Approve') await approvals.approve('Checked with the customer by phone.');
    else await approvals.reject('The customer did not confirm this payment.');
  });
}

/** What the Audit Trail must hold for each way a payment can end (record PAY-…). */
export const paymentTrail = {
  completed: [{ event: 'FundsReserved' }, { event: 'FundsSettled' }, { event: 'PaymentCompleted' }] as ExpectedEntry[],
  approved: (officer: PersonaName): ExpectedEntry[] => [
    { event: 'FundsReserved' },
    { event: 'PaymentApprovalRequired' },
    { event: 'FundsSettled' },
    { event: 'PaymentCompleted', decidedBy: lanIds[officer] },
  ],
  rejectedByOfficer: (officer: PersonaName): ExpectedEntry[] => [
    { event: 'FundsReserved' },
    { event: 'PaymentApprovalRequired' },
    { event: 'FundsReleased' },
    { event: 'PaymentRejected', decidedBy: lanIds[officer] },
  ],
  failedAndReleased: [{ event: 'FundsReserved' }, { event: 'FundsReleased' }, { event: 'PaymentFailed' }] as ExpectedEntry[],
};

/** The saga's timeline line once the payment has waited past Payments:Saga:UnconfirmedAlertMinutes (Development 2). */
export const UNCONFIRMED_ALERT = /Unconfirmed by the network for more than \d+ minutes/;

/** An operator opens the payment from the Payment Processing Monitor (operations see every branch). */
export async function openAsOperator(operator: ShellPage, paymentNumber: string): Promise<void> {
  await operator.openMenuItem('Payments', 'Payment Processing Monitor');
  await operator.waitForScreen('Payment processing monitor');
  const link = operator.workspace().getByRole('link', { name: paymentNumber, exact: true }).first();
  await expect(link).toBeVisible({ timeout: 60_000 });
  await link.click();
  await operator.waitForScreen(`Payment · ${paymentNumber}`);
}

/** The first operator proposes how the network actually handled the payment (with evidence). */
export async function proposeResolution(operator: ShellPage, outcome: 'SENT' | 'NOT_SENT', evidence: string, networkReference?: string): Promise<void> {
  await test.step(`Operator proposes: ${outcome}`, async () => {
    const mfe = operator.workspace();
    await expect(mfe.getByText('Reconciliation with the payment network')).toBeVisible({ timeout: 30_000 });
    await mfe.locator('#resolution-outcome').selectOption(outcome);
    if (networkReference) await mfe.locator('#resolution-reference').fill(networkReference);
    await mfe.locator('#resolution-evidence').fill(evidence);
    await mfe.getByRole('button', { name: 'Propose resolution', exact: true }).click();
    await mfe.getByRole('alertdialog').getByRole('button', { name: 'Propose', exact: true }).click();
    await expect(mfe.getByText('You proposed this resolution: another operator must approve or reject it.')).toBeVisible({ timeout: 30_000 });
  });
}

/** The SECOND operator approves or rejects the proposal (rejecting needs remarks). */
export async function decideResolution(operator: ShellPage, action: 'Approve' | 'Reject', remarks?: string): Promise<void> {
  await test.step(`Second operator: ${action} the proposal`, async () => {
    const mfe = operator.workspace();
    await expect(mfe.getByText('Waiting for a second person')).toBeVisible({ timeout: 30_000 });
    if (remarks) await mfe.locator('#resolution-remarks').fill(remarks);
    await mfe.getByRole('button', { name: action, exact: true }).first().click();
    await mfe.getByRole('alertdialog').getByRole('button', { name: action, exact: true }).click();
  });
}