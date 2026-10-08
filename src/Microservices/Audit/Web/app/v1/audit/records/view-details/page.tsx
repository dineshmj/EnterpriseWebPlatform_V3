'use client';

import { ArrowLeft, History, Landmark } from 'lucide-react';
import Link from 'next/link';
import { useSearchParams } from 'next/navigation';
import { Suspense, useEffect, useState } from 'react';
import { formatAmount, formatTime, MfeShell, personLabel, signInAgain } from '@/app/components/MfeShell';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { DescriptionList } from '@/app/components/ui/data';
import { Alert, Badge, StatusBadge } from '@/app/components/ui/feedback';
import type { RecordJourney } from '@/lib/audit-types';
import { openRecord } from '../../actions';

/**
 * One record, end to end - the Journey API's composition: the audit timeline (Audit API) and,
 * for a payment, where it stands now (Payments API), fetched in one call. Opening it is itself
 * recorded in the trail.
 */
function RecordPage() {
  const recordRef = useSearchParams().get('recordRef') ?? '';
  const [journey, setJourney] = useState<RecordJourney | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      const result = await openRecord(recordRef);
      if (result.ok) { setJourney(result.data); return; }
      if (result.signedOut) { signInAgain(); return; }
      setError(result.error);
    })();
  }, [recordRef]);

  const payment = journey?.current?.payment as Record<string, unknown> | undefined;

  return (
    <MfeShell title={recordRef || 'Record'} subtitle="Audit trail of one record">
      <div className="space-y-6">
        <Link href="/v1/audit/trail/view-all" className="inline-flex items-center gap-1.5 text-sm font-medium text-brand-700 hover:underline">
          <ArrowLeft className="size-4" aria-hidden="true" /> Back to the search
        </Link>

        {error && <Alert tone="danger">{error}</Alert>}

        {journey?.current && (
          <Card>
            <CardHeader icon={<Landmark />} title="Where it stands now" description={`From the ${journey.current.source} service, at this moment - not from the trail`} />
            <CardContent>
              {payment ? (
                <DescriptionList columns={3} items={[
                  { label: 'Status', value: <StatusBadge status={String(payment.status ?? '')} /> },
                  { label: 'Amount', value: formatAmount(Number(payment.amount), String(payment.currency ?? 'AUD')) },
                  { label: 'Customer', value: String(payment.customerNumber ?? '') },
                  { label: 'Payee', value: String(payment.payeeName ?? '') },
                  { label: 'Network reference', value: String(payment.networkReference ?? '—') },
                  { label: 'Branch', value: String(payment.branchCode ?? '') },
                ]} />
              ) : (
                <Alert tone="warning">The current status is unavailable: {journey.current.unavailable}. The audit timeline below is complete.</Alert>
              )}
            </CardContent>
          </Card>
        )}

        <Card>
          <CardHeader icon={<History />} title="What happened" description={journey ? `${journey.timeline.length} entries, oldest first` : 'Loading…'} />
          <CardContent>
            <ol className="relative space-y-5 border-l border-line pl-6">
              {journey?.timeline.map((e) => (
                <li key={e.sequence} className="relative">
                  <span className="absolute -left-[29px] top-1.5 size-2.5 rounded-full bg-brand-700" aria-hidden="true" />
                  <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
                    <span className="font-medium text-ink">{e.eventType}</span>
                    {e.status && <Badge tone="neutral">{e.status}</Badge>}
                    {e.amount !== null && <span className="text-sm text-ink-muted">{formatAmount(e.amount, e.currency)}</span>}
                  </div>
                  <p className="mt-0.5 text-xs text-ink-muted">
                    {formatTime(e.occurredAt)} · {e.source}
                    {e.initiatedBy && <> · initiated by <b>{personLabel(e.initiatedBy)}</b></>}
                    {e.actor && <> · decided by <b>{personLabel(e.actor)}</b></>}
                    {e.reasonCode && <> · {e.reasonCode}</>}
                  </p>
                  <p className="mt-0.5 font-mono text-[11px] text-ink-faint" title="Entry hash (chained to the previous entry) and the SHA-256 of the original message">
                    #{e.sequence} · {e.entryHash.slice(0, 16)}…{e.payloadSha256 && <> · message {e.payloadSha256.slice(0, 12)}…</>}
                  </p>
                </li>
              ))}
            </ol>
          </CardContent>
        </Card>
      </div>
    </MfeShell>
  );
}

export default function Page() {
  return <Suspense><RecordPage /></Suspense>;
}