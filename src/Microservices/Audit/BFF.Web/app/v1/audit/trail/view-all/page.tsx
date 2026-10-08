'use client';

import { Search, ShieldCheck, ShieldAlert, ScrollText } from 'lucide-react';
import Link from 'next/link';
import { FormEvent, useCallback, useEffect, useState } from 'react';
import { formatAmount, formatTime, MfeShell, personLabel, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge, EmptyState } from '@/app/components/ui/feedback';
import { Field, Input, Select } from '@/app/components/ui/form';
import type { AuditPage, IntegrityResult, SearchInput } from '@/lib/audit-types';
import { searchTrail, verifyIntegrity } from '../../actions';

const PAGE_SIZE = 25;

const EVENT_TYPES = [
  'OnboardingApplicationSubmitted', 'OnboardingApplicationRejected',
  'KycCaseCreated', 'KycIdentityVerificationApproved', 'KycIdentityVerificationRejected',
  'KycDocumentVerificationApproved', 'KycDocumentVerificationRejected', 'KycCaseApproved', 'KycCaseRejected',
  'ComplianceCaseCreated', 'ComplianceCaseScreened', 'ComplianceCaseApproved', 'ComplianceCaseRejected',
  'AccountApplicationCreated', 'AccountApplicationRejected', 'AccountOpened', 'AccountOpeningFailed',
  'FundsReserved', 'FundsReservationFailed', 'FundsSettled', 'FundsReleased',
  'PaymentApprovalRequired', 'PaymentCompleted', 'PaymentRejected', 'PaymentFailed', 'PaymentCompensationFailed',
];

