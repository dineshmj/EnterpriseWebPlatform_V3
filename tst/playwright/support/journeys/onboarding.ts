import { test } from '@playwright/test';
import { lanIds, type ExpectedEntry } from '../audit';
import type { Evidence, NewCustomer } from '../data';
import { AccountsMfe, ComplianceMfe, KycMfe, OnboardingMfe, type Submitted } from '../pages/mfes';
import type { ShellPage } from '../pages/ShellPage';

/**
 * The steps of customer onboarding, each as the person who does it - shared by the journeys and
 * the seed scenarios. Each step waits for the work to reach that person (through Kafka).
 */
export async function submitApplication(agent: ShellPage, customer: NewCustomer, evidence?: Evidence): Promise<Submitted> {
  return test.step(`Sophie submits ${customer.firstName} ${customer.lastName}`, async () => {
    const onboarding = new OnboardingMfe(agent);
    await onboarding.open();
    return onboarding.submitNewCustomer(customer, evidence);
  });
}

export async function kycDecision(officer: ShellPage, applicationNumber: string, stage: 'Identity verification' | 'Document verification', action: 'Approve' | 'Reject'): Promise<void> {
  await test.step(`KYC officer: ${stage} - ${action}`, async () => {
    const kyc = new KycMfe(officer);
    await kyc.openCase(stage, applicationNumber);
    await kyc.decide(action, action === 'Approve' ? `${stage}: evidence checked.` : `${stage}: the evidence does not match the applicant.`);
  });
}

export async function complianceDecision(officer: ShellPage, applicationNumber: string, action: 'Approve' | 'Reject'): Promise<void> {
  await test.step(`Compliance officer: ${action}`, async () => {
    const compliance = new ComplianceMfe(officer);
    await compliance.openCase(applicationNumber);
    await compliance.expectCaseStatus('Under review');
    if (action === 'Approve') await compliance.approve('Screening reviewed; no concerns.');
    else await compliance.reject('Adverse information found during review.');
  });
}

export async function accountDecision(officer: ShellPage, applicationNumber: string, action: 'Approve' | 'Reject'): Promise<void> {
  await test.step(`Account officer: ${action}`, async () => {
    const accounts = new AccountsMfe(officer);
    await accounts.openCase(applicationNumber);
    await accounts.expectCaseStatus('Pending review');
    if (action === 'Approve') {
      // Opening happens a moment later, at core banking. It is proven by Sophie's status
      // ("Completed") and the trail's AccountOpened - "Opened" on this page is also a date label.
      await accounts.approve('Approved for account opening.');
    } else {
      await accounts.reject('Product not suitable for this customer.');
    }
  });
}

export async function expectOnboardingStatus(agent: ShellPage, applicationNumber: string, status: string): Promise<void> {
  await test.step(`Sophie sees the application "${status}"`, async () => {
    const onboarding = new OnboardingMfe(agent);
    await onboarding.open();
    await onboarding.expectStatus(applicationNumber, status);
  });
}

/** What the Audit Trail must hold, step by step, for each way an onboarding can end. */
export const onboardingTrail = {
  submitted: [{ event: 'OnboardingApplicationSubmitted' }, { event: 'KycCaseCreated' }] as ExpectedEntry[],
  kycApproved: (officer: keyof typeof lanIds): ExpectedEntry[] => [
    { event: 'KycIdentityVerificationApproved', decidedBy: lanIds[officer] },
    { event: 'KycDocumentVerificationApproved', decidedBy: lanIds[officer] },
    { event: 'KycCaseApproved' },
  ],
  complianceScreened: [{ event: 'ComplianceCaseCreated' }, { event: 'ComplianceCaseScreened' }] as ExpectedEntry[],
};