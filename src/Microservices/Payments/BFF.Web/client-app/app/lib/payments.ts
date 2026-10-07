import { getJson } from './api';

export interface Page<T> {
  items: T[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

/** A payment in the branch's list. */
export interface PaymentSummary {
  paymentId: number;
  paymentNumber: string;
  customerNumber: string;
  amount: number;
  currency: string;
  payeeName: string;
  toBsb: string;
  toAccountNumber: string;
  status: string;
  approvalRequired: boolean;
  createdAt: string;
  updatedAt: string;
  initiatedByUserId: string;
  initiatedByLanId?: string | null;
}

/** One line of the saga's timeline. */
export interface SagaTimelineEntry {
  at: string;
  step: string;
  kind: string;
  detail: string;
  messageId?: string | null;
}

/** The saga (orchestrator state) behind a payment. */
export interface SagaView {
  sagaId: string;
  step: string;
  status: string;
  attempts: number;
  nextCheckAt?: string | null;
  lastError?: string | null;
  workflowId: string;
  correlationId: string;
  timeline: SagaTimelineEntry[];
}

export interface PaymentDetail {
  paymentId: number;
  paymentRef: string;
  paymentNumber: string;
  customerNumber: string;
  fromBsb: string;
  fromAccountNumber: string;
  payeeName: string;
  toBsb: string;
  toAccountNumber: string;
  amount: number;
  currency: string;
  reference?: string | null;
  branchCode: string;
  status: string;
  approvalRequired: boolean;
  outcomeCode?: string | null;
  outcomeReason?: string | null;
  networkReference?: string | null;
  createdAt: string;
  updatedAt: string;
  endedAt?: string | null;
  initiatedByUserId: string;
  initiatedByLanId?: string | null;
  saga?: SagaView | null;
  /** The payments officer's decision (approval tier). */
  decisionByUserId?: string | null;
  decisionByLanId?: string | null;
  decisionAt?: string | null;
  decisionRemarks?: string | null;
}

/** A saga operations should look at (Payment Processing Monitor). */
export interface ProcessingItem {
  paymentId: number;
  paymentNumber: string;
  branchCode: string;
  amount: number;
  currency: string;
  payeeName: string;
  paymentStatus: string;
  sagaStep: string;
  sagaStatus: string;
  attempts: number;
  nextCheckAt?: string | null;
  lastError?: string | null;
  updatedAt: string;
  overdue: boolean;
}

export interface ProcessingOverview {
  compensationFailed: number;
  overdue: number;
  retrying: number;
  waitingForApproval: number;
  running: number;
  items: ProcessingItem[];
}

/** A customer's paying account (from Accounts), with what is available now. */
export interface PayerAccount {
  customerNumber: string;
  holderName: string;
  bsb: string;
  accountNumber: string;
  product: string;
  currency: string;
  available: number;
}

export interface BsbInfo {
  bsb: string;
  bank: string;
  branch: string;
  state: string;
  acceptsRealTimePayments: boolean;
}

export interface PayeeConfirmation {
  result: 'MATCH' | 'CLOSE_MATCH' | 'NO_MATCH' | string;
  accountNameHeld?: string | null;
}

export interface PaymentPolicy {
  currency: string;
  approvalThreshold: number;
  maxAmount: number;
  referenceMaxLength: number;
  /** What the signed-in person's clearance may approve; null = no limit, 0 = may not approve. */
  yourApprovalLimit?: number | null;
}

/** The signed-in staff member, from the BFF (display only: the APIs decide). */
export interface StaffUser {
  sub: string;
  name: string;
  roles: string[];
  branch?: string;
  lanId?: string;
  clearance: number;
}

export async function getStaffUser(): Promise<StaffUser> {
  const claims = await getJson<{ type: string; value: string }[]>('/api/auth/user');
  const value = (type: string) => claims.find(c => c.type === type)?.value;
  return {
    sub: value('sub') ?? '',
    name: value('name') ?? '',
    roles: claims.filter(c => c.type === 'role').map(c => c.value),
    branch: value('branch'),
    lanId: value('lan_id'),
    clearance: Number(value('clearance_level') ?? 0) || 0,
  };
}

/** Statuses after which nothing more happens without a person (or ever). */
const SETTLED_STATUSES = ['COMPLETED', 'REJECTED', 'FAILED', 'COMPENSATION_FAILED', 'PENDING_APPROVAL'];

/** True while the saga is working on its own: the status page keeps refreshing. */
export function isInProgress(status: string) {
  return !SETTLED_STATUSES.includes(status);
}

const money = new Intl.NumberFormat('en-AU', { style: 'currency', currency: 'AUD' });

export function formatMoney(amount: number | null | undefined) {
  return amount == null ? '—' : money.format(amount);
}

/** "062000" or "062 000" → "062-000"; anything else unchanged. */
export function normaliseBsb(value: string) {
  const digits = value.replace(/\D/g, '');
  return digits.length === 6 ? `${digits.slice(0, 3)}-${digits.slice(3)}` : value.trim();
}

export const productLabel = (code: string) =>
  ({ EVERYDAY_TRANSACTION: 'Everyday', SAVINGS: 'Savings' } as Record<string, string>)[code] ?? code;

/** Plain-language labels for the saga's timeline. */
export const TIMELINE_KIND: Record<string, string> = {
  STARTED: 'Payment started',
  COMMAND_SENT: 'Instruction sent to Accounts',
  REPLY_RECEIVED: 'Answer from Accounts',
  REPLY_IGNORED: 'Late or duplicate answer ignored',
  DECIDED: 'Next step decided',
  NETWORK_ACCEPTED: 'Payment network accepted',
  NETWORK_REFUSED: 'Payment network refused',
  NETWORK_UNAVAILABLE: 'Payment network did not answer',
  TIMEOUT: 'No answer in time',
  COMPENSATION_FAILED: 'Release not confirmed',
  RETRY: 'Release retried',
  APPROVED: 'Approved by a payments officer',
  REJECTED_BY_APPROVER: 'Rejected by a payments officer',
  FINISHED: 'Finished',
};

export const STEP_LABEL: Record<string, string> = {
  RESERVE_FUNDS: 'Reserve funds',
  AWAIT_APPROVAL: 'Approval',
  SEND_TO_NETWORK: 'Send to network',
  SETTLE_FUNDS: 'Settle funds',
  RELEASE_FUNDS: 'Release funds',
  DONE: 'Done',
};

/** Who did it, as staff see it: "You", the LAN ID, or a short form of the subject ID. */
export function personLabel(userId: string, lanId: string | null | undefined, me: string | undefined) {
  if (me && userId === me) return 'You';
  return lanId ?? `${userId.slice(0, 8)}…`;
}