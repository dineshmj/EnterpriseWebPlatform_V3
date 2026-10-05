'use client';

import { CheckCircle2, ChevronRight, Inbox } from 'lucide-react';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import { MfeShell } from '../../../components/MfeShell';
import { RiskBadge } from '../../../components/RiskBadge';
import { Card, CardHeader } from '../../../components/ui/card';
import { cn } from '../../../components/ui/cn';
import { Table, Td, Th, formatDateTime } from '../../../components/ui/data';
import { Alert, EmptyState, StatusBadge } from '../../../components/ui/feedback';
import { getJson } from '../../../lib/api';
import { type ComplianceCasePage, type Officer, getOfficer, shortId } from '../../../lib/compliance';

const FILTERS = [
  { key: 'UNDER_REVIEW', label: 'Awaiting decision', empty: 'No cases in your branch are awaiting a compliance decision.' },
  { key: 'ON_HOLD', label: 'On hold', empty: 'No cases in your branch are on hold.' },
  { key: 'SCREENING', label: 'Screening', empty: 'No cases are waiting for the screening provider.' },
  { key: '', label: 'All', empty: 'Your branch has no compliance cases yet.' },
] as const;

export default function ComplianceWorkQueuePage() {
  const [filter, setFilter] = useState<(typeof FILTERS)[number]>(FILTERS[0]);
  const [data, setData] = useState<ComplianceCasePage | null>(null);
  const [officer, setOfficer] = useState<Officer | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getOfficer().then(setOfficer).catch(() => setOfficer(null));
  }, []);

  useEffect(() => {
    setData(null);
    setError(null);
    const status = filter.key ? `&status=${filter.key}` : '';
    getJson<ComplianceCasePage>(`/bff/api/compliance/cases?pageNumber=1&pageSize=50${status}`)
      .then(setData)
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load compliance cases.'));
  }, [filter]);

  const assignee = (id?: string | null) =>
    !id ? 'Unassigned' : id === officer?.sub ? 'You' : shortId(id);

  return (
    <MfeShell title="Compliance work queue" subtitle="Compliance">
      <Card>
        <CardHeader
          icon={<Inbox />}
          title="Compliance cases"
          description="Applications of your branch that passed KYC, screened against AML / sanctions / PEP lists. Open a case to review and decide it."
        />

        <div role="tablist" aria-label="Case status" className="flex flex-wrap gap-1 border-b border-line px-6 pt-2">
          {FILTERS.map(f => (
            <button
              key={f.label}
              role="tab"
              type="button"
              aria-selected={f.key === filter.key}
              onClick={() => setFilter(f)}
              className={cn(
                '-mb-px border-b-2 px-3 py-2 text-[13px] font-medium transition-colors',
                f.key === filter.key ? 'border-accent-600 text-ink' : 'border-transparent text-ink-muted hover:text-ink',
              )}
            >
              {f.label}
            </button>
          ))}
        </div>

        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading compliance cases…</p>}

        {data && data.items.length === 0 && (
          <EmptyState icon={<CheckCircle2 />} title="Nothing here">{filter.empty}</EmptyState>
        )}

        {data && data.items.length > 0 && (
          <Table>
            <thead>
              <tr>
                <Th>Application</Th>
                <Th>Customer</Th>
                <Th>Risk</Th>
                <Th>Status</Th>
                <Th>Assigned</Th>
                <Th>Opened</Th>
                <Th><span className="sr-only">Open</span></Th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(item => {
                const href = `/v1/compliance/cases/view-details?caseId=${item.complianceCaseId}`;
                return (
                  <tr key={item.complianceCaseId} className="group hover:bg-subtle/70">
                    <Td>
                      <Link className="font-mono text-[13px] font-semibold text-brand-700 hover:underline" href={href}>
                        {item.applicationNumber}
                      </Link>
                      <div className="text-xs text-ink-faint">Case #{item.complianceCaseId}</div>
                    </Td>
                    <Td>
                    <div className="font-medium text-ink">{item.customerName}</div>
                    <div className="font-mono text-xs text-ink-faint">{item.customerNumber}</div>
                  </Td>
                    <Td><RiskBadge risk={item.riskRating} clearance={item.requiredClearance} /></Td>
                    <Td><StatusBadge status={item.status} /></Td>
                    <Td className="text-ink-muted">{assignee(item.assignedOfficerUserId)}</Td>
                    <Td className="text-ink-muted">{formatDateTime(item.createdAt)}</Td>
                    <Td className="text-right">
                      <Link aria-label={`Open case ${item.complianceCaseId}`} href={href}
                        className="inline-flex size-8 items-center justify-center rounded-md text-ink-faint group-hover:text-brand-700">
                        <ChevronRight className="size-4" aria-hidden="true" />
                      </Link>
                    </Td>
                  </tr>
                );
              })}
            </tbody>
          </Table>
        )}
      </Card>
    </MfeShell>
  );
}