import { getJson } from './api';

/** An account application as returned by the Accounts API (through this BFF). */
export interface AccountApplication {
  accountApplicationId: number;
  applicationNumber: string;
  customerNumber: string;
  /** The applicant's name as Compliance cleared it: the future account holder. */
  customerName: string;
  /** LAN IDs of the people on the application, for display (rules use the subject IDs). */
  staff?: {
    initiatedBy?: string | null;
    complianceApprovedBy?: string | null;
    assignedOfficer?: string | null;
    decisionBy?: string | null;
  };
  complianceCaseId: number;
  branchCode: string;
  status: string;
  product?: string | null;
  initiatedByUserId?: string | null;
  complianceApprovedByUserId?: string | null;
  assignedOfficerUserId?: string | null;
  holdReason?: string | null;
  decisionByUserId?: string | null;
  decisionAt?: string | null;
  decisionRemarks?: string | null;
  openingAttempts: number;
  nextOpeningAt?: string | null;
  lastOpeningError?: string | null;
  accountNumber?: string | null;
  openedAt?: string | null;
  failureReason?: string | null;
  createdAt: string;
  updatedAt: string;
}

/** An opened account (issued by the core-banking system). */
export interface Account {
  accountId: number;
  accountNumber: string;
  bsb: string;
  customerNumber: string;
  /** The name the account is held in. */
  holderName: string;
  branchCode: string;
  product: string;
  status: string;
  coreBankingReference: string;
  openedAt: string;
}

export interface Page<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

/** The signed-in officer, for display hints only: every rule is enforced by the Accounts API. */
export interface Officer {
  sub: string;
  name?: string;
  branch?: string;
}

export async function getOfficer(): Promise<Officer> {
  const claims = await getJson<{ type: string; value: string }[]>('/api/auth/user');
  const value = (type: string) => claims.find(c => c.type === type)?.value;
  return { sub: value('sub') ?? '', name: value('name'), branch: value('branch') };
}

export const PRODUCTS = [
  { code: 'EVERYDAY_TRANSACTION', label: 'Everyday transaction account' },
  { code: 'SAVINGS', label: 'Savings account' },
] as const;

export const productLabel = (code?: string | null) =>
  PRODUCTS.find(p => p.code === code)?.label ?? (code ? code : '—');

export const shortId = (id: string | null | undefined) => (id ? `${id.slice(0, 8)}…` : '—');

/** BSB and account number as a bank shows them: 062-000 10000001. */
export const formatAccount = (bsb?: string | null, number?: string | null) =>
  bsb && number ? `${bsb} ${number}` : number ?? '—';

/** The officer's decision is final once the application is rejected or handed to core banking. */
export const isDecided = (status: string) => ['REJECTED', 'OPENING', 'OPENED', 'FAILED'].includes(status);

/** Why this officer may not act on the application (separation of duties), or null. Mirrors the API's rule. */
export function separationOfDutiesConflict(a: AccountApplication, officerSub: string): string | null {
  if (!officerSub) return null;
  if (a.initiatedByUserId === officerSub) return 'You initiated this onboarding, so you cannot open its account.';
  if (a.complianceApprovedByUserId === officerSub) return 'You approved this application in Compliance, so you cannot also open its account.';
  return null;
}