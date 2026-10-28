import { expect, type Locator, type Page } from '@playwright/test';
import { samplePdf, type Evidence, type NewCustomer } from '../data';
import type { ShellPage } from './ShellPage';

/** Work arriving through Kafka (a new case in a queue, a status that moved on): checked again until it is there. */
export const WORKFLOW_WAIT = { timeout: 90_000, intervals: [1_000, 2_000, 3_000, 5_000] };

/**
 * Re-opens (reloads) the screen until `target` appears - a case lands in a queue a moment after
 * the previous step, through Kafka. No fixed sleeps.
 */
async function waitFor(shell: ShellPage, title: string, target: () => Locator): Promise<void> {
  await expect(async () => {
    // Give the screen time to load its list before deciding it needs a fresh look.
    if (!(await appears(target()))) {
      await shell.reloadWorkspace();
      await shell.waitForScreen(title);
    }
    await expect(target()).toBeVisible({ timeout: 5_000 });
  }).toPass(WORKFLOW_WAIT);
}

/** Whether the element shows up within a few seconds (the screen's own loading time). */
function appears(target: Locator, timeout = 5_000): Promise<boolean> {
  return target.waitFor({ state: 'visible', timeout }).then(() => true, () => false);
}

// ---------------------------------------------------------------- Customer Onboarding

export interface Submitted {
  customerNumber: string;
  applicationNumber: string;
}

/**
 * Customer Onboarding: "New Onboarding Application" (the form) and "View Onboarding Applications"
 * (the branch's applications and their status - where the bell's progress notifications lead).
 */
export class OnboardingMfe {
  static readonly newTitle = 'New onboarding application';
  static readonly listTitle = 'Onboarding applications';

  constructor(private readonly shell: ShellPage) {}

  /** The form for a new onboarding. */
  async openNew(): Promise<void> {
    await this.shell.openMenuItem('Customer Onboarding', 'New Onboarding Application');
    await this.shell.waitForScreen(OnboardingMfe.newTitle);
  }

  /** The applications list. */
  async openApplications(): Promise<void> {
    await this.shell.openMenuItem('Customer Onboarding', 'View Onboarding Applications');
    await this.shell.waitForScreen(OnboardingMfe.listTitle);
  }

  /**
   * Fills the form for a new customer, attaches both PDFs and submits; returns the numbers shown.
   * Without `evidence`, small generated PDFs are attached (fast, and unique per test).
   */
  async submitNewCustomer(customer: NewCustomer, evidence?: Evidence): Promise<Submitted> {
    const mfe = this.shell.workspace();
    await this.fillNewCustomer(customer, evidence);
    await mfe.getByRole('button', { name: 'Submit application', exact: true }).click();

    const success = mfe.getByRole('status').filter({ hasText: 'Application submitted' });
    await expect(success).toBeVisible({ timeout: 60_000 });
    const text = (await success.textContent()) ?? '';
    const customerNumber = /Customer\s+(\S+)/.exec(text)?.[1];
    const applicationNumber = /application\s+(\S+?)\.?$/m.exec(text.trim())?.[1] ?? /application\s+(\S+)/.exec(text)?.[1];
    if (!customerNumber || !applicationNumber) throw new Error(`Could not read the numbers from "${text}".`);
    return { customerNumber, applicationNumber: applicationNumber.replace(/\.$/, '') };
  }

  /** Fills the form; with `omitTaxProof` the tax proof is left out (the submission must then be refused). */
  async fillNewCustomer(customer: NewCustomer, evidence?: Evidence, omitTaxProof = false): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#firstName').fill(customer.firstName);
    await mfe.locator('#lastName').fill(customer.lastName);
    await mfe.locator('#email').fill(customer.email);
    await mfe.locator('#phoneNumber').fill(customer.phoneNumber);
    await mfe.locator('#addressLine1').fill(customer.addressLine1);
    await mfe.locator('#city').fill(customer.city);
    await mfe.locator('#countryCode').fill(customer.countryCode);
    await mfe.locator('#state').selectOption(customer.state);
    await mfe.locator('#postalCode').fill(customer.postalCode);
    await mfe.locator('#kycProof').setInputFiles(evidence?.identityProof ?? samplePdf(`Identity proof ${customer.lastName}`));
    if (!omitTaxProof) await mfe.locator('#taxProof').setInputFiles(evidence?.taxProof ?? samplePdf(`Tax proof ${customer.lastName}`));
  }

  /** Waits until the application's status (as the agent sees it) is `status`, e.g. "Completed". */
  async expectStatus(applicationNumber: string, status: string): Promise<void> {
    const mfe = this.shell.workspace();
    const row = () => mfe.locator('tbody tr').filter({ hasText: applicationNumber });
    await expect(async () => {
      await mfe.getByRole('button', { name: 'Refresh', exact: true }).click();
      await expect(row()).toContainText(status, { timeout: 2_000 });
    }).toPass(WORKFLOW_WAIT);
  }
}

