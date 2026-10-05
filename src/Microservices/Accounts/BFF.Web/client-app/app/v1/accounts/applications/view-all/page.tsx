'use client';

import { CheckCircle2, ChevronRight, Inbox } from 'lucide-react';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import { MfeShell } from '../../../../components/MfeShell';
import { Card, CardHeader } from '../../../../components/ui/card';
import { cn } from '../../../../components/ui/cn';
import { Table, Td, Th, formatDateTime } from '../../../../components/ui/data';
import { Alert, EmptyState, StatusBadge } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';
import { type AccountApplication, type Officer, type Page, getOfficer, productLabel, shortId } from '../../../../lib/accounts';

const FILTERS = [
  { key: 'PENDING_REVIEW', label: 'Awaiting decision', empty: 'No account applications of your branch are awaiting a decision.' },
  { key: 'ON_HOLD', label: 'On hold', empty: 'No account applications of your branch are on hold.' },
  { key: 'OPENING', label: 'Opening', empty: 'No approved accounts are waiting for the core-banking system.' },
  { key: 'FAILED', label: 'Failed', empty: 'No account openings have failed.' },
  { key: '', label: 'All', empty: 'Your branch has no account applications yet.' },
] as const;

export default function AccountApplicationsPage() {
  const [filter, setFilter] = useState<(typeof FILTERS)[number]>(FILTERS[0]);
  const [data, setData] = useState<Page<AccountApplication> | null>(null);
  const [officer, setOfficer] = useState<Officer | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getOfficer().then(setOfficer).catch(() => setOfficer(null));
  }, []);

  useEffect(() => {
    setData(null);
    setError(null);
    const status = filter.key ? `&status=${filter.key}` : '';
    getJson<Page<AccountApplication>>(`/bff/api/accounts/applications?pageNumber=1&pageSize=50${status}`)
      .then(setData)
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load account applications.'));
  }, [filter]);

  const assignee = (id?: string | null, lanId?: string | null) =>
    !id ? 'Unassigned' : id === officer?.sub ? 'You' : (lanId ?? shortId(id));

  return (
    <MfeShell title="Account applications" subtitle="Accounts">
      <Card>
        <CardHeader
          icon={<Inbox />}
          title="Account applications"
          description="Onboarding applications of your branch that passed KYC and Compliance. Open one to decide the account opening."
        />

        <div role="tablist" aria-label="Application status" className="flex flex-wrap gap-1 border-b border-line px-6 pt-2">
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
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading account applications…</p>}

        {data && data.items.length === 0 && (
          <EmptyState icon={<CheckCircle2 />} title="Nothing here">{filter.empty}</EmptyState>
        )}

        {data && data.items.length > 0 && (
          <Table>
            <thead>
              <tr>
                <Th>Application</Th>
                <Th>Customer</Th>
                <Th>Product</Th>
                <Th>Status</Th>
                <Th>Assigned</Th>
                <Th>Received</Th>
                <Th><span className="sr-only">Open</span></Th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(item => {
                const href = `/v1/accounts/applications/view-details?applicationId=${item.accountApplicationId}`;
                return (
                  <tr key={item.accountApplicationId} className="group hover:bg-subtle/70">
                    <Td>
                      <Link className="font-mono text-[13px] font-semibold text-brand-700 hover:underline" href={href}>
                        {item.applicationNumber}
                      </Link>
                      <div className="text-xs text-ink-faint">Application #{item.accountApplicationId}</div>
                    </Td>
                    <Td>
                    <div className="font-medium text-ink">{item.customerName}</div>
                    <div className="font-mono text-xs text-ink-faint">{item.customerNumber}</div>
                  </Td>
                    <Td className="text-ink-muted">{item.product ? productLabel(item.product) : '—'}</Td>
                    <Td><StatusBadge status={item.status} /></Td>
                    <Td className="text-ink-muted">{assignee(item.assignedOfficerUserId, item.staff?.assignedOfficer)}</Td>
                    <Td className="text-ink-muted">{formatDateTime(item.createdAt)}</Td>
                    <Td className="text-right">
                      <Link aria-label={`Open application ${item.accountApplicationId}`} href={href}
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