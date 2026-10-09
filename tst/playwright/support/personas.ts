/**
 * The demo people seeded in the IDP (IdentityAccessDb.sql) - the only users the tests sign in as.
 * Customers, applications and payments are created by the tests themselves.
 */
export type PersonaName =
  | 'sophie.cs' | 'mia.cs'
  | 'ethan.kyc' | 'liam.kyc' | 'noah.kyc'
  | 'grace.compliance' | 'olivia.compliance'
  | 'jack.accounts'
  | 'emily.payments'
  | 'daniel.ops'
  | 'sarah.audit';

export interface Persona {
  username: PersonaName;
  role: string;
  /** What the Shell's menu offers this role: context → its items (Shell MenuDB seed). */
  menu: Record<string, string[]>;
}

const customerServiceMenu = {
  'Customer Onboarding': ['View Customers', 'View Onboarding Applications', 'Onboarding Workflow Monitor'],
  Payments: ['New Payment', 'View Payments'],
};
const kycMenu = { 'Customer KYC': ['View KYC Cases', 'Identity Verification', 'Document Verification'] };
const complianceMenu = { Compliance: ['Compliance Monitor'] };

export const personas: Record<PersonaName, Persona> = {
  'sophie.cs': { username: 'sophie.cs', role: 'customer_service_agent', menu: customerServiceMenu },
  'mia.cs': { username: 'mia.cs', role: 'customer_service_agent', menu: customerServiceMenu },
  'ethan.kyc': { username: 'ethan.kyc', role: 'kyc_officer', menu: kycMenu },
  'liam.kyc': { username: 'liam.kyc', role: 'kyc_officer', menu: kycMenu },
  'noah.kyc': { username: 'noah.kyc', role: 'kyc_officer', menu: kycMenu },
  'grace.compliance': { username: 'grace.compliance', role: 'compliance_officer', menu: complianceMenu },
  'olivia.compliance': { username: 'olivia.compliance', role: 'compliance_officer', menu: complianceMenu },
  'jack.accounts': {
    username: 'jack.accounts', role: 'account_officer',
    menu: { Accounts: ['View Account Applications', 'View Accounts', 'Account Lifecycle'] },
  },
  'emily.payments': {
    username: 'emily.payments', role: 'payments_officer',
    menu: { Payments: ['View Payments', 'Payment Approvals', 'Payment Processing Monitor'] },
  },
  'daniel.ops': {
    username: 'daniel.ops', role: 'operations_administrator',
    menu: {
      'Customer Onboarding': ['View Customers', 'View Onboarding Applications', 'Onboarding Workflow Monitor'],
      Payments: ['View Payments', 'Payment Processing Monitor'],
    },
  },
  'sarah.audit': { username: 'sarah.audit', role: 'auditor', menu: { Audit: ['Audit Trail'] } },
};

/** Every context the Shell knows; a persona must see exactly the ones in its menu. */
export const allContexts = ['Customer Onboarding', 'Customer KYC', 'Compliance', 'Accounts', 'Payments', 'Audit'];

/** Where each menu item lives: context → area → item (the Shell's three menu levels). */
export const menuAreas: Record<string, string> = {
  'View Customers': 'Customer Management',
  'View Onboarding Applications': 'Onboarding Applications',
  'Onboarding Workflow Monitor': 'Onboarding Workflow',
  'View KYC Cases': 'KYC Cases',
  'Identity Verification': 'Identity Verification',
  'Document Verification': 'Document Verification',
  'Compliance Monitor': 'Compliance',
  'View Account Applications': 'Account Applications',
  'View Accounts': 'Accounts',
  'Account Lifecycle': 'Account Lifecycle',
  'New Payment': 'Payment Instructions',
  'View Payments': 'Payment Instructions',
  'Payment Approvals': 'Payment Instructions',
  'Manage Beneficiaries': 'Beneficiaries',
  'Payment Processing Monitor': 'Payment Processing',
  'Audit Trail': 'Audit',
};

/** The password from .env: EWP_PASSWORD_<USER> (dots → underscores), else EWP_PASSWORD_DEFAULT. */
export function passwordOf(username: PersonaName): string {
  const own = process.env[`EWP_PASSWORD_${username.replace(/\./g, '_').toUpperCase()}`];
  const password = own || process.env.EWP_PASSWORD_DEFAULT;
  if (!password) {
    throw new Error(`No password for ${username}: copy tst\\playwright\\.env.example to .env and fill in EWP_PASSWORD_DEFAULT (or EWP_PASSWORD_${username.replace(/\./g, '_').toUpperCase()}).`);
  }
  return password;
}