/** Customer Onboarding: "View Customers" - the branch's customers, their status and what can be done next. */
export class CustomersMfe {
  static readonly title = 'Customers';

  constructor(private readonly shell: ShellPage) {}

  async open(): Promise<void> {
    await this.shell.openMenuItem('Customer Onboarding', 'View Customers');
    await this.shell.waitForScreen(CustomersMfe.title);
  }

  /** The customer's row (empty when the customer is not visible to this person). */
  row(customerNumber: string): Locator {
    return this.shell.workspace().locator('tbody tr').filter({ hasText: customerNumber });
  }

  /** Waits until the row shows `text` (the list reflects the workflow a moment later). */
  async expectRowText(customerNumber: string, text: string | RegExp): Promise<void> {
    await expect(async () => {
      await this.shell.reloadWorkspace();
      await this.shell.waitForScreen(CustomersMfe.title);
      await expect(this.row(customerNumber)).toContainText(text, { timeout: 3_000 });
    }).toPass(WORKFLOW_WAIT);
  }
}

// ---------------------------------------------------------------- Customer KYC

export type KycStage = 'Identity verification' | 'Document verification';

/** Customer KYC: the two review screens (identity proof, tax proof), each with its queue and decision. */
export class KycMfe {
  constructor(private readonly shell: ShellPage) {}

  /** Opens the stage's screen, waits for the application's case in its queue, and selects it. */
  async openCase(stage: KycStage, applicationNumber: string): Promise<void> {
    // The menu item is "Identity Verification"; the screen's title is "Identity verification".
    await this.shell.openMenuItem('Customer KYC', stage === 'Identity verification' ? 'Identity Verification' : 'Document Verification');
    await this.shell.waitForScreen(stage);
    const option = () => this.shell.workspace().getByRole('option').filter({ hasText: applicationNumber });
    await waitFor(this.shell, stage, option);
    await option().getByRole('button').click();
  }

  /** Records the decision on the selected case (rejecting needs remarks). */
  async decide(action: 'Approve' | 'Reject', remarks: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#decisionRemarks').fill(remarks);
    await mfe.getByRole('button', { name: action, exact: true }).click();
    await expect(mfe.getByRole('status').filter({ hasText: 'Decision recorded' })).toBeVisible({ timeout: 30_000 });
  }
}

// ---------------------------------------------------------------- Compliance and Accounts (case pages)

/** A work queue (tabs + table) whose rows open a case page with Approve / Reject behind a confirmation. */
abstract class CaseQueueMfe {
  constructor(protected readonly shell: ShellPage) {}

  protected abstract readonly context: string;
  protected abstract readonly menuItem: string;
  protected abstract readonly queueTitle: string;
  protected abstract readonly approveLabel: string;

  /** Opens the queue, waits for the application on the "All" tab and opens its case page. */
  async openCase(applicationNumber: string): Promise<void> {
    await this.shell.openMenuItem(this.context, this.menuItem);
    await this.shell.waitForScreen(this.queueTitle);
    const mfe = this.shell.workspace();
    const link = () => mfe.getByRole('link', { name: applicationNumber, exact: true });
    await expect(async () => {
      await mfe.getByRole('tab', { name: 'All', exact: true }).click();
      if (!(await appears(link()))) {
        await this.shell.reloadWorkspace();
        await this.shell.waitForScreen(this.queueTitle);
        await mfe.getByRole('tab', { name: 'All', exact: true }).click();
      }
      await expect(link()).toBeVisible({ timeout: 3_000 });
    }).toPass(WORKFLOW_WAIT);
    await link().click();
    await expect(mfe.getByRole('heading', { level: 1 })).toContainText(applicationNumber);
  }

