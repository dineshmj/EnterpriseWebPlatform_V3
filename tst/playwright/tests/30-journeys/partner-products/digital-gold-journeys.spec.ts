import { expect, test } from '../../../support/fixtures';
import { lanIds } from '../../../support/audit';
import { buyGold, goldAccount, partnerBankReachable } from '../../../support/journeys/digital-gold';
import { DigitalGoldMfe } from '../../../support/pages/mfes';
import { resetAllSimulators, withSimulator } from '../../../support/simulators';

/**
 * Digital Gold end to end - EWP as the Partner Bank's distributor: Sophie records the customer's
 * signed Form VII and sends it (signed instruction over mutual TLS; the Partner Bank opens the
 * account), views the account on the Partner Bank's own read-only page (one-time link, new tab),
 * and buys gold with a transfer slip: the order goes to the Partner Bank, the payment runs through
 * the Payments saga as Sophie, and the Temporal workflow follows it - payment advice -> gold
 * allocated, or payment not made -> order cancelled.
 *
 * Needs the Partner Bank running (otherwise the file is skipped), Temporal
 * (ps\temporal\Start-Temporal.ps1) and the Partner Products Worker.
 *
 * The tests share one customer with an open Digital Gold account, onboarded by the first test.
 */
test.describe('Digital Gold journeys', () => {
  test.describe.configure({ timeout: 8 * 60_000 });

  test.beforeAll(async () => {
    test.skip(!(await partnerBankReachable()), 'The Partner Bank is not reachable: start it with Start-PartnerBank.ps1 -Lan (PC) and check the hosts entries.');
  });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('Form VII sent: the Partner Bank opens the account, and it holds no gold yet', async ({ signedInAs }) => {
    // Arrange + Act
    const { customerNumber, partnerAccountNumber } = await goldAccount(signedInAs, 'DigitalGold');

    // Assert
    expect(partnerAccountNumber).toMatch(/^PB-DGA-[A-Z0-9]{6}$/);
    const digitalGold = new DigitalGoldMfe(await signedInAs('sophie.cs'));
    await digitalGold.open();
    await digitalGold.selectCustomer(customerNumber);
    await expect(digitalGold.viewAtPartnerBankButton()).toBeVisible();
    await digitalGold.expectGoldHeld(customerNumber, '0.0000 g');
  });

  test('View at the Partner Bank: its own read-only page in a new tab, cut off from EWP, one use only', async ({ signedInAs }) => {
    // Arrange
    const { customerNumber, partnerAccountNumber } = await goldAccount(signedInAs, 'DigitalGold');
    const digitalGold = new DigitalGoldMfe(await signedInAs('sophie.cs'));
    await digitalGold.open();
    await digitalGold.selectCustomer(customerNumber);

    // Act
    const tab = await digitalGold.viewAtPartnerBank();

    // Assert
    await test.step('The Partner Bank shows the account, for Sophie, read-only', async () => {
      await expect(tab.getByRole('heading', { level: 1 })).toHaveText(`Digital Gold account ${partnerAccountNumber}`);
      await expect(tab.getByText(`customer ${customerNumber}`)).toBeVisible();
      await expect(tab.getByText(`Read-only view for ${lanIds['sophie.cs']}`)).toBeVisible();
    });
    await test.step('The tab cannot reach back to EWP\'s page (no opener)', async () => {
      expect(await tab.evaluate(() => window.opener)).toBeNull();
    });
    await test.step('The link works once: reloading it shows the view is no longer available', async () => {
      await tab.reload();
      await expect(tab.getByRole('heading', { level: 1 })).toHaveText('This view is no longer available');
    });
    await tab.close();
  });

  test('Transfer slip, Send & pay: the payment completes, the workflow sends the payment advice, the gold is allocated', async ({ signedInAs }) => {
    // Arrange
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGold');
    const sophie = await signedInAs('sophie.cs');

    // Act
    const { digitalGold, slipNumber } = await buyGold(sophie, customerNumber, '20.00');

    // Assert
    await test.step('The workflow sees the payment completed and tells the Partner Bank: gold allocated', async () => {
      await digitalGold.expectPurchase(customerNumber, slipNumber, 'Gold allocated');
      await expect(digitalGold.purchaseRow(slipNumber)).toContainText(/\d+\.\d{4} g at /);
    });
    await test.step('The Partner Bank now reports gold held', async () => {
      await digitalGold.expectGoldHeld(customerNumber, /^(?!0\.0000 g$)\d+\.\d{4} g$/);
    });
  });

  test('Payment not made (the payment network refuses it): the workflow cancels the order', async ({ signedInAs }) => {
    // Arrange
    const { customerNumber } = await goldAccount(signedInAs, 'DigitalGold');
    const sophie = await signedInAs('sophie.cs');

    await withSimulator('paymentNetwork', { behaviour: 'Refusing' }, async () => {
      // Act
      const { digitalGold, slipNumber } = await buyGold(sophie, customerNumber, '20.00');

      // Assert
      await test.step('The payment fails, the funds are released; the workflow cancels the order at the Partner Bank', async () => {
        await digitalGold.expectPurchase(customerNumber, slipNumber, 'Cancelled - not paid');
        await expect(digitalGold.purchaseRow(slipNumber)).toContainText(/Payment PAY-\S+ was not made/);
      });
    });
  });
});