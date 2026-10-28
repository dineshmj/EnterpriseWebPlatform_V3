'use client';

import {
  ArrowLeft, CheckCircle2, CircleAlert, CircleDot, Clock, Cog, FileText, Gavel, Inbox, LifeBuoy, Scale, Send, Undo2, Workflow, XCircle,
} from 'lucide-react';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, type ShellNotification, publishSelection, setUnsavedChanges, useShellNotifications } from '../../../components/MfeShell';
import { Button, buttonVariants } from '../../../components/ui/button';
import { Card, CardContent, CardFooter, CardHeader } from '../../../components/ui/card';
import { cn } from '../../../components/ui/cn';
import { ConfirmDialog } from '../../../components/ui/confirm-dialog';
import { DescriptionList, formatDateTime } from '../../../components/ui/data';
import { Alert, Skeleton, StatusBadge, statusLabel } from '../../../components/ui/feedback';
import { Field, Input, Select, Textarea } from '../../../components/ui/form';
import { getJson, postJson } from '../../../lib/api';
import {
  type PaymentDetail, type PaymentPolicy, type SagaTimelineEntry, type StaffUser,
  STEP_LABEL, TIMELINE_KIND, formatMoney, fraudCheckLabel, getStaffUser, isInProgress, personLabel,
} from '../../../lib/payments';

const REFRESH_MS = 2000;

