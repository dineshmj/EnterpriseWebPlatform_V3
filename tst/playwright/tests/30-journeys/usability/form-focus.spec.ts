import { expect, test } from '../../../support/fixtures';
import { AuditTrail } from '../../../support/audit';
import { fundedCustomer } from '../../../support/journeys/payments';
import {
  CashDepositMfe, CustomersMfe, DigitalGoldMfe, OnboardingMfe, PaymentsMfe, PersonalDataErasureMfe,
} from '../../../support/pages/mfes';
import type { ShellPage } from '../../../support/pages/ShellPage';
import type { PersonaName } from '../../../support/personas';

// ┌─ :: ARCHITECTURE :: A form takes the focus; Enter submits only when it is complete ─────────────
// │ Why : A person should type at once on every entry screen and finish with Enter - but never send
// │       an incomplete payment by pressing it.
// │ How : Each screen is opened through the Shell (the MFE runs in its cross-origin frame, where the
// │       HTML autofocus attribute is ignored - the apps set the focus from script). Then: the first
// │       field has the focus; Enter on an incomplete payment does nothing; Enter on a complete cash
// │       deposit records it.
// └────────────────────────────────────────────────────────────────────────────────────────────────
type Screen = { name: string; persona: PersonaName; open: (shell: ShellPage) => Promise<void>; field: string };

const screens: Screen[] = [
  { name: 'New onboarding application', persona: 'sophie.cs', open: (s) => new OnboardingMfe(s).openNew(), field: '#firstName' },
  { name: 'Customers', persona: 'sophie.cs', open: (s) => new CustomersMfe(s).open(), field: '#customer-search' },
  { name: 'New payment', persona: 'sophie.cs', open: (s) => new PaymentsMfe(s).openNew(), field: '#payer-search' },
  { name: 'Cash deposit', persona: 'sophie.cs', open: (s) => new CashDepositMfe(s).open(), field: '#deposit-search' },
  { name: 'Digital Gold', persona: 'sophie.cs', open: (s) => new DigitalGoldMfe(s).open(), field: '#search' },
  { name: 'Audit Trail', persona: 'sarah.audit', open: (s) => new AuditTrail(s).open(), field: '#record' },
  { name: 'Personal data erasure', persona: 'daniel.ops', open: (s) => new PersonalDataErasureMfe(s).open(), field: '#erasure-customer-number' },
];

test.describe('Forms: focus and Enter', () => {
  for (const screen of screens) {
    test(`${screen.name}: the first field has the focus when the screen opens`, async ({ signedInAs }) => {
      // Arrange
      const shell = await signedInAs(screen.persona);

      // Act
      await screen.open(shell);

      // Assert
      await expect(shell.workspace().locator(screen.field)).toBeFocused();
    });
  }

  test('New payment: Enter on an incomplete payment does nothing', async ({ signedInAs }) => {
    // Arrange
    const sophie = await signedInAs('sophie.cs');
    await new PaymentsMfe(sophie).openNew();
    const mfe = sophie.workspace();

    // Act
    await mfe.locator('#payer-search').press('Enter');

    // Assert: still the form, its Transfer button disabled, nothing started.
    await expect(mfe.getByRole('button', { name: 'Transfer', exact: true })).toBeDisabled();
    await expect(mfe.locator('#payer-search')).toBeVisible();
  });

  test('Cash deposit: once the account is chosen the amount has the focus, and Enter records the deposit', async ({ signedInAs }) => {
    // Arrange
    const customer = await fundedCustomer(signedInAs, 'FormFocus');
    const sophie = await signedInAs('sophie.cs');
    const deposit = new CashDepositMfe(sophie);
    await deposit.open();
    await deposit.chooseAccount(customer);
    const mfe = sophie.workspace();
    await expect(mfe.locator('#deposit-amount')).toBeFocused();
    await mfe.locator('#deposit-amount').fill('20.00');
    await mfe.getByLabel('I have counted the cash and it matches the amount').check();

    // Act
    await mfe.locator('#deposit-amount').press('Enter');

    // Assert
    await expect(mfe.getByText('Deposit recorded', { exact: true })).toBeVisible({ timeout: 30_000 });
  });
});