/** The auditor's search over the whole trail, plus "Verify integrity" of the hash chain. */
export default function AuditTrailPage() {
  const [form, setForm] = useState<SearchInput>({ kind: 'EVENT' });
  const [query, setQuery] = useState<SearchInput>({ kind: 'EVENT', pageNumber: 1 });
  const [page, setPage] = useState<AuditPage | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [integrity, setIntegrity] = useState<IntegrityResult | null>(null);
  const [verifying, setVerifying] = useState(false);

  const load = useCallback(async (input: SearchInput) => {
    setLoading(true);
    const result = await searchTrail({ ...input, from: toUtc(input.from), to: toUtc(input.to, true), pageSize: PAGE_SIZE });
    setLoading(false);
    if (result.ok) { setPage(result.data); setError(null); return; }
    if (result.signedOut) { signInAgain(); return; }
    setError(result.error);
  }, []);

  useEffect(() => { void load(query); }, [query, load]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    setQuery({ ...form, pageNumber: 1 });
  };

  const verify = async () => {
    setVerifying(true);
    const result = await verifyIntegrity();
    setVerifying(false);
    if (result.ok) { setIntegrity(result.data); return; }
    if (result.signedOut) { signInAgain(); return; }
    setError(result.error);
  };

  const pages = page ? Math.max(1, Math.ceil(page.totalCount / PAGE_SIZE)) : 1;
  const update = (name: keyof SearchInput) => (event: { target: { value: string } }) =>
    setForm((f) => ({ ...f, [name]: event.target.value }));

  return (
    <MfeShell title="Audit Trail">
      <div className="space-y-6">
        <Card>
          <CardHeader
            icon={<Search />}
            title="Search the trail"
            description="Every business event on the platform, recorded once and never changed. Every search you make is recorded too."
            actions={
              <Button variant="secondary" onClick={verify} disabled={verifying}>
                <ShieldCheck className="size-4" aria-hidden="true" /> {verifying ? 'Verifying…' : 'Verify integrity'}
              </Button>
            }
          />
          <CardContent>
            <form onSubmit={submit} className="grid gap-4 md:grid-cols-3 xl:grid-cols-6">
              <Field label="Record or customer" htmlFor="record" hint="PAY-…, APP-…, CUST-…">
                <Input id="record" value={form.record ?? ''} onChange={update('record')} maxLength={100} />
              </Field>
              <Field label="Person" htmlFor="person" hint="LAN ID, e.g. somit">
                <Input id="person" value={form.person ?? ''} onChange={update('person')} maxLength={100} />
              </Field>
              <Field label="Event" htmlFor="eventType">
                <Select id="eventType" value={form.eventType ?? ''} onChange={update('eventType')}>
                  <option value="">Any event</option>
                  {EVENT_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
                </Select>
              </Field>
              <Field label="From" htmlFor="from">
                <Input id="from" type="date" value={form.from ?? ''} onChange={update('from')} />
              </Field>
              <Field label="To" htmlFor="to">
                <Input id="to" type="date" value={form.to ?? ''} onChange={update('to')} />
              </Field>
              <Field label="Entries" htmlFor="kind">
                <Select id="kind" value={form.kind ?? 'EVENT'} onChange={update('kind')}>
                  <option value="EVENT">Business events</option>
                  <option value="ACCESS">Who read the trail</option>
                </Select>
              </Field>
              <div className="md:col-span-3 xl:col-span-6 flex justify-end">
                <Button type="submit" disabled={loading}><Search className="size-4" aria-hidden="true" /> Search</Button>
              </div>
            </form>
          </CardContent>
        </Card>

        {integrity && (
          integrity.intact
            ? <Alert tone="success" title="The trail is intact">All {integrity.entriesChecked.toLocaleString('en-AU')} entries verified at {formatTime(integrity.verifiedAt)}: no entry was changed, removed, inserted or reordered.</Alert>
            : <Alert tone="danger" title="The trail has been tampered with"><ShieldAlert className="inline size-4" aria-hidden="true" /> The chain breaks at entry {integrity.brokenAtSequence}: {integrity.problem}. Escalate to security.</Alert>
        )}

        {error && <Alert tone="danger">{error}</Alert>}

        <Card>
          <CardHeader
            icon={<ScrollText />}
            title={query.kind === 'ACCESS' ? 'Who read the trail' : 'Entries'}
            description={page ? `${page.totalCount.toLocaleString('en-AU')} found, newest first` : 'Loading…'}
          />
          {page && page.items.length === 0 ? (
            <EmptyState icon={<Search />} title="Nothing found">Change the filters and search again.</EmptyState>
          ) : (
            <Table>
              <thead>
                <tr>
                  <Th>#</Th><Th>When</Th><Th>Event</Th><Th>Record</Th><Th>Initiated by</Th><Th>Decided by</Th><Th>Status</Th><Th className="text-right">Amount</Th>
                </tr>
              </thead>
              <tbody>
                {page?.items.map((e) => (
                  <tr key={e.sequence}>
                    <Td className="font-mono text-xs text-ink-faint">{e.sequence}</Td>
                    <Td className="whitespace-nowrap">{formatTime(e.occurredAt)}</Td>
                    <Td><span className="font-medium">{e.eventType}</span>{e.source && <span className="block text-xs text-ink-faint">{e.source}</span>}</Td>
                    <Td>
                      {e.recordRef && e.kind === 'EVENT'
                        ? <Link className="font-medium text-brand-700 hover:underline" href={`/v1/audit/records/view-details?recordRef=${encodeURIComponent(e.recordRef)}`}>{e.recordRef}</Link>
                        : e.recordRef}
                      {e.customerNumber && e.customerNumber !== e.recordRef && <span className="block text-xs text-ink-faint">{e.customerNumber}</span>}
                    </Td>
                    <Td>{personLabel(e.initiatedBy)}</Td>
                    <Td>{e.kind === 'ACCESS' ? <Badge tone="info">{personLabel(e.actor)}</Badge> : personLabel(e.actor)}</Td>
                    <Td className="text-xs">{e.status}{e.reasonCode && <span className="block text-ink-faint">{e.reasonCode}</span>}</Td>
                    <Td className="whitespace-nowrap text-right">{formatAmount(e.amount, e.currency)}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          )}
          {page && pages > 1 && (
            <CardContent className="flex items-center justify-end gap-3">
              <Button variant="secondary" size="sm" disabled={page.pageNumber <= 1 || loading} onClick={() => setQuery((q) => ({ ...q, pageNumber: page.pageNumber - 1 }))}>Newer</Button>
              <span className="text-xs text-ink-muted">Page {page.pageNumber} of {pages}</span>
              <Button variant="secondary" size="sm" disabled={page.pageNumber >= pages || loading} onClick={() => setQuery((q) => ({ ...q, pageNumber: page.pageNumber + 1 }))}>Older</Button>
            </CardContent>
          )}
        </Card>
      </div>
    </MfeShell>
  );
}

/** A date picked in the browser (local day) as the UTC instant the API expects; "to" is the end of that day. */
function toUtc(day: string | undefined, endOfDay = false): string | undefined {
  if (!day) return undefined;
  const date = new Date(`${day}T00:00:00`);
  if (endOfDay) date.setDate(date.getDate() + 1);
  return date.toISOString();
}