  /**
   * The case page shows `status` (reloading while the workflow moves on). Use it only for status
   * words that are not also labels on the page (e.g. "Under review", "Pending review").
   */
  async expectCaseStatus(status: string): Promise<void> {
    const mfe = this.shell.workspace();
    await expect(async () => {
      if (!(await appears(mfe.getByText(status, { exact: true }).first()))) await this.shell.reloadWorkspace();
      await expect(mfe.getByText(status, { exact: true }).first()).toBeVisible({ timeout: 3_000 });
    }).toPass(WORKFLOW_WAIT);
  }

  approveButton(): Locator {
    // Before the confirmation opens there is one button of this name; the dialog's own comes after.
    return this.shell.workspace().getByRole('button', { name: this.approveLabel, exact: true }).first();
  }

  async approve(remarks: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#officer-text').fill(remarks);
    await this.approveButton().click();
    await mfe.getByRole('alertdialog').getByRole('button', { name: 'Approve', exact: true }).click();
  }

  async reject(remarks: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#officer-text').fill(remarks);
    await mfe.getByRole('button', { name: 'Reject', exact: true }).first().click();
    await mfe.getByRole('alertdialog').getByRole('button', { name: 'Reject', exact: true }).click();
  }
}

/** Compliance: "Compliance Monitor" → the case page (screening result, risk, decision). */
export class ComplianceMfe extends CaseQueueMfe {
  protected readonly context = 'Compliance';
  protected readonly menuItem = 'Compliance Monitor';
  protected readonly queueTitle = 'Compliance work queue';
  protected readonly approveLabel = 'Approve';
}

/** Accounts: "View Account Applications" → the application page (product, decision). */
export class AccountsMfe extends CaseQueueMfe {
  protected readonly context = 'Accounts';
  protected readonly menuItem = 'View Account Applications';
  protected readonly queueTitle = 'Account applications';
  protected readonly approveLabel = 'Approve and open account';
}

// ---------------------------------------------------------------- Payments

/** A payee as the customer gives it. The payment network simulator decides Confirmation of Payee by the account's last digit. */
export interface Payee {
  bsb: string;
  accountNumber: string;
  name: string;
}

/** What the payment's page says when it has ended (the outcome's title). */
export type PaymentOutcome =
  | 'Payment completed'
  | 'Payment rejected'
  | 'Payment failed - funds released'
  | 'Waiting for a payments officer';

/** The page of one payment: "Payment · PAY-yymmdd-XXXXXX". */
const PAYMENT_HEADING = /^Payment · (PAY-\d{6}-[A-Z0-9]{6})$/;

/** Payments: "New Payment" (the assisted channel) and the payment's own page, which follows the saga live. */
export class PaymentsMfe {
  static readonly newTitle = 'New payment';

  constructor(private readonly shell: ShellPage) {}

  async openNew(): Promise<void> {
    await this.shell.openMenuItem('Payments', 'New Payment');
    await this.shell.waitForScreen(PaymentsMfe.newTitle);
  }

  /** 1 · From: finds the customer (by customer number) and chooses their account. */
  async choosePayer(customerNumber: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#payer-search').fill(customerNumber);
    const account = mfe.getByRole('list', { name: 'Matching accounts' }).getByRole('button').filter({ hasText: customerNumber });
    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ Wait longer than the Payments BFF may take (10 s per attempt, 30 s in total): right
    // │ after a restart the first search can need a retry, and that is not a failure.
    // └──────────────────────────────────────────────────────────────────────────────────────
    await expect(account.first()).toBeVisible({ timeout: 35_000 });
    await account.first().click();
  }

