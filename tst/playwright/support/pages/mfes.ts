import { expect, type Locator } from '@playwright/test';
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

/** Customer Onboarding: "View Onboarding Applications" - the form and the agent's recent applications. */
export class OnboardingMfe {
  static readonly title = 'New onboarding application';

  constructor(private readonly shell: ShellPage) {}

  async open(): Promise<void> {
    await this.shell.openMenuItem('Customer Onboarding', 'View Onboarding Applications');
    await this.shell.waitForScreen(OnboardingMfe.title);
  }

  /**
   * Fills the form for a new customer, attaches both PDFs and submits; returns the numbers shown.
   * Without `evidence`, small generated PDFs are attached (fast, and unique per test).
   */
  async submitNewCustomer(customer: NewCustomer, evidence?: Evidence): Promise<Submitted> {
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
    await mfe.locator('#taxProof').setInputFiles(evidence?.taxProof ?? samplePdf(`Tax proof ${customer.lastName}`));
    await mfe.getByRole('button', { name: 'Submit application', exact: true }).click();

    const success = mfe.getByRole('status').filter({ hasText: 'Application submitted' });
    await expect(success).toBeVisible({ timeout: 60_000 });
    const text = (await success.textContent()) ?? '';
    const customerNumber = /Customer\s+(\S+)/.exec(text)?.[1];
    const applicationNumber = /application\s+(\S+?)\.?$/m.exec(text.trim())?.[1] ?? /application\s+(\S+)/.exec(text)?.[1];
    if (!customerNumber || !applicationNumber) throw new Error(`Could not read the numbers from "${text}".`);
    return { customerNumber, applicationNumber: applicationNumber.replace(/\.$/, '') };
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