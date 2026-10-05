'use client';

import { ArrowLeft, Building2, FileSearch, Gavel, Users } from 'lucide-react';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, publishSelection } from '../../../../components/MfeShell';
import { Button, buttonVariants } from '../../../../components/ui/button';
import { Card, CardContent, CardHeader } from '../../../../components/ui/card';
import { ConfirmDialog } from '../../../../components/ui/confirm-dialog';
import { DescriptionList, formatDateTime } from '../../../../components/ui/data';
import { Alert, Skeleton, StatusBadge, statusLabel } from '../../../../components/ui/feedback';
import { Field, Select, Textarea } from '../../../../components/ui/form';
import { getJson, postJson } from '../../../../lib/api';
import {
  type AccountApplication, type Officer, PRODUCTS, getOfficer, isDecided, productLabel, separationOfDutiesConflict, shortId,
} from '../../../../lib/accounts';

type Action = 'claim' | 'release' | 'approve' | 'reject' | 'hold' | 'release-hold';

const DONE: Record<Action, string> = {
  claim: 'The application is now assigned to you.',
  release: 'The application is back in the shared queue.',
  approve: 'Approved. The core-banking system is opening the account; this page updates when it is open.',
  reject: 'The application is rejected. Customer Onboarding will record the outcome.',
  hold: 'The application is on hold.',
  'release-hold': 'The hold is released; the application awaits a decision again.',
};

