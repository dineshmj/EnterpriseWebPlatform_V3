'use client';

import { CheckCircle2, ChevronRight, Inbox } from 'lucide-react';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import { MfeShell } from './MfeShell';
import { getJson } from '../lib/api';
import { Card, CardHeader } from './ui/card';
import { Table, Td, Th, formatDateTime } from './ui/data';
import { Alert, EmptyState, StatusBadge } from './ui/feedback';

export interface KycCase {
  kycCaseId: number;
  customerNumber: string;
  customerName?: string;
  applicationNumber?: string;
  branchCode?: string;
  assignedOfficerUserId?: string | null;
  status: string;
  identityVerificationStatus?: string;
  documentVerificationStatus?: string;
  initiatedByUserId?: string | null;
  staff?: { assignedOfficer?: string | null };
  createdAt: string;
  updatedAt: string;
}

interface PageResult {
  items: KycCase[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

interface Props {
  title: string;
  subtitle: string;
  description: string;
}

export function KycCaseWorkQueue({ title, subtitle, description }: Props) {
  const [data, setData] = useState<PageResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getJson<PageResult>('/bff/api/kyc/cases?pageNumber=1&pageSize=25&status=PENDING_REVIEW')
      .then(setData)
      .catch(error => setError(error instanceof Error ? error.message : 'Unable to load KYC cases.'));
  }, []);

  return (
    <MfeShell title={title} subtitle="Customer KYC">
      <Card>
        <CardHeader icon={<Inbox />} title={subtitle} description={description} />

        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading KYC cases…</p>}

        {data && data.items.length === 0 && (
          <EmptyState icon={<CheckCircle2 />} title="Queue is clear">No KYC cases in your branch are awaiting review.</EmptyState>
        )}

        {data && data.items.length > 0 && (
          <Table>
            <thead>
              <tr>
                <Th>Application</Th>
                <Th>Customer</Th>
                <Th>Identity</Th>
                <Th>Documents</Th>
                <Th>Assigned</Th>
                <Th>Opened</Th>
                <Th><span className="sr-only">Open</span></Th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(item => (
                <tr key={item.kycCaseId} className="group hover:bg-subtle/70">
                  <Td>
                    <Link className="font-mono text-[13px] font-semibold text-brand-700 hover:underline" href={`/v1/kyc/cases/view-details?caseId=${item.kycCaseId}`}>
                      {item.applicationNumber ?? `Case ${item.kycCaseId}`}
                    </Link>
                    <div className="text-xs text-ink-faint">Case #{item.kycCaseId}</div>
                  </Td>
                  <Td>
                    <div className="font-medium text-ink">{item.customerName ?? '—'}</div>
                    <div className="font-mono text-xs text-ink-faint">{item.customerNumber}</div>
                  </Td>
                  <Td><StatusBadge status={item.identityVerificationStatus} /></Td>
                  <Td><StatusBadge status={item.documentVerificationStatus} /></Td>
                  <Td className="text-ink-muted">{item.assignedOfficerUserId ? (item.staff?.assignedOfficer ?? `${item.assignedOfficerUserId.slice(0, 8)}…`) : 'Unassigned'}</Td>
                  <Td className="text-ink-muted">{formatDateTime(item.createdAt)}</Td>
                  <Td className="text-right">
                    <Link aria-label={`Open case ${item.kycCaseId}`} href={`/v1/kyc/cases/view-details?caseId=${item.kycCaseId}`}
                      className="inline-flex size-8 items-center justify-center rounded-md text-ink-faint group-hover:text-brand-700">
                      <ChevronRight className="size-4" aria-hidden="true" />
                    </Link>
                  </Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </MfeShell>
  );
}