import { getJson } from './api';

/** A compliance case as returned by the Compliance API (through this BFF). */
export interface ComplianceCase {
  complianceCaseId: number;
  applicationNumber: string;
  customerNumber: string;
  /** The applicant as KYC verified them (Compliance's snapshot). */
  customerName: string;
  residentialAddress: ApplicantAddress | null;
  kycCaseId: number;
  branchCode: string;
  status: string;
  screeningOutcome?: string | null;
  screeningProvider?: string | null;
  screeningReference?: string | null;
  screenedAt?: string | null;
  screeningAttempts: number;
  nextScreeningAt?: string | null;
  lastScreeningError?: string | null;
  riskRating?: string | null;
  requiredClearance?: number | null;
  initiatedByUserId?: string | null;
  kycIdentityDecidedByUserId?: string | null;
  kycDocumentDecidedByUserId?: string | null;
  assignedOfficerUserId?: string | null;
  holdReason?: string | null;
  decisionByUserId?: string | null;
  decisionAt?: string | null;
  decisionRemarks?: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface ComplianceCasePage {
  items: ComplianceCase[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

/** The signed-in officer, for display hints only: every rule is enforced by the Compliance API. */
export interface Officer {
  sub: string;
  name?: string;
  branch?: string;
  clearance?: number;
}

export async function getOfficer(): Promise<Officer> {
  const claims = await getJson<{ type: string; value: string }[]>('/api/auth/user');
  const value = (type: string) => claims.find(c => c.type === type)?.value;
  const clearance = Number(value('clearance_level'));
  return {
    sub: value('sub') ?? '',
    name: value('name'),
    branch: value('branch'),
    clearance: Number.isFinite(clearance) && clearance > 0 ? clearance : undefined,
  };
}

export const shortId = (id: string | null | undefined) => (id ? `${id.slice(0, 8)}…` : '—');

export const isDecided = (status: string) => status === 'APPROVED' || status === 'REJECTED';

/** Why this officer may not act on the case (separation of duties), or null. Mirrors the API's rule. */
export function separationOfDutiesConflict(c: ComplianceCase, officerSub: string): string | null {
  if (!officerSub) return null;
  if (c.initiatedByUserId === officerSub) return 'You initiated this onboarding, so you cannot handle its compliance case.';
  if (c.kycIdentityDecidedByUserId === officerSub || c.kycDocumentDecidedByUserId === officerSub)
    return 'You decided this customer\'s KYC, so you cannot also handle its compliance case.';
  return null;
}

/** The applicant's residential address as KYC verified it. */
export interface ApplicantAddress {
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  state?: string | null;
  postalCode?: string | null;
  countryCode?: string | null;
}

/** "12 George St, Sydney NSW 2000, AU" - or an em dash when nothing is recorded. */
export function formatAddress(address: ApplicantAddress | null | undefined): string {
  if (!address) return '—';
  const locality = [address.city, address.state, address.postalCode].filter(Boolean).join(' ');
  const parts = [address.addressLine1, address.addressLine2, locality, address.countryCode].filter(Boolean);
  return parts.length > 0 ? parts.join(', ') : '—';
}