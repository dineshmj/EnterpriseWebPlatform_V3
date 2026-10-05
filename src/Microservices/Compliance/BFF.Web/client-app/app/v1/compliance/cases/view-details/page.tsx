'use client';

import { ArrowLeft, FileSearch, Gavel, Radar, Users } from 'lucide-react';
import Link from 'next/link';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, publishSelection } from '../../../../components/MfeShell';
import { RiskBadge } from '../../../../components/RiskBadge';
import { Button, buttonVariants } from '../../../../components/ui/button';
import { Card, CardContent, CardHeader } from '../../../../components/ui/card';
import { ConfirmDialog } from '../../../../components/ui/confirm-dialog';
import { DescriptionList, formatDateTime } from '../../../../components/ui/data';
import { Alert, Skeleton, StatusBadge, statusLabel } from '../../../../components/ui/feedback';
import { Field, Textarea } from '../../../../components/ui/form';
import { getJson, postJson } from '../../../../lib/api';
import {
  type ComplianceCase, type Officer, formatAddress, getOfficer, isDecided, separationOfDutiesConflict, shortId,
} from '../../../../lib/compliance';

type Action = 'claim' | 'release' | 'approve' | 'reject' | 'hold' | 'release-hold';

const DONE: Record<Action, string> = {
  claim: 'The case is now assigned to you.',
  release: 'The case is back in the shared queue.',
  approve: 'The case is approved. Customer Onboarding will record the outcome.',
  reject: 'The case is rejected. Customer Onboarding will record the outcome.',
  hold: 'The case is on hold.',
  'release-hold': 'The hold is released; the case is under review again.',
};

