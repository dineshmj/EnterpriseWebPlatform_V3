/**
 * Every deployable part of EWP V3 and its readiness probe (ReadMe.txt sections 3 and 9a; the same
 * list as tst/playwright/support/services.ts). Probed server-side only: the screen never sees a URL.
 */
export interface PlatformComponent {
  name: string;
  group: 'Front ends' | 'Identity' | 'Domain APIs' | 'Journey APIs' | 'Workers' | 'Simulators';
  readiness: string;
}

export const components: PlatformComponent[] = [
  { name: 'IDP', group: 'Identity', readiness: 'https://idp.dev.localhost:46392/health/ready' },

  { name: 'Shell', group: 'Front ends', readiness: 'https://shell.dev.localhost:46367/health/ready' },
  { name: 'Customer Onboarding BFF', group: 'Front ends', readiness: 'https://customer.dev.localhost:46311/health/ready' },
  { name: 'Customer KYC BFF', group: 'Front ends', readiness: 'https://kyc.dev.localhost:33800/health/ready' },
  { name: 'Compliance BFF', group: 'Front ends', readiness: 'https://compliance.dev.localhost:46399/health/ready' },
  { name: 'Accounts BFF', group: 'Front ends', readiness: 'https://accounts.dev.localhost:45456/health/ready' },
  { name: 'Payments BFF', group: 'Front ends', readiness: 'https://payments.dev.localhost:46388/health/ready' },
  { name: 'Audit web', group: 'Front ends', readiness: 'https://audit.dev.localhost:46380/' },
  { name: 'Operations web', group: 'Front ends', readiness: 'https://operations.dev.localhost:46381/' },

  { name: 'Customer Onboarding API', group: 'Domain APIs', readiness: 'https://customer-api.dev.localhost:46363/health/ready' },
  { name: 'Customer KYC API', group: 'Domain APIs', readiness: 'https://kyc-api.dev.localhost:46305/health/ready' },
  { name: 'Documents Management API', group: 'Domain APIs', readiness: 'https://documents-management-api.dev.localhost:49486/health/ready' },
  { name: 'Compliance API', group: 'Domain APIs', readiness: 'https://compliance-api.dev.localhost:46306/health/ready' },
  { name: 'Accounts API', group: 'Domain APIs', readiness: 'https://accounts-api.dev.localhost:48486/health/ready' },
  { name: 'Payments API', group: 'Domain APIs', readiness: 'https://payments-api.dev.localhost:44488/health/ready' },
  { name: 'Notifications API', group: 'Domain APIs', readiness: 'https://notifications-api.dev.localhost:46377/health/ready' },
  { name: 'Audit API', group: 'Domain APIs', readiness: 'https://audit-api.dev.localhost:46378/health/ready' },

  { name: 'Audit Journey API', group: 'Journey APIs', readiness: 'https://audit-journey.dev.localhost:46379/health/ready' },
  { name: 'Operations Journey API', group: 'Journey APIs', readiness: 'https://operations-journey.dev.localhost:46382/health/ready' },

  { name: 'CustomerOutboxPublisher', group: 'Workers', readiness: 'http://localhost:5101/health/ready' },
  { name: 'KycCaseOpeningSubscriber', group: 'Workers', readiness: 'http://localhost:5102/health/ready' },
  { name: 'OnboardingOutcomeSubscriber', group: 'Workers', readiness: 'http://localhost:5103/health/ready' },
  { name: 'ComplianceCaseOpeningSubscriber', group: 'Workers', readiness: 'http://localhost:5104/health/ready' },
  { name: 'DocumentInvalidationSubscriber', group: 'Workers', readiness: 'http://localhost:5105/health/ready' },
  { name: 'AccountApplicationOpeningSubscriber', group: 'Workers', readiness: 'http://localhost:5106/health/ready' },
  { name: 'NotificationsSubscriber', group: 'Workers', readiness: 'http://localhost:5107/health/ready' },
  { name: 'AccountsCommandSubscriber', group: 'Workers', readiness: 'http://localhost:5108/health/ready' },
  { name: 'PaymentsSagaReplySubscriber', group: 'Workers', readiness: 'http://localhost:5109/health/ready' },

  { name: 'Screening Provider (simulator)', group: 'Simulators', readiness: 'https://localhost:46366/health' },
  { name: 'Core Banking (simulator)', group: 'Simulators', readiness: 'https://localhost:46376/health' },
  { name: 'Payment Network (simulator)', group: 'Simulators', readiness: 'https://localhost:46386/health' },
];

/** The simulators whose behaviour can be switched (PUT /admin/behaviour, loopback callers only). */
export const simulators = [
  { key: 'screening-provider', name: 'Screening Provider', baseUrl: 'https://localhost:46366', affects: 'Compliance screening' },
  { key: 'core-banking', name: 'Core Banking', baseUrl: 'https://localhost:46376', affects: 'Account opening' },
  { key: 'payment-network', name: 'Payment Network', baseUrl: 'https://localhost:46386', affects: 'Sending payments' },
] as const;

export type SimulatorKey = (typeof simulators)[number]['key'];

export const behaviours = ['Healthy', 'Slow', 'Failing', 'Down', 'Refusing'] as const;
export type Behaviour = (typeof behaviours)[number];