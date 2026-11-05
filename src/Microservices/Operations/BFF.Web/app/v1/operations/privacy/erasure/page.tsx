'use client';

import { CheckCircle2, RefreshCw, ShieldOff, XCircle } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { formatTime, MfeShell, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { ConfirmDialog } from '@/app/components/ui/confirm-dialog';
import { Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge, EmptyState } from '@/app/components/ui/feedback';
import { Field, Input, Select, Textarea } from '@/app/components/ui/form';
import { ERASURE_REASONS, type ActionResult, type ErasureReason, type ErasureRequest } from '@/lib/operations-types';
import { confirmWithAuthenticator, NOT_CONFIRMED } from '@/lib/step-up';
import { decideErasure, loadErasureRequests, requestErasure } from '../../actions';

const REASON_LABEL: Record<ErasureReason, string> = {
  CUSTOMER_REQUEST: 'The customer asked to be forgotten',
  RETENTION_ENDED: 'The retention period has ended',
};

const STATUS_TONE: Record<ErasureRequest['status'], 'warning' | 'success' | 'danger' | 'neutral'> = {
  PENDING: 'warning', ERASED: 'success', REJECTED: 'danger', EXPIRED: 'neutral',
};

const MIN_REMARKS = 10;
const MAX_REMARKS = 500;

/**
 * Runs a change; if it needs a recent authenticator code, confirms it in a small window and tries
 * ONCE more - never loops, never signs out (the same pattern as the parked-message resend).
 */
async function withStepUp<T>(change: () => Promise<ActionResult<T>>): Promise<ActionResult<T>> {
  const result = await change();
  if (result.ok || !result.stepUp) return result;
  try {
    return await confirmWithAuthenticator() ? await change() : { ok: false, error: NOT_CONFIRMED };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : NOT_CONFIRMED };
  }
}

