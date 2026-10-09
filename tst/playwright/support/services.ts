/**
 * Every running part of EWP V3 and where it answers (ReadMe.txt sections 3 and 9a).
 * The external-system simulators have no health endpoint and are not listed.
 */
export const urls = {
  idp: 'https://idp.dev.localhost:46392',
  shell: 'https://shell.dev.localhost:46367',
  customerOnboardingMfe: 'https://customer.dev.localhost:46311',
  kycMfe: 'https://kyc.dev.localhost:33800',
  complianceMfe: 'https://compliance.dev.localhost:46399',
  accountsMfe: 'https://accounts.dev.localhost:45456',
  paymentsMfe: 'https://payments.dev.localhost:46388',
  auditMfe: 'https://audit.dev.localhost:46380',
} as const;

export interface Service {
  name: string;
  /** The readiness probe; for a service without one, any page that proves it answers. */
  readiness: string;
  /** How it is started, for the error message when it is down. */
  startedBy: 'Visual Studio' | 'Start-NodeServices.ps1';
}

const vs = 'Visual Studio' as const;
const node = 'Start-NodeServices.ps1' as const;

export const services: Service[] = [
  { name: 'IDP', readiness: `${urls.idp}/health/ready`, startedBy: vs },
  { name: 'Shell BFF', readiness: `${urls.shell}/health/ready`, startedBy: vs },
  { name: 'Customer Onboarding BFF', readiness: `${urls.customerOnboardingMfe}/health/ready`, startedBy: vs },
  { name: 'Customer Onboarding API', readiness: 'https://customer-api.dev.localhost:46363/health/ready', startedBy: vs },
  { name: 'Customer KYC BFF', readiness: `${urls.kycMfe}/health/ready`, startedBy: node },
  { name: 'Customer KYC API', readiness: 'https://kyc-api.dev.localhost:46305/health/ready', startedBy: vs },
  { name: 'Documents Management API', readiness: 'https://documents-management-api.dev.localhost:49486/health/ready', startedBy: vs },
  { name: 'Compliance BFF', readiness: `${urls.complianceMfe}/health/ready`, startedBy: vs },
  { name: 'Compliance API', readiness: 'https://compliance-api.dev.localhost:46306/health/ready', startedBy: vs },
  { name: 'Accounts BFF', readiness: `${urls.accountsMfe}/health/ready`, startedBy: vs },
  { name: 'Accounts API', readiness: 'https://accounts-api.dev.localhost:48486/health/ready', startedBy: vs },
  { name: 'Payments BFF', readiness: `${urls.paymentsMfe}/health/ready`, startedBy: vs },
  { name: 'Payments API', readiness: 'https://payments-api.dev.localhost:44488/health/ready', startedBy: vs },
  { name: 'Notifications API', readiness: 'https://notifications-api.dev.localhost:46377/health/ready', startedBy: vs },
  { name: 'Audit API', readiness: 'https://audit-api.dev.localhost:46378/health/ready', startedBy: vs },
  { name: 'Audit Journey API', readiness: 'https://audit-journey.dev.localhost:46379/health/ready', startedBy: node },
  { name: 'Audit web', readiness: `${urls.auditMfe}/`, startedBy: node },
  { name: 'CustomerOutboxPublisher', readiness: 'http://localhost:5101/health/ready', startedBy: vs },
  { name: 'KycCaseOpeningSubscriber', readiness: 'http://localhost:5102/health/ready', startedBy: vs },
  { name: 'OnboardingOutcomeSubscriber', readiness: 'http://localhost:5103/health/ready', startedBy: vs },
  { name: 'ComplianceCaseOpeningSubscriber', readiness: 'http://localhost:5104/health/ready', startedBy: vs },
  { name: 'DocumentInvalidationSubscriber', readiness: 'http://localhost:5105/health/ready', startedBy: vs },
  { name: 'AccountApplicationOpeningSubscriber', readiness: 'http://localhost:5106/health/ready', startedBy: vs },
  { name: 'NotificationsSubscriber', readiness: 'http://localhost:5107/health/ready', startedBy: vs },
  { name: 'AccountsCommandSubscriber', readiness: 'http://localhost:5108/health/ready', startedBy: vs },
  { name: 'PaymentsSagaReplySubscriber', readiness: 'http://localhost:5109/health/ready', startedBy: vs },
];