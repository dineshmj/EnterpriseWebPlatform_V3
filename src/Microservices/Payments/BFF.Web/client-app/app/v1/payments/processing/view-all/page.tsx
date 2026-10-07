'use client';

import { Activity, CheckCircle2, ChevronRight } from 'lucide-react';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, type ShellNotification, useShellNotifications } from '../../../../components/MfeShell';
import { Card, CardHeader } from '../../../../components/ui/card';
import { cn } from '../../../../components/ui/cn';
import { Table, Td, Th, formatDateTime } from '../../../../components/ui/data';
import { Alert, Badge, EmptyState, StatusBadge, statusLabel } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';
import { type ProcessingItem, type ProcessingOverview, STEP_LABEL, formatMoney } from '../../../../lib/payments';

const REFRESH_MS = 5000;

/**
 * Payment Processing Monitor: every payment saga that is not finished, most urgent first -
 * a failed compensation (operations must retry the release), overdue, retrying, waiting
 * for approval, running. Operations see all branches; payments officers their own.
 */
export default function PaymentProcessingPage() {
  const [data, setData] = useState<ProcessingOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);

  const load = useCallback(() =>
    getJson<ProcessingOverview>('/bff/api/payments/processing')
      .then(overview => { setData(overview); setError(null); })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load the processing monitor.')), []);

  useEffect(() => { void load(); }, [load, refresh]);

  // A live monitor: re-read every few seconds.
  useEffect(() => {
    const timer = window.setInterval(() => void load(), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [load]);

  useShellNotifications(useCallback((n: ShellNotification) => {
    if (n.target?.mfe === 'payments') setRefresh(r => r + 1);
  }, []));

  return (
    <MfeShell title="Payment processing monitor" subtitle="Payments">
      {data && (
        <div className="mb-6 grid gap-4 sm:grid-cols-2 xl:grid-cols-5">
          <Tile label="Compensation failed" value={data.compensationFailed} tone={data.compensationFailed > 0 ? 'danger' : 'neutral'} hint="Funds may still be held: retry the release" />
          <Tile label="Overdue" value={data.overdue} tone={data.overdue > 0 ? 'warning' : 'neutral'} hint="Timer more than a minute past due" />
          <Tile label="Retrying" value={data.retrying} tone={data.retrying > 0 ? 'warning' : 'neutral'} hint="A step needed more than one attempt" />
          <Tile label="Awaiting approval" value={data.waitingForApproval} tone="neutral" hint="Waiting for a payments officer" />
          <Tile label="Running" value={data.running} tone="neutral" hint="Working on their own" />
        </div>
      )}

      <Card>
        <CardHeader
          icon={<Activity />}
          title="Payments in progress"
          description="Every payment whose saga has not finished, most urgent first. Updates every 5 seconds."
        />
        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading…</p>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<CheckCircle2 />} title="All clear">No payment is in progress or needs attention.</EmptyState>
        )}
        {data && data.items.length > 0 && (
          <Table>
            <thead>
              <tr>
                <Th>Payment</Th>
                <Th>Branch</Th>
                <Th className="text-right">Amount</Th>
                <Th>Payment status</Th>
                <Th>Saga step</Th>
                <Th>Attempts</Th>
                <Th>Next check</Th>
                <Th>Last error</Th>
                <Th><span className="sr-only">Open</span></Th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(item => <Row key={item.paymentId} item={item} />)}
            </tbody>
          </Table>
        )}
      </Card>
    </MfeShell>
  );
}

function Row({ item }: { item: ProcessingItem }) {
  const href = `/v1/payments/view-details/?paymentId=${item.paymentId}`;
  return (
    <tr className={cn('group hover:bg-subtle/70', item.sagaStatus === 'STUCK' && 'bg-danger-50/60')}>
      <Td>
        <Link className="font-mono text-[13px] font-semibold text-brand-700 hover:underline" href={href}>{item.paymentNumber}</Link>
        <div className="text-xs text-ink-faint">{item.payeeName}</div>
      </Td>
      <Td className="text-ink-muted">{item.branchCode}</Td>
      <Td className="text-right font-semibold">{formatMoney(item.amount)}</Td>
      <Td><StatusBadge status={item.paymentStatus} /></Td>
      <Td>
        <div className="text-ink">{STEP_LABEL[item.sagaStep] ?? statusLabel(item.sagaStep)}</div>
        <div className="flex gap-1.5 text-xs text-ink-faint">
          {statusLabel(item.sagaStatus)}
          {item.overdue && <Badge tone="warning">Overdue</Badge>}
        </div>
      </Td>
      <Td className="text-ink-muted">{item.attempts}</Td>
      <Td className="text-ink-muted">{formatDateTime(item.nextCheckAt)}</Td>
      <Td className="max-w-[280px] text-xs text-ink-muted">{item.lastError ?? '—'}</Td>
      <Td className="text-right">
        <Link aria-label={`Open payment ${item.paymentNumber}`} href={href}
          className="inline-flex size-8 items-center justify-center rounded-md text-ink-faint group-hover:text-brand-700">
          <ChevronRight className="size-4" aria-hidden="true" />
        </Link>
      </Td>
    </tr>
  );
}

function Tile({ label, value, tone, hint }: { label: string; value: number; tone: 'danger' | 'warning' | 'neutral'; hint: string }) {
  return (
    <Card className={cn('px-5 py-4', tone === 'danger' && 'border-danger-200 bg-danger-50', tone === 'warning' && 'border-warning-200 bg-warning-50')}>
      <p className="text-xs font-medium uppercase tracking-wide text-ink-faint">{label}</p>
      <p className={cn('mt-1 font-display text-2xl font-semibold', tone === 'danger' ? 'text-danger-700' : tone === 'warning' ? 'text-warning-700' : 'text-ink')}>{value}</p>
      <p className="mt-0.5 text-xs text-ink-muted">{hint}</p>
    </Card>
  );
}