export default function ComplianceCaseDetailsPage() {
  const [caseId, setCaseId] = useState<number | null>(null);
  const [data, setData] = useState<ComplianceCase | null>(null);
  const [officer, setOfficer] = useState<Officer | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [confirm, setConfirm] = useState<'approve' | 'reject' | null>(null);

  const load = useCallback((id: number) =>
    getJson<ComplianceCase>(`/bff/api/compliance/cases/${id}`)
      .then(detail => {
        setData(detail);
        publishSelection(
          [{ title: 'Customer Name', value: detail.customerName }, { title: 'Customer Number', value: detail.customerNumber }],
          [
            { title: 'Application Number', value: detail.applicationNumber },
            { title: 'Compliance Case', value: `#${detail.complianceCaseId}` },
            { title: 'Compliance Status', value: statusLabel(detail.status) },
          ],
        );
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load the compliance case.')), []);

  useEffect(() => {
    const parsed = Number(new URLSearchParams(window.location.search).get('caseId'));
    if (!Number.isSafeInteger(parsed) || parsed <= 0) {
      setError('A valid Compliance Case ID was not supplied.');
      return;
    }
    setCaseId(parsed);
    load(parsed);
    getOfficer().then(setOfficer).catch(() => setOfficer(null));
  }, [load]);

  const act = async (action: Action) => {
    if (!data) return;
    setConfirm(null);
    setBusy(true);
    setActionError(null);
    setNotice(null);
    try {
      await postJson(`/bff/api/compliance/cases/${data.complianceCaseId}/${action}`, { text: text.trim() || null });
      setNotice(DONE[action]);
      setText('');
      await load(data.complianceCaseId);
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
  const mayAct = !!data && !sod && !assignedToOther && !isDecided(data.status) && data.status !== 'SCREENING';
  const lacksClearance = !!data?.requiredClearance && !!officer?.clearance && officer.clearance < data.requiredClearance;
  // People are shown by LAN ID (records and rules keep the subject ID); "You" for the signed-in officer.
  const person = (id?: string | null, lanId?: string | null) => (!id ? '—' : id === me ? 'You' : <span className="font-mono text-[13px]">{lanId ?? shortId(id)}</span>);

  return (
    <MfeShell title={data ? `Compliance case · ${data.applicationNumber}` : `Compliance case ${caseId ?? ''}`} subtitle="Compliance">
      <div className="mb-6">
        <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/compliance/view-all/">
          <ArrowLeft aria-hidden="true" />Back to work queue
        </Link>
      </div>

      {error && <Alert tone="danger">{error}</Alert>}
      {!data && !error && <Skeleton className="h-64" />}

      {data && (
        <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_420px]">
          <div className="flex flex-col gap-6">
            <Card>
              <CardHeader icon={<FileSearch />} title="Case details" description="Authoritative information from the Compliance API." actions={<StatusBadge status={data.status} />} />
              <CardContent>
                <DescriptionList
                  columns={3}
                  items={[
                    { label: 'Application', value: <span className="font-mono text-[13px]">{data.applicationNumber}</span> },
                    { label: 'Applicant (screened)', value: <span className="font-medium">{data.customerName}</span> },
                    { label: 'Customer', value: <span className="font-mono text-[13px]">{data.customerNumber}</span> },
                    { label: 'Residential address', value: formatAddress(data.residentialAddress) },
                    { label: 'Branch', value: data.branchCode },
                    { label: 'Compliance case', value: `#${data.complianceCaseId}` },
                    { label: 'KYC case', value: `#${data.kycCaseId}` },
                    { label: 'Assigned officer', value: data.assignedOfficerUserId ? person(data.assignedOfficerUserId, data.staff?.assignedOfficer) : 'Unassigned' },
                    { label: 'Opened', value: formatDateTime(data.createdAt) },
                    { label: 'Last updated', value: formatDateTime(data.updatedAt) },
                    { label: 'Decided', value: formatDateTime(data.decisionAt) },
                  ]}
                />
              </CardContent>
            </Card>

            <Card>
              <CardHeader icon={<Radar />} title="Screening" description="AML / sanctions / PEP screening by the external provider." actions={<RiskBadge risk={data.riskRating} clearance={data.requiredClearance} />} />
              <CardContent className="space-y-4">
                {data.status === 'SCREENING' && (
                  <Alert tone="info" title="Waiting for the screening provider">
                    A provider failure is never treated as a pass: the case stays here and is retried automatically.
                    {data.screeningAttempts > 0 && <> Attempts so far: {data.screeningAttempts}; next attempt {formatDateTime(data.nextScreeningAt)}.</>}
                    {data.lastScreeningError && <span className="mt-1 block font-mono text-xs">{data.lastScreeningError}</span>}
                  </Alert>
                )}
                <DescriptionList
                  columns={3}
                  items={[
                    { label: 'Outcome', value: statusLabel(data.screeningOutcome) },
                    { label: 'Provider', value: data.screeningProvider ?? '—' },
                    { label: 'Provider reference', value: <span className="font-mono text-[13px]">{data.screeningReference ?? '—'}</span> },
                    { label: 'Screened', value: formatDateTime(data.screenedAt) },
                    { label: 'Attempts', value: data.screeningAttempts },
                    { label: 'Approval needs clearance', value: data.requiredClearance ?? '—' },
                  ]}
                />
              </CardContent>
            </Card>

            <Card>
              <CardHeader icon={<Users />} title="Separation of duties" description="These people may not handle this case." />
              <CardContent>
                <DescriptionList
                  columns={3}
                  items={[
                    { label: 'Onboarding initiated by', value: person(data.initiatedByUserId, data.staff?.initiatedBy) },
                    { label: 'KYC identity decided by', value: person(data.kycIdentityDecidedByUserId, data.staff?.kycIdentityDecidedBy) },
                    { label: 'KYC documents decided by', value: person(data.kycDocumentDecidedByUserId, data.staff?.kycDocumentDecidedBy) },
                  ]}
                />
              </CardContent>
            </Card>
          </div>

          <Card>
            <CardHeader icon={<Gavel />} title="Decision" description={officer?.clearance ? `Your clearance level: ${officer.clearance}` : undefined} />
            <CardContent className="space-y-4">
              {notice && <Alert tone="success">{notice}</Alert>}
              {actionError && <Alert tone="danger">{actionError}</Alert>}

              {isDecided(data.status) && (
                <div className="space-y-2">
                  <p className="text-sm text-ink">
                    {statusLabel(data.status)} by {person(data.decisionByUserId, data.staff?.decisionBy)} · {formatDateTime(data.decisionAt)}
                  </p>
                  {data.decisionRemarks && <p className="rounded-control bg-subtle px-3 py-2 text-[13px] text-ink">{data.decisionRemarks}</p>}
                </div>
              )}

              {!isDecided(data.status) && sod && <Alert tone="warning" title="Separation of duties">{sod}</Alert>}
              {!isDecided(data.status) && !sod && assignedToOther && (
                <Alert tone="info">This case is assigned to another officer ({data.staff?.assignedOfficer ?? shortId(data.assignedOfficerUserId)}). Only they can act on it until they release it.</Alert>
              )}
              {data.status === 'SCREENING' && <p className="text-sm text-ink-muted">The case can be decided once screening has completed.</p>}

              {data.status === 'ON_HOLD' && data.holdReason && (
                <Alert tone="warning" title="On hold">{data.holdReason}</Alert>
              )}

              {mayAct && (
                <>
                  {data.status === 'UNDER_REVIEW' && !data.assignedOfficerUserId && (
                    <div className="flex items-center justify-between gap-3 rounded-control bg-subtle px-3 py-2">
                      <p className="text-[13px] text-ink-muted">Unassigned. Claim it, or take any decision below to assign it to yourself.</p>
                      <Button size="sm" variant="secondary" disabled={busy} onClick={() => act('claim')}>Claim</Button>
                    </div>
                  )}

                  <Field
                    label={data.status === 'ON_HOLD' ? 'Remarks' : 'Remarks / hold reason'}
                    htmlFor="officer-text"
                    hint="Required to reject or to put the case on hold; optional to approve."
                  >
                    <Textarea id="officer-text" maxLength={4000} value={text} onChange={e => setText(e.target.value)} disabled={busy} />
                  </Field>

                  {data.status === 'UNDER_REVIEW' && lacksClearance && (
                    <Alert tone="warning">
                      This {data.riskRating?.toLowerCase()}-risk case needs clearance {data.requiredClearance} to approve; yours is {officer?.clearance}. You can still reject it or put it on hold.
                    </Alert>
                  )}

                  <div className="flex flex-wrap gap-2">
                    {data.status === 'UNDER_REVIEW' && (
                      <>
                        <Button disabled={busy || lacksClearance} onClick={() => setConfirm('approve')}>Approve</Button>
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
          title={confirm === 'approve' ? 'Approve this compliance case?' : 'Reject this compliance case?'}
          cancelLabel="Cancel"
          confirmLabel={confirm === 'approve' ? 'Approve' : 'Reject'}
          onCancel={() => setConfirm(null)}
          onConfirm={() => act(confirm)}
        >
          The decision for {data.applicationNumber} is final and is sent to Customer Onboarding.
        </ConfirmDialog>
      )}
    </MfeShell>
  );
}