export default function PaymentDetailsPage() {
  const [paymentId, setPaymentId] = useState<number | null>(null);
  const [data, setData] = useState<PaymentDetail | null>(null);
  const [user, setUser] = useState<StaffUser | null>(null);
  const [policy, setPolicy] = useState<PaymentPolicy | null>(null);
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
    getJson<PaymentPolicy>('/bff/api/payments/policy').then(setPolicy).catch(() => setPolicy(null));
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
  const isOfficer = !!user?.roles.includes('payments_officer');
  const isOperations = !!user?.roles.includes('operations_administrator');

  return (
    <MfeShell title={data ? `Payment · ${data.paymentNumber}` : `Payment ${paymentId ?? ''}`} subtitle="Payments">
      <div className="mb-6 flex flex-wrap gap-2">
        <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/payments/view-all/">
          <ArrowLeft aria-hidden="true" />Back to payments
        </Link>
        {isOfficer
          ? <Link className={buttonVariants({ variant: 'secondary', size: 'sm' })} href="/v1/payments/approvals/view-all/">Approval queue</Link>
          : <Link className={buttonVariants({ variant: 'secondary', size: 'sm' })} href="/v1/payments/new/">New payment</Link>}
      </div>

      {error && <Alert tone="danger">{error}</Alert>}
      {!data && !error && <Skeleton className="h-64" />}

      {data && (
        <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1.15fr)]">
          <div className="flex flex-col gap-6">
            <Outcome payment={data} />

            {data.status === 'COMPENSATION_FAILED' && isOperations && (
              <RetryReleasePanel payment={data} onRetried={() => void load(data.paymentId)} />
            )}

            {saga?.step === 'CONFIRM_WITH_NETWORK' && saga.status === 'RUNNING' && isOperations && user && (
              <ReconciliationPanel payment={data} user={user} onChanged={() => void load(data.paymentId)} />
            )}

            {data.status === 'PENDING_APPROVAL' && isOfficer && user && (
              <DecisionPanel payment={data} user={user} policy={policy} onDecided={() => void load(data.paymentId)} />
            )}

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
                    { label: 'Approval', value: !data.approvalRequired ? 'Not needed' : data.fraudCheck?.decision === 'REVIEW' ? 'Needed (fraud check: review)' : 'Needed (above the tier)' },
                    ...(data.fraudCheck ? [{ label: 'Fraud check', value: fraudCheckLabel(data.fraudCheck) }] : []),
                    { label: 'Network reference', value: data.networkReference ? <span className="font-mono text-[13px]">{data.networkReference}</span> : '—' },
                    { label: 'Started by', value: personLabel(data.initiatedByUserId, data.initiatedByLanId, user?.sub) },
                    { label: 'Branch', value: data.branchCode },
                    { label: 'Started', value: formatDateTime(data.createdAt) },
                    { label: 'Ended', value: formatDateTime(data.endedAt) },
                    ...(data.decisionByUserId ? [
                      { label: 'Decided by', value: `${personLabel(data.decisionByUserId, data.decisionByLanId, user?.sub)} · ${formatDateTime(data.decisionAt)}` },
                      { label: 'Decision remarks', value: data.decisionRemarks ?? '—' },
                    ] : []),
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

/**
 * The payments officer's decision. The screen explains the rules up front (never your own
 * payment; within your clearance's limit); the Payments API enforces them either way.
 */
function DecisionPanel({ payment, user, policy, onDecided }: {
  payment: PaymentDetail; user: StaffUser; policy: PaymentPolicy | null; onDecided: () => void;
}) {
  const [remarks, setRemarks] = useState('');
  const [confirm, setConfirm] = useState<'approve' | 'reject' | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  useEffect(() => {
    setUnsavedChanges(remarks.trim().length > 0);
    return () => setUnsavedChanges(false);
  }, [remarks]);

  const ownPayment = payment.initiatedByUserId === user.sub;
  const limit = policy?.yourApprovalLimit;
  const aboveLimit = limit !== undefined && limit !== null && payment.amount > limit;
  const mayApprove = !ownPayment && !aboveLimit;

  const decide = async (decision: 'approve' | 'reject') => {
    setConfirm(null);
    setBusy(true);
    setActionError(null);
    try {
      await postJson(`/bff/api/payments/${payment.paymentId}/${decision}`, { remarks: remarks.trim() || null });
      setRemarks('');
      setUnsavedChanges(false);
      onDecided();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'The decision could not be recorded.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <CardHeader icon={<Gavel />} title="Your decision" description="Approve to send the payment; reject to release the reserved funds." />
      <CardContent className="space-y-4">
        {ownPayment && <Alert tone="warning" title="Separation of duties">You started this payment, so another payments officer must decide it.</Alert>}
        {!ownPayment && aboveLimit && (
          <Alert tone="warning" title="Above your approval limit">
            Your clearance may approve up to {formatMoney(limit)}; this payment is {formatMoney(payment.amount)}. You may still reject it.
          </Alert>
        )}
        {actionError && <Alert tone="danger">{actionError}</Alert>}
        <Field label="Remarks" htmlFor="decision-remarks" hint="Optional to approve; required to reject. The initiator sees them.">
          <Textarea id="decision-remarks" value={remarks} maxLength={1000} onChange={e => setRemarks(e.target.value)} disabled={ownPayment || busy} />
        </Field>
      </CardContent>
      <CardFooter>
        <Button onClick={() => setConfirm('approve')} disabled={!mayApprove || busy}>Approve and send</Button>
        <Button variant="danger-outline" onClick={() => setConfirm('reject')} disabled={ownPayment || busy || !remarks.trim()}>Reject</Button>
        {!ownPayment && !remarks.trim() && <span className="text-xs text-ink-muted">Enter remarks to reject.</span>}
      </CardFooter>

      {confirm === 'approve' && (
        <ConfirmDialog title="Approve this payment?" cancelLabel="Cancel" confirmLabel="Approve and send"
          onCancel={() => setConfirm(null)} onConfirm={() => void decide('approve')}>
          {formatMoney(payment.amount)} will be sent to {payment.payeeName} ({payment.toBsb} {payment.toAccountNumber}) at once.
        </ConfirmDialog>
      )}
      {confirm === 'reject' && (
        <ConfirmDialog title="Reject this payment?" cancelLabel="Cancel" confirmLabel="Reject"
          onCancel={() => setConfirm(null)} onConfirm={() => void decide('reject')}>
          The payment will not be made and the reserved {formatMoney(payment.amount)} becomes available to the customer again.
        </ConfirmDialog>
      )}
    </Card>
  );
}

/**
 * Operations recovery: send the release of the reserved funds again. The saga records the
 * retry and continues on its own; Accounts treats the same payment idempotently.
 */
function RetryReleasePanel({ payment, onRetried }: { payment: PaymentDetail; onRetried: () => void }) {
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const retry = async () => {
    setConfirm(false);
    setBusy(true);
    setActionError(null);
    try {
      await postJson(`/bff/api/payments/${payment.paymentId}/retry-release`, {});
      onRetried();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'The retry could not be started.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card>
      <CardHeader icon={<LifeBuoy />} title="Operations recovery" description="The release of the reserved funds was not confirmed. Make sure Accounts is reachable, then retry." />
      <CardContent className="space-y-3">
        <p className="text-sm text-ink-muted">
          {formatMoney(payment.amount)} may still be held on the customer's account {payment.fromBsb} {payment.fromAccountNumber}.
          Retrying sends the release again; if it already happened, Accounts simply confirms it.
        </p>
        {actionError && <Alert tone="danger">{actionError}</Alert>}
      </CardContent>
      <CardFooter>
        <Button variant="accent" onClick={() => setConfirm(true)} disabled={busy}>{busy ? 'Retrying…' : 'Retry release'}</Button>
      </CardFooter>
      {confirm && (
        <ConfirmDialog title="Retry the release?" cancelLabel="Cancel" confirmLabel="Retry release"
          onCancel={() => setConfirm(false)} onConfirm={() => void retry()}>
          The saga sends the release of {formatMoney(payment.amount)} to Accounts again and follows it until it is confirmed.
        </ConfirmDialog>
      )}
    </Card>
  );
}

/**
 * Operations: a payment the payment network has not confirmed. The saga keeps asking the network;
 * after the alert time, one operator may propose what the network's own records show (sent, with
 * the network's reference - or not sent) with the evidence, and ANOTHER operator approves or
 * rejects it. A time-out alone never releases anything.
 */
function ReconciliationPanel({ payment, user, onChanged }: { payment: PaymentDetail; user: StaffUser; onChanged: () => void }) {
  const saga = payment.saga!;
  const proposal = saga.proposal ?? null;
  const [outcome, setOutcome] = useState<'SENT' | 'NOT_SENT'>('NOT_SENT');
  const [networkReference, setNetworkReference] = useState('');
  const [evidence, setEvidence] = useState('');
  const [remarks, setRemarks] = useState('');
  const [confirm, setConfirm] = useState<'propose' | 'approve' | 'reject' | null>(null);
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const evidenceOk = evidence.trim().length >= 10 && evidence.trim().length <= 500;
  const referenceOk = outcome === 'NOT_SENT' || (networkReference.trim().length > 0 && networkReference.trim().length <= 100);
  const remarksOk = remarks.trim().length >= 10 && remarks.trim().length <= 500;
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ The proposer sees no Approve / Reject - a convenience only: the saga refuses them
  // │ anyway (four eyes).
  // └──────────────────────────────────────────────────────────────────────────────────────
  const ownProposal = proposal?.proposedByUserId === user.sub;

  /** Why a button is still disabled: shown under the field instead of leaving the person guessing. */
  const lengthHint = (text: string, required: boolean) => {
    const n = text.trim().length;
    if (n === 0) return required ? 'Required: 10 to 500 characters.' : null;
    return n < 10 ? `${10 - n} more character${10 - n === 1 ? '' : 's'} needed (${n} of at least 10).` : null;
  };
  const evidenceHint = lengthHint(evidence, true);
  const remarksHint = lengthHint(remarks, false);

  const run = async (action: 'propose' | 'approve' | 'reject') => {
    setConfirm(null);
    setBusy(true);
    setActionError(null);
    try {
      if (action === 'propose') {
        await postJson(`/bff/api/payments/${payment.paymentId}/network-resolution`, {
          outcome, networkReference: outcome === 'SENT' ? networkReference.trim() : null, evidence: evidence.trim(),
        });
      } else {
        await postJson(`/bff/api/payments/${payment.paymentId}/network-resolution/${action}`, { remarks: remarks.trim() || null });
      }
      setEvidence(''); setNetworkReference(''); setRemarks('');
      onChanged();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'The action could not be completed.');
    } finally {
      setBusy(false);
    }
  };

  const describe = (o: string, reference?: string | null) =>
    o === 'SENT' ? `the network SENT the payment (reference ${reference})` : 'the network did NOT send the payment';

  return (
    <Card>
      <CardHeader
        icon={<Scale />}
        title="Reconciliation with the payment network"
        description={`Unconfirmed since ${formatDateTime(saga.unconfirmedSince)}. The funds stay reserved, and the network is asked again and again; its own answer always wins.`}
      />
      <CardContent className="space-y-4">
        {!saga.unconfirmedTooLong && (
          <Alert tone="info">
            The automatic enquiry is still running. If the network has not answered after {saga.unconfirmedAlertMinutes} minutes,
            operations can resolve the payment by hand, with a second person approving.
          </Alert>
        )}

        {saga.unconfirmedTooLong && !proposal && (
          <>
            <p className="text-sm text-ink-muted">
              Check the network&apos;s own records (statement, the network&apos;s operations desk), then propose what happened.
              A second operator must approve before anything changes.
            </p>
            <Field label="What the network's records show" htmlFor="resolution-outcome" required>
              <Select id="resolution-outcome" value={outcome} onChange={e => setOutcome(e.target.value as 'SENT' | 'NOT_SENT')} disabled={busy}>
                <option value="NOT_SENT">Not sent - release the reserved funds</option>
                <option value="SENT">Sent - settle (debit) the reserved funds</option>
              </Select>
            </Field>
            {outcome === 'SENT' && (
              <Field label="The network's reference for the payment" htmlFor="resolution-reference" required>
                <Input id="resolution-reference" value={networkReference} maxLength={100} onChange={e => setNetworkReference(e.target.value)} disabled={busy} />
              </Field>
            )}
            <Field label="Evidence" htmlFor="resolution-evidence" required hint="Where you saw it, e.g. the statement line or ticket number (10 to 500 characters). Recorded with your LAN ID.">
              <Textarea id="resolution-evidence" value={evidence} maxLength={500} onChange={e => setEvidence(e.target.value)} disabled={busy}
                placeholder="e.g. NPP statement 13-OCT, no entry for this payment; ticket OPS-1234" aria-describedby="resolution-evidence-status" />
              {evidenceHint && <p id="resolution-evidence-status" className="text-xs text-warning-700">{evidenceHint}</p>}
            </Field>
            {outcome === 'SENT' && !referenceOk && <p className="text-xs text-warning-700">The network&apos;s reference is required to resolve the payment as sent.</p>}
          </>
        )}

        {proposal && (
          <>
            <Alert tone="warning" title="Waiting for a second person">
              {proposal.proposedByLabel} proposes: {describe(proposal.proposedOutcome, proposal.networkReference)}.
              Evidence: {proposal.evidence} ({formatDateTime(proposal.proposedAt)})
            </Alert>
            {ownProposal
              ? <p className="text-sm text-ink-muted">You proposed this resolution: another operator must approve or reject it.</p>
              : (
                <Field label="Remarks" htmlFor="resolution-remarks" hint="Optional to approve; required to reject (10 to 500 characters).">
                  <Textarea id="resolution-remarks" value={remarks} maxLength={500} onChange={e => setRemarks(e.target.value)} disabled={busy}
                    aria-describedby="resolution-remarks-status" />
                  {remarksHint && <p id="resolution-remarks-status" className="text-xs text-warning-700">{remarksHint} Remarks are required to reject.</p>}
                </Field>
              )}
          </>
        )}

        {actionError && <Alert tone="danger">{actionError}</Alert>}
      </CardContent>

      {saga.unconfirmedTooLong && !proposal && (
        <CardFooter>
          <Button variant="accent" onClick={() => setConfirm('propose')} disabled={busy || !evidenceOk || !referenceOk}>
            {busy ? 'Proposing…' : 'Propose resolution'}
          </Button>
        </CardFooter>
      )}
      {proposal && !ownProposal && (
        <CardFooter className="gap-3">
          <Button variant="secondary" onClick={() => setConfirm('reject')} disabled={busy || !remarksOk}>Reject</Button>
          <Button variant="accent" onClick={() => setConfirm('approve')} disabled={busy}>{busy ? 'Working…' : 'Approve'}</Button>
        </CardFooter>
      )}

      {confirm === 'propose' && (
        <ConfirmDialog title="Propose this resolution?" cancelLabel="Cancel" confirmLabel="Propose"
          onCancel={() => setConfirm(null)} onConfirm={() => void run('propose')}>
          You state that {describe(outcome, networkReference.trim())}. Nothing changes until a second operator approves.
        </ConfirmDialog>
      )}
      {confirm === 'approve' && proposal && (
        <ConfirmDialog title="Approve this resolution?" cancelLabel="Cancel" confirmLabel="Approve"
          onCancel={() => setConfirm(null)} onConfirm={() => void run('approve')}>
          {proposal.proposedOutcome === 'SENT'
            ? `The reserved ${formatMoney(payment.amount)} will be debited from the customer's account: the payment is treated as sent.`
            : `The reserved ${formatMoney(payment.amount)} will be released to the customer: the payment is treated as never sent.`}
        </ConfirmDialog>
      )}
      {confirm === 'reject' && proposal && (
        <ConfirmDialog title="Reject this resolution?" cancelLabel="Cancel" confirmLabel="Reject"
          onCancel={() => setConfirm(null)} onConfirm={() => void run('reject')}>
          The proposal is dropped; the network is still asked, and a new resolution can be proposed.
        </ConfirmDialog>
      )}
    </Card>
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
    case 'NETWORK_CONFIRMED_SENT':
    case 'APPROVED':
      return { icon: CheckCircle2, tone: 'text-success-700' };
    case 'NETWORK_CONFIRMED_NOT_SENT':
      return { icon: XCircle, tone: 'text-danger-700' };
    case 'NETWORK_STATUS_UNKNOWN':
      return { icon: CircleAlert, tone: 'text-warning-700' };
    case 'NETWORK_REFUSED':
    case 'COMPENSATION_FAILED':
    case 'REJECTED_BY_APPROVER':
      return { icon: XCircle, tone: 'text-danger-700' };
    case 'NETWORK_UNAVAILABLE':
    case 'TIMEOUT':
      return { icon: CircleAlert, tone: 'text-warning-700' };
    case 'COMMAND_SENT':
      return { icon: Send, tone: 'text-brand-700' };
    case 'REPLY_RECEIVED':
      return { icon: Inbox, tone: 'text-brand-700' };
    case 'RETRY':
    case 'MESSAGE_RESENT':
      return { icon: Undo2, tone: 'text-warning-700' };
    case 'RESOLUTION_PROPOSED':
    case 'RESOLUTION_REJECTED':
    case 'RESOLUTION_SUPERSEDED':
      return { icon: Scale, tone: 'text-warning-700' };
    case 'RESOLUTION_APPROVED':
      return { icon: Scale, tone: 'text-success-700' };
    case 'REPLY_IGNORED':
      return { icon: Clock, tone: 'text-ink-faint' };
    default:
      return { icon: CircleDot, tone: 'text-ink-muted' };
  }
}