export default function AccountApplicationDetailsPage() {
  const [applicationId, setApplicationId] = useState<number | null>(null);
  const [data, setData] = useState<AccountApplication | null>(null);
  const [officer, setOfficer] = useState<Officer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [product, setProduct] = useState<string>(PRODUCTS[0].code);
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<'approve' | 'reject' | null>(null);

  const load = useCallback((id: number) =>
    getJson<AccountApplication>(`/bff/api/accounts/applications/${id}`)
      .then(detail => {
        setData(detail);
        publishSelection(
          [{ title: 'Customer Number', value: detail.customerNumber }],
          [
            { title: 'Application Number', value: detail.applicationNumber },
            { title: 'Account Application', value: `#${detail.accountApplicationId}` },
            { title: 'Account Status', value: statusLabel(detail.status) },
          ],
        );
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load the account application.')), []);

  useEffect(() => {
    const parsed = Number(new URLSearchParams(window.location.search).get('applicationId'));
    if (!Number.isSafeInteger(parsed) || parsed <= 0) {
      setError('A valid Account Application ID was not supplied.');
      return;
    }
    setApplicationId(parsed);
    load(parsed);
    getOfficer().then(setOfficer).catch(() => setOfficer(null));
  }, [load]);

  // While core banking is opening the account, refresh every few seconds until it is decided.
  useEffect(() => {
    if (!data || data.status !== 'OPENING') return;
    const timer = window.setInterval(() => load(data.accountApplicationId), 4000);
    return () => window.clearInterval(timer);
  }, [data, load]);

  const act = async (action: Action) => {
    if (!data) return;
    setConfirm(null);
    setBusy(true);
    setActionError(null);
    setNotice(null);
    try {
      await postJson(`/bff/api/accounts/applications/${data.accountApplicationId}/${action}`,
        { text: text.trim() || null, product: action === 'approve' ? product : null });
      setNotice(DONE[action]);
      setText('');
      await load(data.accountApplicationId);
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'The action failed.');
    } finally {
      setBusy(false);
    }
  };

  const me = officer?.sub ?? '';
  const sod = data && me ? separationOfDutiesConflict(data, me) : null;
  const assignedToMe = !!data && !!me && data.assignedOfficerUserId === me;
  const assignedToOther = !!data && !!data.assignedOfficerUserId && !assignedToMe;
  const mayAct = !!data && !sod && !assignedToOther && !isDecided(data.status);
  const person = (id?: string | null) => (!id ? '—' : id === me ? 'You' : <span className="font-mono text-[13px]">{shortId(id)}</span>);

  return (
    <MfeShell title={data ? `Account application · ${data.applicationNumber}` : `Account application ${applicationId ?? ''}`} subtitle="Accounts">
      <div className="mb-6">
        <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/accounts/applications/view-all/">
          <ArrowLeft aria-hidden="true" />Back to account applications
        </Link>
      </div>

      {error && <Alert tone="danger">{error}</Alert>}
      {!data && !error && <Skeleton className="h-64" />}

      {data && (
        <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_420px]">
          <div className="flex flex-col gap-6">
            <Card>
              <CardHeader icon={<FileSearch />} title="Application details" description="Authoritative information from the Accounts API." actions={<StatusBadge status={data.status} />} />
              <CardContent>
                <DescriptionList
                  columns={3}
                  items={[
                    { label: 'Application', value: <span className="font-mono text-[13px]">{data.applicationNumber}</span> },
                    { label: 'Customer', value: <span className="font-mono text-[13px]">{data.customerNumber}</span> },
                    { label: 'Branch', value: data.branchCode },
                    { label: 'Account application', value: `#${data.accountApplicationId}` },
                    { label: 'Compliance case', value: `#${data.complianceCaseId}` },
                    { label: 'Assigned officer', value: data.assignedOfficerUserId ? person(data.assignedOfficerUserId) : 'Unassigned' },
                    { label: 'Received', value: formatDateTime(data.createdAt) },
                    { label: 'Last updated', value: formatDateTime(data.updatedAt) },
                    { label: 'Decided', value: formatDateTime(data.decisionAt) },
                  ]}
                />
              </CardContent>
            </Card>

            <Card>
              <CardHeader icon={<Building2 />} title="Account" description="Opened by the bank's core-banking system after approval." />
              <CardContent className="space-y-4">
                {data.status === 'OPENING' && (
                  <Alert tone="info" title="Opening the account">
                    The core-banking system is opening the account. A technical failure is retried automatically and never counts as opened.
                    {data.openingAttempts > 0 && <> Attempts so far: {data.openingAttempts}; next attempt {formatDateTime(data.nextOpeningAt)}.</>}
                    {data.lastOpeningError && <span className="mt-1 block font-mono text-xs">{data.lastOpeningError}</span>}
                  </Alert>
                )}
                {data.status === 'FAILED' && (
                  <Alert tone="danger" title="The account could not be opened">{data.failureReason}</Alert>
                )}
                <DescriptionList
                  columns={3}
                  items={[
                    { label: 'Product', value: productLabel(data.product) },
                    { label: 'Account number', value: <span className="font-mono text-[13px]">{data.accountNumber ?? '—'}</span> },
                    { label: 'Opened', value: formatDateTime(data.openedAt) },
                  ]}
                />
              </CardContent>
            </Card>

            <Card>
              <CardHeader icon={<Users />} title="Separation of duties" description="These people may not decide this application." />
              <CardContent>
                <DescriptionList
                  columns={2}
                  items={[
                    { label: 'Onboarding initiated by', value: person(data.initiatedByUserId) },
                    { label: 'Compliance approved by', value: person(data.complianceApprovedByUserId) },
                  ]}
                />
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader icon={<Gavel />} title="Decision" />
            <CardContent className="space-y-4">
              {notice && <Alert tone="success">{notice}</Alert>}
              {actionError && <Alert tone="danger">{actionError}</Alert>}

              {isDecided(data.status) && data.decisionByUserId && (
                <div className="space-y-2">
                  <p className="text-sm text-ink">
                    {data.status === 'REJECTED' ? 'Rejected' : `Approved (${productLabel(data.product)})`} by {person(data.decisionByUserId)} · {formatDateTime(data.decisionAt)}
                  </p>
                  {data.decisionRemarks && <p className="rounded-control bg-subtle px-3 py-2 text-[13px] text-ink">{data.decisionRemarks}</p>}
                </div>
              )}

              {!isDecided(data.status) && sod && <Alert tone="warning" title="Separation of duties">{sod}</Alert>}
              {!isDecided(data.status) && !sod && assignedToOther && (
                <Alert tone="info">This application is assigned to another officer ({shortId(data.assignedOfficerUserId)}). Only they can act on it until they release it.</Alert>
              )}

              {data.status === 'ON_HOLD' && data.holdReason && (
                <Alert tone="warning" title="On hold">{data.holdReason}</Alert>
              )}

              {mayAct && (
                <>
                  {data.status === 'PENDING_REVIEW' && !data.assignedOfficerUserId && (
                    <div className="flex items-center justify-between gap-3 rounded-control bg-subtle px-3 py-2">
                      <p className="text-[13px] text-ink-muted">Unassigned. Claim it, or take any decision below to assign it to yourself.</p>
                      <Button size="sm" variant="secondary" disabled={busy} onClick={() => act('claim')}>Claim</Button>
                    </div>
                  )}

                  {data.status === 'PENDING_REVIEW' && (
                    <Field label="Product" htmlFor="product" hint="The account the core-banking system will open.">
                      <Select id="product" value={product} onChange={e => setProduct(e.target.value)} disabled={busy}>
                        {PRODUCTS.map(p => <option key={p.code} value={p.code}>{p.label}</option>)}
                      </Select>
                    </Field>
                  )}

                  <Field
                    label={data.status === 'ON_HOLD' ? 'Remarks' : 'Remarks / hold reason'}
                    htmlFor="officer-text"
                    hint="Required to reject or to put the application on hold; optional to approve."
                  >
                    <Textarea id="officer-text" maxLength={4000} value={text} onChange={e => setText(e.target.value)} disabled={busy} />
                  </Field>

                  <div className="flex flex-wrap gap-2">
                    {data.status === 'PENDING_REVIEW' && (
                      <>
                        <Button disabled={busy} onClick={() => setConfirm('approve')}>Approve and open account</Button>
                        <Button variant="danger-outline" disabled={busy || !text.trim()} onClick={() => setConfirm('reject')}>Reject</Button>
                        <Button variant="secondary" disabled={busy || !text.trim()} onClick={() => act('hold')}>Put on hold</Button>
                        {assignedToMe && <Button variant="ghost" disabled={busy} onClick={() => act('release')}>Release to queue</Button>}
                      </>
                    )}
                    {data.status === 'ON_HOLD' && assignedToMe && (
                      <>
                        <Button variant="secondary" disabled={busy} onClick={() => act('release-hold')}>Release hold</Button>
                        <Button variant="danger-outline" disabled={busy || !text.trim()} onClick={() => setConfirm('reject')}>Reject</Button>
                      </>
                    )}
                  </div>
                </>
              )}
            </CardContent>
          </Card>
        </div>
      )}

      {confirm && data && (
        <ConfirmDialog
          title={confirm === 'approve' ? 'Approve and open this account?' : 'Reject this account application?'}
          cancelLabel="Cancel"
          confirmLabel={confirm === 'approve' ? 'Approve' : 'Reject'}
          onCancel={() => setConfirm(null)}
          onConfirm={() => act(confirm)}
        >
          {confirm === 'approve'
            ? <>The core-banking system will open a {productLabel(product).toLowerCase()} for {data.customerNumber}. The decision is final.</>
            : <>The decision for {data.applicationNumber} is final and is sent to Customer Onboarding.</>}
        </ConfirmDialog>
      )}
    </MfeShell>
  );
}