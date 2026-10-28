import { expect, test } from '../../../support/fixtures';
import { flagFileNotUsedReason, withFlag } from '../../../support/feature-flags';
import { formNumber, goldAccount, mobileOnRecord, partnerBankReachable } from '../../../support/journeys/digital-gold';
import { DigitalGoldMfe } from '../../../support/pages/mfes';

/**
 * ┌─ :: RESILIENCE :: The Digital Gold kill switch ────────────────────────────────────────────────
 * │ Why : The bank must be able to stop selling gold at once (a pricing fault at the Partner Bank,
 * │       a regulator's request) without a redeploy - and stopping must not strand anything that
 * │       has already left EWP.
 * │ How : Sophie records a slip while sales are open. The flag digital-gold-sales-paused goes on:
 * │       a NEW slip is refused, and so is "Send & pay" for the slip not yet sent - with the
 * │       reason, nothing sent, nothing charged. The flag goes off: the waiting slip is sent and
 * │       paid as usual. (A slip already SENT is never stopped - Partner Products API code.)
 * │ Ref : feature-flags/README.md | Partner Products API README | B8
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 *
 * Needs what 30-journeys/partner-products needs (the Partner Bank, Temporal, the worker), and the
 * APIs reading feature-flags\ewp-flags.json (not LaunchDarkly). One new customer per run.
 */
const pausedMessage = /Digital Gold sales are paused by the bank for now: nothing was sent to the Partner Bank and nothing was charged/;

test.describe('Digital Gold kill switch - resilience', () => {
  test.describe.configure({ timeout: 10 * 60_000 });

  test.beforeAll(async () => {
    test.skip(!!flagFileNotUsedReason, flagFileNotUsedReason);
    test.skip(!(await partnerBankReachable()), 'The Partner Bank is not reachable: start it with Start-PartnerBank.ps1 -Lan (PC) and check the hosts entries.');
  });

  test('Sales paused: new slips and unsent ones are refused with the reason; resumed: the waiting slip is bought', async ({ signedInAs }) => {
    // Arrange: an open Digital Gold account and a slip recorded while sales are open.
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGoldKillSwitch');
    const sophie = await signedInAs('sophie.cs');
    const digitalGold = new DigitalGoldMfe(sophie);
    await digitalGold.open();
    await digitalGold.selectCustomer(customerNumber);
    const waitingSlip = formNumber('TS');
    await digitalGold.recordTransferSlip(waitingSlip, '20.00', mobileOnRecord);

    await withFlag('digital-gold-sales-paused', 'on', async () => {
      // Act + Assert 1: a new slip is refused, with the reason.
      await test.step('Paused: a new slip is refused with the reason', async () => {
        // The screen is open already: opening it again would only swap the page under the test.
        await digitalGold.selectCustomer(customerNumber);
        const refused = await digitalGold.tryRecordTransferSlip(formNumber('TS'), '20.00', mobileOnRecord);
        await expect(refused.filter({ hasText: pausedMessage })).toBeVisible({ timeout: 30_000 });
      });

      // Act + Assert 2: the slip recorded before the pause is not sent.
      await test.step('Paused: "Send & pay" of the waiting slip is refused; it stays Recorded', async () => {
        await digitalGold.purchaseRow(waitingSlip).getByRole('button', { name: 'Send & pay', exact: true }).click();
        await expect(sophie.workspace().getByRole('alert').filter({ hasText: pausedMessage })).toBeVisible({ timeout: 30_000 });
        await digitalGold.selectCustomer(customerNumber);
        await expect(digitalGold.purchaseRow(waitingSlip).getByRole('button', { name: 'Send & pay', exact: true })).toBeVisible();
      });
    });

    // Act + Assert 3: sales open again - the waiting slip is sent and paid as usual.
    await test.step('Resumed: the waiting slip is sent and its payment starts', async () => {
      await digitalGold.selectCustomer(customerNumber);
      expect(await digitalGold.sendAndPay(waitingSlip)).toMatch(/^PAY-/);
    });
  });
});