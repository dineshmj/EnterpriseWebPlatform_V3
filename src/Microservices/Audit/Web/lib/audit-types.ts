/** Shapes shared by the server actions and the pages (the Journey API's answers). */

export type ActionResult<T> =
  | { ok: true; data: T }
  | { ok: false; error: string; signedOut?: boolean };

export interface SearchInput {
  record?: string;
  person?: string;
  eventType?: string;
  from?: string;
  to?: string;
  kind?: 'EVENT' | 'ACCESS';
  pageNumber?: number;
  pageSize?: number;
}

export interface Person {
  userId: string;
  lanId: string | null;
}

export interface AuditEntry {
  sequence: number;
  kind: 'EVENT' | 'ACCESS';
  eventType: string;
  source: string | null;
  occurredAt: string;
  recordedAt: string;
  recordType: string | null;
  recordRef: string | null;
  customerNumber: string | null;
  branchCode: string | null;
  status: string | null;
  reasonCode: string | null;
  amount: number | null;
  currency: string | null;
  initiatedBy: Person | null;
  actor: Person | null;
  workflowId: string | null;
  payloadSha256: string | null;
  entryHash: string;
}

export interface AuditPage {
  items: AuditEntry[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

export interface RecordJourney {
  recordRef: string;
  current: { source: string; payment?: Record<string, unknown>; unavailable?: string } | null;
  timeline: AuditEntry[];
}

export interface IntegrityResult {
  intact: boolean;
  entriesChecked: number;
  brokenAtSequence: number | null;
  problem: string | null;
  verifiedAt: string;
}