// ┌─ :: SECURITY :: Personal-data erasure: one operator asks, another decides ─────────────────────
// │ Why : Erasing a customer cannot be undone; it must never rest on one person's click.
// │ How : This screen offers a request (customer number + reason code) and, to a DIFFERENT
// │       operator, approve or reject - each step with a recent authenticator code. It only
// │       reflects the rules: the Customer Onboarding API decides eligibility and four eyes, and
// │       the screen shows its refusal in its own words. No personal data is shown or sent.
// │ Ref : doc/Crypto-Shredding.md | GDPR Art. 17 | separation of duties
// └────────────────────────────────────────────────────────────────────────────────────────────────
export default function PersonalDataErasurePage() {
  const [requests, setRequests] = useState<ErasureRequest[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [customerNumber, setCustomerNumber] = useState('');
  const [reason, setReason] = useState<ErasureReason>('CUSTOMER_REQUEST');

  const [approving, setApproving] = useState<ErasureRequest | null>(null);
  const [rejecting, setRejecting] = useState<ErasureRequest | null>(null);
  const [remarks, setRemarks] = useState('');

  const refresh = useCallback(async () => {
    const result = await loadErasureRequests();
    if (!result.ok) {
      if (result.signedOut) { signInAgain(); return; }
      setError(result.error);
      return;
    }
    setRequests(result.data);
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  /** Shows the outcome of a change and reloads the list. */
  const finish = async (result: ActionResult<unknown>, done: string) => {
    setBusy(false);
    if (!result.ok) {
      if (result.signedOut) { signInAgain(); return false; }
      // A 404 here means the customer number (or request) does not exist - not a missing feature.
      setError(result.notAvailable ? 'No customer or erasure request with that number.' : result.error);
      return false;
    }
    setNotice(done);
    await refresh();
    return true;
  };

  const submitRequest = async () => {
    setBusy(true);
    setError(null);
    setNotice(null);
    const number = customerNumber.trim().toUpperCase();
    if (await finish(await withStepUp(() => requestErasure(number, reason)),
      `Erasure of ${number} requested. Another operator must approve it within the request's validity.`)) {
      setCustomerNumber('');
    }
  };

  const approve = async () => {
    if (!approving) return;
    const request = approving;
    setApproving(null);
    setBusy(true);
    setError(null);
    setNotice(null);
    await finish(await withStepUp(() => decideErasure(request.erasureRequestId, true)),
      `${request.customerNumber}: personal data erased. Their key is destroyed; the customer number, history and audit entries remain.`);
  };

  const reject = async () => {
    if (!rejecting) return;
    const request = rejecting;
    setBusy(true);
    setError(null);
    setNotice(null);
    if (await finish(await withStepUp(() => decideErasure(request.erasureRequestId, false, remarks)),
      `Erasure of ${request.customerNumber} rejected. Nothing was erased.`)) {
      setRejecting(null);
      setRemarks('');
    }
  };

  const pending = requests?.filter((r) => r.status === 'PENDING') ?? [];
  const history = requests?.filter((r) => r.status !== 'PENDING') ?? [];

  return (
    <MfeShell title="Personal data erasure">
      <div className="space-y-6">
        {error && <Alert tone="danger">{error}</Alert>}
        {notice && <Alert tone="success">{notice}</Alert>}

        <Card>
          <CardHeader
            icon={<ShieldOff />}
            title="Request an erasure"
            description="Erases a customer's personal data for good: their key is destroyed, so their name, contact details and address can no longer be read anywhere. Only a prospect whose record has been unchanged for the retention period qualifies. Another operator must approve it."
          />
          <CardContent>
            <form
              className="grid gap-4 sm:grid-cols-[minmax(0,14rem)_minmax(0,1fr)_auto] sm:items-end"
              onSubmit={(event) => { event.preventDefault(); void submitRequest(); }}
            >
              <Field label="Customer number" htmlFor="erasure-customer-number" required>
                <Input
                  id="erasure-customer-number"
                  value={customerNumber}
                  maxLength={30}
                  placeholder="CUS-000123"
                  autoComplete="off"
                  onChange={(event) => setCustomerNumber(event.target.value)}
                />
              </Field>
              <Field label="Reason" htmlFor="erasure-reason" required>
                <Select id="erasure-reason" value={reason} onChange={(event) => setReason(event.target.value as ErasureReason)}>
                  {ERASURE_REASONS.map((code) => <option key={code} value={code}>{REASON_LABEL[code]}</option>)}
                </Select>
              </Field>
              <Button type="submit" disabled={busy || customerNumber.trim().length === 0}>
                {busy ? 'Working…' : 'Request erasure'}
              </Button>
            </form>
          </CardContent>
        </Card>

        <Card>
          <CardHeader
            title="Waiting for a second person"
            description="Oldest first. You cannot decide a request you made yourself; a request nobody decides in time expires."
            actions={<Button variant="secondary" size="sm" onClick={() => void refresh()}><RefreshCw aria-hidden="true" />Refresh</Button>}
          />
          <CardContent className="space-y-4">
            {!requests && !error && <p className="text-sm text-ink-muted">Reading the requests…</p>}
            {requests && pending.length === 0 && <EmptyState title="Nothing is waiting">No erasure request needs a decision.</EmptyState>}
            {pending.length > 0 && (
              <Table aria-label="Erasure requests waiting for a decision">
                <thead>
                  <tr><Th>Customer</Th><Th>Reason</Th><Th>Requested</Th><Th>Expires</Th><Th /></tr>
                </thead>
                <tbody>
                  {pending.map((r) => (
                    <tr key={r.erasureRequestId}>
                      <Td className="font-mono text-[13px] font-medium">{r.customerNumber}</Td>
                      <Td>{REASON_LABEL[r.reasonCode]}</Td>
                      <Td className="text-ink-muted">{r.requestedBy}, {formatTime(r.requestedAt)}</Td>
                      <Td className="text-ink-muted">{formatTime(r.expiresAt)}</Td>
                      <Td className="text-right">
                        {r.mayDecide ? (
                          <span className="inline-flex gap-2">
                            <Button size="sm" disabled={busy} onClick={() => { setApproving(r); setNotice(null); }}>
                              <CheckCircle2 aria-hidden="true" />Approve
                            </Button>
                            <Button size="sm" variant="secondary" disabled={busy} onClick={() => { setRejecting(r); setRemarks(''); setNotice(null); }}>
                              <XCircle aria-hidden="true" />Reject
                            </Button>
                          </span>
                        ) : (
                          <span className="text-[13px] text-ink-muted">Your request: another operator decides</span>
                        )}
                      </Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}
            {rejecting && (
              <div className="space-y-3 rounded-lg border border-line bg-subtle p-4">
                <p className="text-sm">
                  Reject the erasure of <strong>{rejecting.customerNumber}</strong>. Why? Kept with the request; never published.
                </p>
                <Textarea
                  aria-label="Reason for rejecting"
                  value={remarks}
                  maxLength={MAX_REMARKS}
                  rows={2}
                  placeholder="e.g. The customer has an open complaint; the record must be kept until it is closed."
                  onChange={(event) => setRemarks(event.target.value)}
                />
                <div className="flex gap-2">
                  <Button size="sm" disabled={busy || remarks.trim().length < MIN_REMARKS} onClick={() => void reject()}>
                    {busy ? 'Rejecting…' : 'Reject'}
                  </Button>
                  <Button size="sm" variant="secondary" disabled={busy} onClick={() => setRejecting(null)}>Cancel</Button>
                  {remarks.trim().length < MIN_REMARKS && <span className="self-center text-xs text-ink-muted">At least {MIN_REMARKS} characters.</span>}
                </div>
              </div>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader title="Decided and expired" description="The latest 50. Every step is also in the audit trail (who asked, who decided, the reason code)." />
          {history.length === 0 ? (
            <CardContent><p className="text-sm text-ink-muted">None yet.</p></CardContent>
          ) : (
            <Table aria-label="Decided and expired erasure requests">
              <thead>
                <tr><Th>Customer</Th><Th>Outcome</Th><Th>Reason</Th><Th>Requested</Th><Th>Decided</Th><Th>Remarks</Th></tr>
              </thead>
              <tbody>
                {history.map((r) => (
                  <tr key={r.erasureRequestId}>
                    <Td className="font-mono text-[13px] font-medium">{r.customerNumber}</Td>
                    <Td><Badge tone={STATUS_TONE[r.status]} dot>{r.status}</Badge></Td>
                    <Td>{REASON_LABEL[r.reasonCode]}</Td>
                    <Td className="text-ink-muted">{r.requestedBy}, {formatTime(r.requestedAt)}</Td>
                    <Td className="text-ink-muted">{r.decidedBy && r.decidedAt ? `${r.decidedBy}, ${formatTime(r.decidedAt)}` : '—'}</Td>
                    <Td className="max-w-[260px] text-xs text-ink-muted">{r.decisionRemarks ?? '—'}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          )}
        </Card>
      </div>

      {approving && (
        <ConfirmDialog
          title={`Erase ${approving.customerNumber}'s personal data?`}
          cancelLabel="Keep the data"
          confirmLabel="Erase for good"
          onCancel={() => setApproving(null)}
          onConfirm={() => void approve()}
        >
          This cannot be undone. Their key is destroyed: name, contact details and address become unreadable everywhere,
          backups taken from now on included. Requested by {approving.requestedBy}: {REASON_LABEL[approving.reasonCode].toLowerCase()}.
        </ConfirmDialog>
      )}
    </MfeShell>
  );
}