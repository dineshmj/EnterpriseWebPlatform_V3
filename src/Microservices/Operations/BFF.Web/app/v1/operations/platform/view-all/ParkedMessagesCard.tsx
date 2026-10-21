'use client';

import { Inbox, RefreshCw, Send } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { formatTime, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge, EmptyState } from '@/app/components/ui/feedback';
import { Textarea } from '@/app/components/ui/form';
import type { ContextParkedMessages, ParkedMessage } from '@/lib/operations-types';
import { confirmWithAuthenticator, NOT_CONFIRMED } from '@/lib/step-up';
import { loadParkedMessages, resendParkedMessage } from '../../actions';

const CONTEXT_LABEL: Record<string, string> = { accounts: 'Accounts', compliance: 'Compliance', 'partner-products': 'Partner Products' };
const MIN_REASON = 10;
const MAX_REASON = 500;

/**
 * Parked Outbox messages of Accounts, Compliance and Partner Products: what the relay gave up on (Kafka unreachable
 * for long), and the resend of one of them - the same message, with a reason, a recent
 * authenticator code and workflow.reprocess. (Payments' parked messages are on its Processing Monitor.)
 */
export function ParkedMessagesCard() {
  const [contexts, setContexts] = useState<ContextParkedMessages[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [resending, setResending] = useState<{ context: string; message: ParkedMessage } | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    const result = await loadParkedMessages();
    if (!result.ok) {
      if (result.signedOut) { signInAgain(); return; }
      setError(result.error);
      return;
    }
    setContexts(result.data);
    setError(null);
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const resend = async () => {
    if (!resending) return;
    setBusy(true);
    setError(null);
    const { context, message } = resending;
    let result = await resendParkedMessage(context, message.messageId, reason);
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ If the resend needs a recent authenticator code, confirm it in a small window and
    // │ try ONCE more - never loop, never sign out.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (!result.ok && result.stepUp) {
      try {
        result = await confirmWithAuthenticator()
          ? await resendParkedMessage(context, message.messageId, reason)
          : { ok: false, error: NOT_CONFIRMED };
      } catch (e) {
        result = { ok: false, error: e instanceof Error ? e.message : NOT_CONFIRMED };
      }
    }
    setBusy(false);
    if (!result.ok) {
      if (result.signedOut) { signInAgain(); return; }
      setError(result.error);
      return;
    }
    setNotice(`${message.eventType} handed back to the ${CONTEXT_LABEL[context]} relay - it is published on its next cycle.`);
    setResending(null);
    setReason('');
    void refresh();
  };

  const total = contexts?.reduce((sum, c) => sum + c.messages.length, 0) ?? 0;
  const trimmed = reason.trim().length;

  return (
    <Card>
      <CardHeader
        icon={<Inbox />}
        title="Parked messages - Accounts, Compliance and Partner Products"
        description="Messages the Outbox relay gave up on after its attempts (the message broker could not take them). Make sure Kafka is up, then resend: the same message goes out again, and a receiver that already has it ignores the copy."
        actions={<Button variant="secondary" size="sm" onClick={() => void refresh()}><RefreshCw aria-hidden="true" />Refresh</Button>}
      />
      <CardContent className="space-y-4">
        {error && <Alert tone="danger">{error}</Alert>}
        {notice && <Alert tone="success">{notice}</Alert>}
        {contexts?.filter((c) => !c.available).map((c) => (
          <Alert key={c.context} tone="warning">{CONTEXT_LABEL[c.context]} could not be asked right now - its parked messages are not shown.</Alert>
        ))}
        {!contexts && !error && <p className="text-sm text-ink-muted">Reading the parked messages…</p>}
        {contexts && total === 0 && <EmptyState title="Nothing is parked">Every message of Accounts, Compliance and Partner Products was published.</EmptyState>}
        {contexts && total > 0 && (
          <Table aria-label="Parked messages">
            <thead>
              <tr><Th>Context</Th><Th>Message</Th><Th>Record</Th><Th>Since</Th><Th>Attempts</Th><Th>Last error</Th><Th>Resent before</Th><Th /></tr>
            </thead>
            <tbody>
              {contexts.flatMap((c) => c.messages.map((m) => (
                <tr key={m.messageId}>
                  <Td>{CONTEXT_LABEL[c.context]}</Td>
                  <Td className="font-medium">
                    {m.eventType}
                    {m.heldBack > 0 && <span className="block text-[12px] text-ink-muted">{m.heldBack} later message(s) of this record wait behind it</span>}
                  </Td>
                  <Td className="font-mono text-[12px] text-ink-muted">{m.aggregateType} {m.aggregateId}</Td>
                  <Td className="text-ink-muted">{formatTime(m.occurredAt)}</Td>
                  <Td><Badge tone="warning">{m.attemptCount}</Badge></Td>
                  <Td className="max-w-[260px] text-xs text-ink-muted">{m.lastError ?? '—'}</Td>
                  <Td className="text-xs text-ink-muted">
                    {m.redriveCount > 0 ? `${m.redriveCount}× - last by ${m.lastRedrivenBy}: ${m.lastRedriveReason}` : '—'}
                  </Td>
                  <Td>
                    <Button size="sm" variant="secondary" disabled={busy} onClick={() => { setResending({ context: c.context, message: m }); setReason(''); setNotice(null); }}>
                      <Send aria-hidden="true" />Resend
                    </Button>
                  </Td>
                </tr>
              )))}
            </tbody>
          </Table>
        )}
        {resending && (
          <div className="space-y-3 rounded-lg border border-line bg-subtle p-4">
            <p className="text-sm">
              Resend <strong>{resending.message.eventType}</strong> ({CONTEXT_LABEL[resending.context]}). Why? It is recorded with your LAN ID.
            </p>
            <Textarea
              aria-label="Reason for the resend"
              value={reason}
              maxLength={MAX_REASON}
              rows={2}
              placeholder="e.g. Kafka was down 14:05-14:40; it is back and healthy."
              onChange={(event) => setReason(event.target.value)}
            />
            <div className="flex gap-2">
              <Button size="sm" disabled={busy || trimmed < MIN_REASON} onClick={() => void resend()}>
                {busy ? 'Resending…' : 'Resend'}
              </Button>
              <Button size="sm" variant="secondary" disabled={busy} onClick={() => setResending(null)}>Cancel</Button>
              {trimmed < MIN_REASON && <span className="self-center text-xs text-ink-muted">At least {MIN_REASON} characters.</span>}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}