  /**
   * 2 · To: BSB (checked against the directory), account number and name, then Confirmation of
   * Payee. Leaving the name field runs the check - but only once the BSB lookup has answered; when
   * the name was typed faster than that (headless), the screen offers "Check payee name" instead,
   * which is clicked, as a person would.
   */
  async enterPayee(payee: Payee): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#payee-bsb').fill(payee.bsb);
    await mfe.locator('#payee-account').fill(payee.accountNumber);
    await mfe.locator('#payee-name').fill(payee.name);
    await mfe.locator('#payee-name').press('Tab');
    const checkButton = mfe.getByRole('button', { name: 'Check payee name', exact: true });
    await expect(async () => {
      if (await checkButton.isVisible()) await checkButton.click();
      await expect(this.confirmationOfPayee()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  /** The Confirmation of Payee result shown for the payee entered. */
  confirmationOfPayee(): Locator {
    return this.shell.workspace().getByText(/^Confirmation of Payee: (match|close match|no match)$/);
  }

  /** Close match: takes over the name the payee's bank holds (the "Use “…”" button). */
  async useHeldName(): Promise<void> {
    await this.shell.workspace().getByRole('button', { name: /^Use “.+”$/ }).click();
  }

  /** 3 · Amount and an optional reference. */
  async enterAmount(amount: string, reference?: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#amount').fill(amount);
    if (reference) await mfe.locator('#reference').fill(reference);
  }

  transferButton(): Locator {
    return this.shell.workspace().getByRole('button', { name: 'Transfer', exact: true });
  }

  /** What still blocks the transfer (the Review card's "Still to do" list). */
  stillToDo(): Locator {
    return this.shell.workspace().getByRole('list', { name: 'Still to do' });
  }

  /** Transfers and returns the payment number from the payment's page, where the screen goes next. */
  async transfer(): Promise<string> {
    await this.transferButton().click();
    const heading = this.shell.workspace().getByRole('heading', { level: 1, name: PAYMENT_HEADING });
    await expect(heading).toBeVisible({ timeout: 30_000 });
    const paymentNumber = PAYMENT_HEADING.exec((await heading.textContent())?.trim() ?? '')?.[1];
    if (!paymentNumber) throw new Error('The payment page did not show a payment number.');
    return paymentNumber;
  }

  /** The payment's page (open after a transfer) reaches `outcome`; it follows the saga by itself. */
  async expectOutcome(outcome: PaymentOutcome, timeout = 60_000): Promise<void> {
    await expect(this.shell.workspace().getByText(outcome, { exact: true })).toBeVisible({ timeout });
  }

  /** A line of the saga's timeline on the payment's page (e.g. "The payment network did not answer"). */
  timeline(): Locator {
    return this.shell.workspace().locator('ol li');
  }
}

/** The receipt's deposit number: "DEP-yymmdd-XXXXXX". */
const DEPOSIT_NUMBER = /^DEP-\d{6}-[A-Z0-9]{6}$/;

/** Payments: "Cash Deposit" - the counter records cash a customer paid in (Accounts credits the account). */
export class CashDepositMfe {
  static readonly title = 'Cash deposit';

  constructor(private readonly shell: ShellPage) {}

  async open(): Promise<void> {
    await this.shell.openMenuItem('Payments', 'Cash Deposit');
    await this.shell.waitForScreen(CashDepositMfe.title);
  }

  /** 1 · Customer: finds the customer (by customer number) and chooses their account. */
  async chooseAccount(customerNumber: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#deposit-search').fill(customerNumber);
    const account = mfe.getByRole('list', { name: 'Matching accounts' }).getByRole('button').filter({ hasText: customerNumber });
    // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
    // │ Wait longer than the Payments BFF may take (10 s per attempt, 30 s in total): right
    // │ after a restart the first search can need a retry, and that is not a failure.
    // └──────────────────────────────────────────────────────────────────────────────────────
    await expect(account.first()).toBeVisible({ timeout: 35_000 });
    await account.first().click();
  }

  /** 2 · Cash: the counted amount (the screen suggests AUD 5,000) and the "counted" confirmation. */
  async enterCash(amount: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#deposit-amount').fill(amount);
    await mfe.getByLabel('I have counted the cash and it matches the amount').check();
  }

  /** Records the deposit and returns the receipt's deposit number. */
  async deposit(): Promise<string> {
    const mfe = this.shell.workspace();
    await mfe.getByRole('button', { name: 'Deposit', exact: true }).click();
    await expect(mfe.getByText('Deposit recorded', { exact: true })).toBeVisible({ timeout: 30_000 });
    const number = mfe.getByText(DEPOSIT_NUMBER);
    await expect(number).toBeVisible();
    return (await number.textContent())!.trim();
  }
}

/** Payments: the officer's "Payment Approvals" queue → the payment's page → approve or reject. */
export class PaymentApprovalsMfe {
  static readonly queueTitle = 'Payment approvals';

  constructor(private readonly shell: ShellPage) {}

  /** Opens the queue, waits for the payment (it arrives once the funds are reserved) and opens it. */
  async openPayment(paymentNumber: string): Promise<void> {
    await this.shell.openMenuItem('Payments', 'Payment Approvals');
    await this.shell.waitForScreen(PaymentApprovalsMfe.queueTitle);
    const link = () => this.shell.workspace().getByRole('link', { name: paymentNumber, exact: true });
    await waitFor(this.shell, PaymentApprovalsMfe.queueTitle, link);
    await link().click();
    await this.shell.waitForScreen(`Payment · ${paymentNumber}`);
  }

  async approve(remarks?: string): Promise<void> {
    const mfe = this.shell.workspace();
    if (remarks) await mfe.locator('#decision-remarks').fill(remarks);
    await mfe.getByRole('button', { name: 'Approve and send', exact: true }).first().click();
    await mfe.getByRole('alertdialog').getByRole('button', { name: 'Approve and send', exact: true }).click();
  }

  async reject(remarks: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#decision-remarks').fill(remarks);
    await mfe.getByRole('button', { name: 'Reject', exact: true }).first().click();
    await mfe.getByRole('alertdialog').getByRole('button', { name: 'Reject', exact: true }).click();
  }
}

// ---------------------------------------------------------------- Partner Products

/** What a purchase's row says (the screen's label for each status). */
export type PurchaseLabel = 'Ordered - waiting for the payment' | 'Gold allocated' | 'Cancelled - not paid' | 'Refused by the Partner Bank';

/** The Partner Bank's Digital Gold account number, as it shows on the screen. */
const PARTNER_ACCOUNT = /^PB-DGA-[A-Z0-9]{6}$/;

/**
 * Partner Products: "Digital Gold" - EWP as the Partner Bank's distributor. Sophie finds the
 * customer, records Form VII (opens the account) or a transfer authorization slip (buys gold),
 * and sends it; the Partner Bank answers over mutual TLS. The screen does not refresh by itself:
 * selecting the customer again reads the latest from the Partner Products API.
 */
export class DigitalGoldMfe {
  static readonly title = 'Digital Gold';

  constructor(private readonly shell: ShellPage) {}

  async open(): Promise<void> {
    await this.shell.openMenuItem('Partner Products', 'Digital Gold');
    await this.shell.waitForScreen(DigitalGoldMfe.title);
  }

  /** Searches the customer by number and selects them; their panel shows EWP's record. */
  async selectCustomer(customerNumber: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#search').fill(customerNumber);
    await mfe.getByRole('button', { name: 'Search', exact: true }).click();
    await mfe.locator('tbody tr').filter({ hasText: customerNumber }).getByRole('button', { name: 'Select', exact: true }).click();
    await expect(mfe.getByText(`Customer ${customerNumber} - EWP's record`, { exact: true })).toBeVisible();
  }

  /** Records the customer's signed Form VII with its scan (both checks ticked). */
  async recordFormVii(formNumber: string, dateOfBirth: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#formNumber').fill(formNumber);
    await mfe.locator('#dateOfBirth').fill(dateOfBirth);
    await mfe.locator('#scan').setInputFiles(samplePdf(`Form VII ${formNumber}`));
    await mfe.getByLabel(/against the customer's photo ID/).check();
    await mfe.getByLabel(/signature on Form VII/).check();
    await mfe.getByRole('button', { name: 'Record Form VII', exact: true }).click();
    await expect(mfe.getByText(new RegExp(`^Form VII ${formNumber} recorded`))).toBeVisible({ timeout: 30_000 });
  }

  /** Sends Form VII; once the Partner Bank opens the account, returns its number (PB-DGA-…). */
  async sendFormVii(): Promise<string> {
    const mfe = this.shell.workspace();
    await mfe.getByRole('button', { name: 'Send to the Partner Bank', exact: true }).click();
    // The Partner Products API waits up to 40 s for the Partner Bank; the BFF up to 60 s.
    await expect(this.viewAtPartnerBankButton()).toBeVisible({ timeout: 60_000 });
    return (await mfe.getByText(PARTNER_ACCOUNT).first().textContent())?.trim() ?? '';
  }

  viewAtPartnerBankButton(): Locator {
    return this.shell.workspace().getByRole('button', { name: 'View at the Partner Bank ↗', exact: true });
  }

  /** "View at the Partner Bank": the new tab the screen opens, once it shows the Partner Bank's page. */
  async viewAtPartnerBank(): Promise<Page> {
    const [tab] = await Promise.all([
      this.shell.page.context().waitForEvent('page'),
      this.viewAtPartnerBankButton().click(),
    ]);
    await tab.waitForURL(/\/StaffView\?request_uri=/, { timeout: 60_000 });
    return tab;
  }

  /** Records a transfer authorization slip for `amount` from the first paying account. */
  async recordTransferSlip(slipNumber: string, amount: string, mobileOnSlip: string): Promise<void> {
    const mfe = this.shell.workspace();
    await mfe.locator('#slipNumber').fill(slipNumber);
    await mfe.locator('#amount').fill(amount);
    await mfe.locator('#mobileOnSlip').fill(mobileOnSlip);
    await mfe.locator('#slipScan').setInputFiles(samplePdf(`Slip ${slipNumber}`));
    await mfe.getByLabel(/signature on the slip/).check();
    await mfe.getByRole('button', { name: 'Record transfer slip', exact: true }).click();
    await expect(mfe.getByText(new RegExp(`^Transfer slip ${slipNumber} recorded`))).toBeVisible({ timeout: 30_000 });
  }

  /** The purchase's row in "Transfer authorization slips". */
  purchaseRow(slipNumber: string): Locator {
    return this.shell.workspace().locator('tbody tr').filter({ hasText: slipNumber });
  }

  /** "Send & pay": the signed order goes to the Partner Bank, the payment starts as Sophie, the workflow follows it. Returns the payment's number. */
  async sendAndPay(slipNumber: string): Promise<string> {
    await this.purchaseRow(slipNumber).getByRole('button', { name: 'Send & pay', exact: true }).click();
    const placed = this.shell.workspace().getByText(/^Order \S+ placed at the Partner Bank; payment PAY-/);
    await expect(placed).toBeVisible({ timeout: 60_000 });
    return /; payment (PAY-\S+) started/.exec((await placed.textContent()) ?? '')?.[1] ?? '';
  }

  /** Selects the customer again until the purchase shows `label` (the workflow moves it on). */
  async expectPurchase(customerNumber: string, slipNumber: string, label: PurchaseLabel, timeout = 180_000): Promise<void> {
    await expect(async () => {
      await this.selectCustomer(customerNumber);
      await expect(this.purchaseRow(slipNumber)).toContainText(label, { timeout: 3_000 });
    }).toPass({ ...WORKFLOW_WAIT, timeout });
  }

  /** "Gold held" in the holding the Partner Bank reports, e.g. "0.1213 g". */
  goldHeld(): Locator {
    return this.shell.workspace().getByText(/^\d+\.\d{4} g$/).first();
  }

  /**
   * The holding shows `held`. The screen reads it from the Partner Bank on every look; when that
   * answer was too slow (e.g. its first after a restart) the screen says it could not be reached -
   * then the customer is selected again, as a person would, for up to a minute.
   */
  async expectGoldHeld(customerNumber: string, held: string | RegExp, timeout = 60_000): Promise<void> {
    await expect(async () => {
      if (!(await this.goldHeld().isVisible())) await this.selectCustomer(customerNumber);
      await expect(this.goldHeld()).toHaveText(held, { timeout: 5_000 });
    }).toPass({ ...WORKFLOW_WAIT, timeout });
  }
}