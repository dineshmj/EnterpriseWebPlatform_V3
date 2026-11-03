'use client';

import { ArrowRightLeft, CheckCircle2, Plus } from 'lucide-react';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, type ShellNotification, useShellNotifications, useVisibleRefresh } from '../../../components/MfeShell';
import { PaymentsTable } from '../../../components/PaymentsTable';
import { buttonVariants } from '../../../components/ui/button';
import { Card, CardHeader } from '../../../components/ui/card';
import { cn } from '../../../components/ui/cn';
import { Alert, EmptyState } from '../../../components/ui/feedback';
import { Pager } from '../../../components/ui/pager';
import { getJson } from '../../../lib/api';
import { type Page, type PaymentSummary, type StaffUser, getStaffUser } from '../../../lib/payments';

const FILTERS = [
  { key: '', label: 'All', empty: 'Your branch has no payments yet.' },
  { key: 'PENDING_APPROVAL', label: 'Awaiting approval', empty: 'No payments of your branch await approval.' },
  { key: 'COMPLETED', label: 'Completed', empty: 'No completed payments.' },
  { key: 'FAILED', label: 'Failed', empty: 'No failed payments.' },
  { key: 'REJECTED', label: 'Rejected', empty: 'No rejected payments.' },
  { key: 'COMPENSATION_FAILED', label: 'Action needed', empty: 'No payment needs operations to retry a release.' },
] as const;

const PAGE_SIZE = 50;

export default function PaymentsPage() {
  const [filter, setFilter] = useState<(typeof FILTERS)[number]>(FILTERS[0]);
  const [pageNumber, setPageNumber] = useState(1);
  const [data, setData] = useState<Page<PaymentSummary> | null>(null);
  const [user, setUser] = useState<StaffUser | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    getStaffUser().then(setUser).catch(() => setUser(null));
  }, []);

  // News about payments (relayed by the Shell) reloads the list at once; otherwise every 30 s while visible.
  useShellNotifications(useCallback((n: ShellNotification) => {
    if (n.target?.mfe === 'payments') setRefresh(r => r + 1);
  }, []));
  useVisibleRefresh(useCallback(() => setRefresh(r => r + 1), []));

  // A new filter shows "loading"; a refresh of the same filter keeps the rows until the new ones arrive.
  useEffect(() => { setData(null); }, [filter]);

  useEffect(() => {
    setError(null);
    const status = filter.key ? `&status=${filter.key}` : '';
    getJson<Page<PaymentSummary>>(`/bff/api/payments?pageNumber=${pageNumber}&pageSize=${PAGE_SIZE}${status}`)
      .then(result => {
        // The last payments of the last page left this list meanwhile: step back to a page that has some.
        if (result.items.length === 0 && pageNumber > 1) setPageNumber(Math.max(1, Math.ceil(result.totalCount / PAGE_SIZE)));
        else setData(result);
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load payments.'));
  }, [filter, pageNumber, refresh]);

  const mayInitiate = !!user?.roles.includes('customer_service_agent');

  return (
    <MfeShell title="Payments" subtitle="Payments">
      <Card>
        <CardHeader
          icon={<ArrowRightLeft />}
          title="Payments"
          description="Payments captured in your branch, newest first. Open one to see its status and timeline."
          actions={mayInitiate && (
            <Link className={buttonVariants({ size: 'sm' })} href="/v1/payments/new/"><Plus aria-hidden="true" />New payment</Link>
          )}
        />

        <div role="tablist" aria-label="Payment status" className="flex flex-wrap gap-1 border-b border-line px-6 pt-2">
          {FILTERS.map(f => (
            <button
              key={f.label}
              role="tab"
              type="button"
              aria-selected={f.key === filter.key}
              onClick={() => { setFilter(f); setPageNumber(1); }}
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
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading payments…</p>}
        {data && data.items.length === 0 && <EmptyState icon={<CheckCircle2 />} title="Nothing here">{filter.empty}</EmptyState>}
        {data && data.items.length > 0 && <PaymentsTable items={data.items} me={user?.sub} />}
        {data && <Pager pageNumber={data.pageNumber} pageSize={PAGE_SIZE} totalCount={data.totalCount} onPage={setPageNumber} />}
      </Card>
    </MfeShell>
  );
}