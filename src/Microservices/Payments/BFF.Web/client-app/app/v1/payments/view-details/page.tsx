'use client';

import {
  ArrowLeft, CheckCircle2, CircleAlert, CircleDot, Clock, Cog, FileText, Inbox, Send, Undo2, Workflow, XCircle,
} from 'lucide-react';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, type ShellNotification, publishSelection, useShellNotifications } from '../../../components/MfeShell';
import { buttonVariants } from '../../../components/ui/button';
import { Card, CardContent, CardHeader } from '../../../components/ui/card';
import { cn } from '../../../components/ui/cn';
import { DescriptionList, formatDateTime } from '../../../components/ui/data';
import { Alert, Skeleton, StatusBadge, statusLabel } from '../../../components/ui/feedback';
import { getJson } from '../../../lib/api';
import {
  type PaymentDetail, type SagaTimelineEntry, type StaffUser,
  STEP_LABEL, TIMELINE_KIND, formatMoney, getStaffUser, isInProgress, personLabel,
} from '../../../lib/payments';

const REFRESH_MS = 2000;

export default function PaymentDetailsPage() {
  const [paymentId, setPaymentId] = useState<number | null>(null);
  const [data, setData] = useState<PaymentDetail | null>(null);
  const [user, setUser] = useState<StaffUser | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback((id: number) =>
    getJson<PaymentDetail>(`/bff/api/payments/${id}`)
      .then(detail => {
        setData(detail);
        setError(null);
        publishSelection(
          [{ title: 'Customer Number', value: detail.customerNumber }],
          [
            { title: 'Payment Number', value: detail.paymentNumber },
            { title: 'Payment Status', value: statusLabel(detail.status) },
          ],
        );
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load the payment.')), []);

  useEffect(() => {
    const parsed = Number(new URLSearchParams(window.location.search).get('paymentId'));
    if (!Number.isSafeInteger(parsed) || parsed <= 0) {
      setError('A valid Payment ID was not supplied.');
      return;
    }
    setPaymentId(parsed);
    void load(parsed);
    getStaffUser().then(setUser).catch(() => setUser(null));
  }, [load]);

  // While the saga works on its own, follow it: re-read every 2 seconds until the payment
  // completes, ends, or waits for a person. (The bell announces the final outcome.)
  useEffect(() => {
    if (!data || !isInProgress(data.status)) return;
    const timer = window.setInterval(() => void load(data.paymentId), REFRESH_MS);
    return () => window.clearInterval(timer);
  }, [data, load]);

  // A notification about this payment (relayed by the Shell) refreshes at once.
  useShellNotifications(useCallback((n: ShellNotification) => {
    if (n.target?.mfe === 'payments' && n.target.recordId === paymentId && paymentId) void load(paymentId);
  }, [paymentId, load]));

  const saga = data?.saga;

  return (
    <MfeShell title={data ? `Payment · ${data.paymentNumber}` : `Payment ${paymentId ?? ''}`} subtitle="Payments">
      <div className="mb-6 flex flex-wrap gap-2">
        <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/payments/view-all/">
          <ArrowLeft aria-hidden="true" />Back to payments
        </Link>
        <Link className={buttonVariants({ variant: 'secondary', size: 'sm' })} href="/v1/payments/new/">New payment</Link>
      </div>

      {error && <Alert tone="danger">{error}</Alert>}
      {!data && !error && <Skeleton className="h-64" />}

      {data && (
        <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)]">
          <div className="flex flex-col gap-6">
            <Outcome payment={data} />

            <Card>
              <CardHeader icon={<FileText />} title="Payment" actions={<StatusBadge status={data.status} />} />
              <CardContent>
                <DescriptionList
                  items={[
                    { label: 'Payment', value: <span className="font-mono text-[13px]">{data.paymentNumber}</span> },
                    { label: 'Amount', value: <span className="font-semibold">{formatMoney(data.amount)}</span> },
                    { label: 'From', value: <span className="font-mono text-[13px]">{data.fromBsb} {data.fromAccountNumber}</span> },
                    { label: 'Customer', value: <span className="font-mono text-[13px]">{data.customerNumber}</span> },
                    { label: 'To', value: <><span className="font-medium">{data.payeeName}</span><br /><span className="font-mono text-[13px]">{data.toBsb} {data.toAccountNumber}</span></> },
                    { label: 'Reference', value: data.reference ?? '—' },
                    { label: 'Approval', value: data.approvalRequired ? 'Needed (above the tier)' : 'Not needed' },
                    { label: 'Network reference', value: data.networkReference ? <span className="font-mono text-[13px]">{data.networkReference}</span> : '—' },
                    { label: 'Started by', value: personLabel(data.initiatedByUserId, data.initiatedByLanId, user?.sub) },
                    { label: 'Branch', value: data.branchCode },
                    { label: 'Started', value: formatDateTime(data.createdAt) },
                    { label: 'Ended', value: formatDateTime(data.endedAt) },
                  ]}
                />
              </CardContent>
            </Card>

            {saga && (
              <Card>
                <CardHeader icon={<Cog />} title="Saga" description="The orchestrator's saved state: where this payment is in its workflow." />
                <CardContent>
                  <DescriptionList
                    items={[
                      { label: 'Step', value: STEP_LABEL[saga.step] ?? statusLabel(saga.step) },
                      { label: 'State', value: statusLabel(saga.status) },
                      { label: 'Attempts at this step', value: saga.attempts },
                      { label: 'Next check', value: formatDateTime(saga.nextCheckAt) },
                      { label: 'Workflow ID', value: <span className="font-mono text-xs">{saga.workflowId}</span> },
                      { label: 'Last error', value: saga.lastError ?? '—' },
                    ]}
                  />
                </CardContent>
              </Card>
            )}
          </div>

          <Card>
            <CardHeader
              icon={<Workflow />}
              title="Timeline"
              description={isInProgress(data.status) ? 'Live: updates every 2 seconds while the payment is in progress.' : 'Everything the saga did and received, in order.'}
            />
            <CardContent>
              {saga && saga.timeline.length > 0 ? (
                <ol className="relative space-y-4 border-l border-line pl-6">
                  {saga.timeline.map((entry, i) => <TimelineItem key={i} entry={entry} />)}
                </ol>
              ) : (
                <p className="text-sm text-ink-muted">No steps yet.</p>
              )}
            </CardContent>
          </Card>
        </div>
      )}
    </MfeShell>
  );
}

function Outcome({ payment }: { payment: PaymentDetail }) {
  switch (payment.status) {
    case 'COMPLETED':
      return <Alert tone="success" title="Payment completed">{formatMoney(payment.amount)} was sent to {payment.payeeName} and debited from the customer's account.</Alert>;
    case 'REJECTED':
      return <Alert tone="danger" title="Payment rejected">{payment.outcomeReason ?? 'The payment was not made.'} Nothing was debited.</Alert>;
    case 'FAILED':
      return <Alert tone="danger" title="Payment failed - funds released">{payment.outcomeReason ?? 'The payment could not be sent.'} The reserved funds are available to the customer again.</Alert>;
    case 'COMPENSATION_FAILED':
      return <Alert tone="danger" title="Action needed - funds may still be held">{payment.outcomeReason} The release of the reserved funds is not confirmed; operations must retry it. The payment was not sent.</Alert>;
    case 'PENDING_APPROVAL':
      return <Alert tone="warning" title="Waiting for a payments officer">The funds are reserved. The payment is sent once a payments officer approves it.</Alert>;
    case 'COMPENSATING':
      return <Alert tone="warning" title="Releasing the reserved funds">{payment.outcomeReason}</Alert>;
    default:
      return <Alert tone="info" title="In progress">The payment is being processed. This page follows it live.</Alert>;
  }
}

function TimelineItem({ entry }: { entry: SagaTimelineEntry }) {
  const { icon: Icon, tone } = timelineLook(entry.kind);
  return (
    <li className="relative">
      <span className={cn('absolute -left-[33px] flex size-[18px] items-center justify-center rounded-full bg-surface ring-4 ring-surface', tone)}>
        <Icon className="size-[18px]" aria-hidden="true" />
      </span>
      <div className="flex flex-wrap items-baseline justify-between gap-x-3">
        <p className="text-sm font-medium text-ink">{TIMELINE_KIND[entry.kind] ?? statusLabel(entry.kind)}</p>
        <p className="font-mono text-xs text-ink-faint">{new Date(entry.at).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })}</p>
      </div>
      <p className="text-[13px] leading-5 text-ink-muted">{entry.detail}</p>
      <p className="mt-0.5 text-xs text-ink-faint">
        {STEP_LABEL[entry.step] ?? entry.step}
        {entry.messageId && <> · message <span className="font-mono">{entry.messageId.slice(0, 8)}</span></>}
      </p>
    </li>
  );
}

function timelineLook(kind: string) {
  switch (kind) {
    case 'FINISHED':
    case 'NETWORK_ACCEPTED':
      return { icon: CheckCircle2, tone: 'text-success-700' };
    case 'NETWORK_REFUSED':
    case 'COMPENSATION_FAILED':
      return { icon: XCircle, tone: 'text-danger-700' };
    case 'NETWORK_UNAVAILABLE':
    case 'TIMEOUT':
      return { icon: CircleAlert, tone: 'text-warning-700' };
    case 'COMMAND_SENT':
      return { icon: Send, tone: 'text-brand-700' };
    case 'REPLY_RECEIVED':
      return { icon: Inbox, tone: 'text-brand-700' };
    case 'RETRY':
      return { icon: Undo2, tone: 'text-warning-700' };
    case 'REPLY_IGNORED':
      return { icon: Clock, tone: 'text-ink-faint' };
    default:
      return { icon: CircleDot, tone: 'text-ink-muted' };
  }
}