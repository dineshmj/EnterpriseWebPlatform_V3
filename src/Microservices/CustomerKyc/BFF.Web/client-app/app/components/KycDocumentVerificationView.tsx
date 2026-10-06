'use client';

import { ArrowLeft, ArrowRight, CheckCircle2, ExternalLink, FileText, Inbox, ThumbsDown, ThumbsUp, UserRound } from 'lucide-react';
import Link from 'next/link';
import { useEffect, useRef, useState } from 'react';
import { MfeShell, getWorkspaceContext, publishSelection, type WorkspaceContext } from './MfeShell';
import { getJson, postJson } from '../lib/api';
import { type ApplicantAddress, formatAddress } from '../lib/applicant';
import { Button, buttonVariants } from './ui/button';
import { Card, CardContent, CardHeader } from './ui/card';
import { cn } from './ui/cn';
import { DescriptionList, formatDateTime } from './ui/data';
import { Alert, Badge, EmptyState, Skeleton, StatusBadge, statusLabel } from './ui/feedback';
import { Textarea } from './ui/form';

interface KycCase {
  kycCaseId: number;
  customerNumber: string;
  customerName: string;
  residentialAddress?: ApplicantAddress | null;
  /** LAN IDs of the people on the case, for display (rules use the subject IDs). */
  staff?: {
    initiatedBy?: string | null;
    assignedOfficer?: string | null;
    identityVerificationBy?: string | null;
    documentVerificationBy?: string | null;
    decisionBy?: string | null;
  };
  applicationNumber?: string;
  branchCode?: string;
  assignedOfficerUserId?: string | null;
  status: string;
  identityVerificationStatus: string;
  documentVerificationStatus: string;
  initiatedByUserId?: string | null;
  createdAt: string;
  updatedAt: string;
}

interface PageResult {
  items: KycCase[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

interface DocumentInfo {
  documentId: string;
  fileName: string;
  contentType: string;
  size: number;
  createdAt: string;
  documentType?: string | null;
  businessReference?: string | null;
  selectionReason: string;
}

interface DecisionResponse {
  kycCaseId?: number;
  stageStatus?: string;
  overallStatus?: string;
  decisionRemarks?: string | null;
}

interface Props {
  title: string;
  subtitle: string;
  documentLabel: string;
  documentRoute: 'identity-proof' | 'tax-proof';
  verificationStage: 'IdentityVerification' | 'DocumentVerification';
}

const REMARKS_LIMIT = 4000;

/** Where the reviewer can go next once a stage decision is recorded. */
interface LastDecision {
  caseId: number;
  otherStagePending: boolean;
}

const caseDetailsHref = (caseId: number) => `/v1/kyc/cases/view-details?caseId=${caseId}`;

/**
 * The application the user was last working on, from the workspace context the
 * Shell handed over (current first, then retained). A hint only: the case list
 * itself comes from the KYC BFF.
 */
function contextApplicationNumber(context: WorkspaceContext): string | null {
  const item = [...context.currentContext, ...context.retainedContext]
    .find(x => x.title.toLowerCase() === 'application number');
  return item ? String(item.value) : null;
}

function shortId(id: string | null | undefined) {
  return id ? `${id.slice(0, 8)}…` : '—';
}

const sameUser = (a: string | null | undefined, b: string | null | undefined) =>
  !!a && !!b && a.toLowerCase() === b.toLowerCase();

/**
 * Why the signed-in officer may not decide this case, or null. The KYC API enforces
 * both rules; the screen only explains them before the officer tries.
 */
function decisionBlockedReason(item: KycCase, me: string | null): string | null {
  if (!me) return null;
  if (sameUser(item.initiatedByUserId, me))
    return 'You initiated this onboarding. Separation of duties requires another officer to decide it.';
  if (item.assignedOfficerUserId && !sameUser(item.assignedOfficerUserId, me))
    return `This case is assigned to another officer (${item.staff?.assignedOfficer ?? shortId(item.assignedOfficerUserId)}). Only they can decide it until they release it.`;
  return null;
}

export function KycDocumentVerificationView({
  title,
  subtitle,
  documentLabel,
  documentRoute,
  verificationStage,
}: Props) {
  const [cases, setCases] = useState<KycCase[]>([]);
  const [selectedCaseId, setSelectedCaseId] = useState<number | null>(null);
  const [document, setDocument] = useState<DocumentInfo | null>(null);
  const [decisionRemarks, setDecisionRemarks] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [decisionMessage, setDecisionMessage] = useState<string | null>(null);
  const [lastDecision, setLastDecision] = useState<LastDecision | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingDocument, setLoadingDocument] = useState(false);
  const [submittingDecision, setSubmittingDecision] = useState<'approve' | 'reject' | null>(null);
  const [me, setMe] = useState<string | null>(null);
  // A link named a case that has nothing awaiting this review (decided, or not in the branch).
  const [deepLinkNotice, setDeepLinkNotice] = useState<string | null>(null);
  // How the current case was chosen; only a default choice may be replaced by a late context hand-over.
  const selectionSource = useRef<'url' | 'context' | 'default' | 'user'>('default');

  const selectedCase = cases.find(item => item.kycCaseId === selectedCaseId) ?? null;
  const stageStatus = selectedCase
    ? (verificationStage === 'IdentityVerification'
      ? selectedCase.identityVerificationStatus
      : selectedCase.documentVerificationStatus)
    : null;
  const awaitingDecision = stageStatus === 'PENDING_REVIEW';
  const blockedReason = selectedCase ? decisionBlockedReason(selectedCase, me) : null;
  const canDecide = awaitingDecision && blockedReason === null;
  const stageName = verificationStage === 'IdentityVerification' ? 'Identity verification' : 'Document verification';
  const otherStage = verificationStage === 'IdentityVerification'
    ? { label: 'Review tax proof', path: '/v1/kyc/documents/view-all' }
    : { label: 'Review identity proof', path: '/v1/kyc/identity-verification/view-all' };

  useEffect(() => {
    getJson<{ sub: string }>('/api/auth/user')
      .then(user => setMe(user.sub))
      .catch(() => setMe(null));
  }, []);

  useEffect(() => {
    getJson<PageResult>(
      `/bff/api/kyc/cases?pageNumber=1&pageSize=25&status=PENDING_REVIEW&stage=${encodeURIComponent(verificationStage)}`,
    )
      .then(result => {
        setCases(result.items);
        const rawCaseId = new URLSearchParams(window.location.search).get('caseId');
        const requested = Number(rawCaseId);
        if (rawCaseId !== null && !result.items.some(x => x.kycCaseId === requested)) {
          // Never silently show a different case than the one the link named.
          selectionSource.current = 'user';
          setDeepLinkNotice(
            `KYC case ${rawCaseId} has nothing awaiting ${verificationStage === 'IdentityVerification' ? 'identity' : 'document'} verification: it may be decided already. Choose a case from the queue, or open the case details.`,
          );
          return;
        }
        if (result.items.length > 0) {
          const applicationNumber = contextApplicationNumber(getWorkspaceContext());
          const fromContext = result.items.find(x => applicationNumber !== null && x.applicationNumber === applicationNumber);

          if (result.items.some(x => x.kycCaseId === requested)) {
            selectionSource.current = 'url';
            setSelectedCaseId(requested);
          } else if (fromContext) {
            selectionSource.current = 'context';
            setSelectedCaseId(fromContext.kycCaseId);
          } else {
            selectionSource.current = 'default';
            setSelectedCaseId(result.items[0].kycCaseId);
          }
        }
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load KYC cases.'))
      .finally(() => setLoading(false));
  }, [verificationStage]);

  // The Shell's hand-over normally arrives before the case list, but can arrive after it.
  useEffect(() => {
    const onHandoff = (event: Event) => {
      if (selectionSource.current !== 'default') return;
      const applicationNumber = contextApplicationNumber((event as CustomEvent<WorkspaceContext>).detail ?? getWorkspaceContext());
      const match = cases.find(x => applicationNumber !== null && x.applicationNumber === applicationNumber);
      if (match) {
        selectionSource.current = 'context';
        setSelectedCaseId(match.kycCaseId);
      }
    };
    window.addEventListener('bss-context-handoff', onHandoff);
    return () => window.removeEventListener('bss-context-handoff', onHandoff);
  }, [cases]);

  // Tell the Shell which case is being reviewed, so the Application Workspace shows it.
  useEffect(() => {
    if (!selectedCase) return;
    publishSelection(
      [{ title: 'Customer Name', value: selectedCase.customerName }, { title: 'Customer Number', value: selectedCase.customerNumber }],
      [
        { title: 'Application Number', value: selectedCase.applicationNumber ?? '—' },
        { title: 'KYC Case', value: `#${selectedCase.kycCaseId}` },
        { title: 'Reviewing', value: stageName },
        { title: 'Stage Status', value: statusLabel(stageStatus) },
      ],
    );
    // Only a change of case re-publishes; status changes are published with the decision.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selectedCase?.kycCaseId]);

  useEffect(() => {
    if (selectedCaseId === null) {
      setDocument(null);
      return;
    }

    setLoadingDocument(true);
    setError(null);
    setDecisionMessage(null);
    setLastDecision(null);
    setDecisionRemarks('');

    getJson<DocumentInfo>(`/bff/api/kyc/cases/${selectedCaseId}/${documentRoute}`)
      .then(setDocument)
      .catch(e => {
        setDocument(null);
        setError(e instanceof Error ? e.message : `Unable to load ${documentLabel}.`);
      })
      .finally(() => setLoadingDocument(false));
  }, [selectedCaseId, documentRoute, documentLabel]);

  async function submitDecision(action: 'approve' | 'reject') {
    if (selectedCaseId === null || !canDecide) return;

    const remarks = decisionRemarks.trim();
    if (action === 'reject' && !remarks) {
      setError('Decision remarks are required when rejecting a KYC case.');
      return;
    }

    setError(null);
    setDecisionMessage(null);
    setSubmittingDecision(action);

    try {
      const result = await postJson<DecisionResponse>(
        `/bff/api/kyc/cases/${selectedCaseId}/${verificationStage === 'IdentityVerification' ? 'identity-verification' : 'document-verification'}/${action}`,
        { decisionRemarks: remarks },
      );

      const decidedStatus = result.stageStatus ?? (action === 'approve' ? 'APPROVED' : 'REJECTED');
      const overallStatus = result.overallStatus ?? selectedCase?.status ?? 'PENDING_REVIEW';
      const otherStageStatus = verificationStage === 'IdentityVerification'
        ? selectedCase?.documentVerificationStatus
        : selectedCase?.identityVerificationStatus;

      if (selectedCase) {
        publishSelection(
          [{ title: 'Customer Name', value: selectedCase.customerName }, { title: 'Customer Number', value: selectedCase.customerNumber }],
          [
            { title: 'Application Number', value: selectedCase.applicationNumber ?? '—' },
            { title: 'KYC Case', value: `#${selectedCase.kycCaseId}` },
            { title: stageName, value: statusLabel(decidedStatus) },
            { title: 'KYC Status', value: statusLabel(overallStatus) },
          ],
        );
      }

      setCases(previous => previous.filter(item => item.kycCaseId !== selectedCaseId));
      setSelectedCaseId(null);
      setDocument(null);
      setDecisionRemarks('');
      setDecisionMessage(
        `${stageName} for KYC case ${selectedCaseId} was ${decidedStatus.toLowerCase()}. Overall KYC status: ${overallStatus.toLowerCase().replace(/_/g, ' ')}.`,
      );
      setLastDecision({
        caseId: selectedCaseId,
        otherStagePending: overallStatus === 'PENDING_REVIEW' && otherStageStatus === 'PENDING_REVIEW',
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : `Unable to ${action} the KYC case.`);
    } finally {
      setSubmittingDecision(null);
    }
  }

  return (
    <MfeShell title={title} subtitle="Customer KYC · Review" wide>
      <div className="mb-4">
        <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/kyc/cases/view-all/">
          <ArrowLeft aria-hidden="true" />Back to work queue
        </Link>
      </div>

      {decisionMessage && (
        <Alert tone="success" title="Decision recorded" className="mb-6">
          {decisionMessage}
          {lastDecision && (
            <div className="mt-3 flex flex-wrap gap-2">
              {lastDecision.otherStagePending && (
                <Link className={buttonVariants({ variant: 'accent', size: 'sm' })} href={`${otherStage.path}?caseId=${lastDecision.caseId}`}>
                  {otherStage.label}<ArrowRight aria-hidden="true" />
                </Link>
              )}
              <Link className={buttonVariants({ variant: 'secondary', size: 'sm' })} href={caseDetailsHref(lastDecision.caseId)}>
                Back to case
              </Link>
            </div>
          )}
        </Alert>
      )}

      {deepLinkNotice && <Alert tone="info" className="mb-6">{deepLinkNotice}</Alert>}

      {/* Wide (xl): queue | evidence | decision. Medium (lg): queue on top, then evidence |
          decision, with the decision panel kept in view. Narrow: one column. */}
      <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_340px] xl:grid-cols-[300px_minmax(0,1fr)_380px]">
        {/* 1. Work queue */}
        <Card className="lg:col-span-2 xl:col-span-1 xl:sticky xl:top-6">
          <CardHeader
            icon={<Inbox />}
            title="Awaiting review"
            description={loading ? 'Loading…' : `${cases.length} case${cases.length === 1 ? '' : 's'} in your branch`}
          />
          {loading && <div className="space-y-3 p-4">{[0, 1, 2].map(i => <Skeleton key={i} className="h-16" />)}</div>}
          {!loading && cases.length === 0 && (
            <EmptyState icon={<CheckCircle2 />} title="Queue is clear">No cases are awaiting {stageName.toLowerCase()}.</EmptyState>
          )}
          {cases.length > 0 && (
            <ul className="max-h-56 divide-y divide-line overflow-y-auto xl:max-h-[calc(100vh-14rem)]" role="listbox" aria-label="KYC cases awaiting review">
              {cases.map(item => {
                const active = item.kycCaseId === selectedCaseId;
                return (
                  <li key={item.kycCaseId} role="option" aria-selected={active}>
                    <button
                      type="button"
                      onClick={() => { selectionSource.current = 'user'; setSelectedCaseId(item.kycCaseId); }}
                      className={cn(
                        'flex w-full flex-col gap-1 border-l-[3px] px-4 py-3 text-left transition-colors',
                        active ? 'border-accent-500 bg-accent-50/70' : 'border-transparent hover:bg-subtle',
                      )}
                    >
                      <span className="flex items-center justify-between gap-2">
                        <span className="font-mono text-[13px] font-semibold text-ink">{item.applicationNumber ?? `Case ${item.kycCaseId}`}</span>
                        <span className="text-xs text-ink-faint">#{item.kycCaseId}</span>
                      </span>
                      <span className="text-xs text-ink-muted">{item.customerName} · {item.customerNumber} · {formatDateTime(item.createdAt)}</span>
                      {item.assignedOfficerUserId && (
                        <span className="text-xs text-info-700">Assigned · {item.staff?.assignedOfficer ?? shortId(item.assignedOfficerUserId)}</span>
                      )}
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </Card>

        {/* 2. Evidence */}
        <Card className="min-w-0 overflow-hidden">
          <CardHeader
            icon={<FileText />}
            title={documentLabel}
            description={document ? `${document.fileName} · ${(document.size / 1024).toFixed(0)} KB · uploaded ${formatDateTime(document.createdAt)}` : subtitle}
            actions={selectedCaseId !== null && document && (
              <a
                className="inline-flex items-center gap-1.5 text-xs font-semibold text-accent-700 hover:underline"
                href={`/bff/api/kyc/cases/${selectedCaseId}/${documentRoute}/content`}
                target="_blank"
                rel="noopener"
              >
                Open in new tab<ExternalLink className="size-3.5" aria-hidden="true" />
              </a>
            )}
          />
          {selectedCaseId === null && !loading && (
            <EmptyState icon={<FileText />} title="No case selected">Select a case from the queue to review its evidence.</EmptyState>
          )}
          {loadingDocument && <Skeleton className="m-6 h-[55vh] min-h-[380px] lg:h-[calc(100vh-16rem)] lg:min-h-[520px]" />}
          {document && !loadingDocument && selectedCaseId !== null && (
            <iframe
              title={`${documentLabel} PDF`}
              src={`/bff/api/kyc/cases/${selectedCaseId}/${documentRoute}/content`}
              className="block h-[60vh] min-h-[420px] w-full bg-subtle lg:h-[calc(100vh-12rem)] lg:min-h-[560px]"
            />
          )}
        </Card>

        {/* 3. Decision - sticky, with its own scroll, so Approve / Reject are always reachable
            (a PDF viewer captures the mouse wheel while the pointer is over it). */}
        <div className="flex flex-col gap-6 lg:sticky lg:top-6 lg:max-h-[calc(100vh-3rem)] lg:overflow-y-auto">
          {error && <Alert tone="danger">{error}</Alert>}

          <Card>
            <CardHeader
              icon={<UserRound />}
              title="Case summary"
              actions={selectedCase && <StatusBadge status={selectedCase.status} />}
            />
            <CardContent>
              {selectedCase ? (
                <div className="space-y-5">
                  <DescriptionList
                    items={[
                      { label: 'Application', value: <span className="font-mono text-[13px]">{selectedCase.applicationNumber ?? '—'}</span> },
                      { label: 'Applicant (as submitted)', value: <span className="font-medium">{selectedCase.customerName}</span> },
                      { label: 'Residential address', value: formatAddress(selectedCase.residentialAddress) },
                      { label: 'Customer', value: <span className="font-mono text-[13px]">{selectedCase.customerNumber}</span> },
                      { label: 'Branch', value: selectedCase.branchCode ?? '—' },
                      { label: 'Opened', value: formatDateTime(selectedCase.createdAt) },
                    ]}
                  />
                  <div className="space-y-2 rounded-control border border-line bg-subtle/70 p-3">
                    <StageRow label="Identity verification" status={selectedCase.identityVerificationStatus} current={verificationStage === 'IdentityVerification'} />
                    <StageRow label="Document verification" status={selectedCase.documentVerificationStatus} current={verificationStage === 'DocumentVerification'} />
                  </div>
                  <p className="text-xs leading-5 text-ink-muted">
                    {selectedCase.assignedOfficerUserId
                      ? <>Assigned to officer <span className="font-mono">{selectedCase.staff?.assignedOfficer ?? shortId(selectedCase.assignedOfficerUserId)}</span>. Only the assigned officer can decide this case.</>
                      : <>Unassigned. Your first decision assigns this case to you.</>}
                  </p>
                  <Link
                    className="inline-flex items-center gap-1.5 text-xs font-semibold text-accent-700 hover:underline"
                    href={caseDetailsHref(selectedCase.kycCaseId)}
                  >
                    Case details<ArrowRight className="size-3.5" aria-hidden="true" />
                  </Link>
                </div>
              ) : (
                <p className="text-sm text-ink-muted">Select a case to see its details.</p>
              )}
            </CardContent>
          </Card>

          <Card>
            <CardHeader title={`${stageName} decision`} description="Approve the evidence, or reject it with a reason. The KYC API makes the final authorization decision." />
            <CardContent className="space-y-2">
              {awaitingDecision && blockedReason && (
                <Alert tone="warning" title="You cannot decide this case" className="mb-2">{blockedReason}</Alert>
              )}
              <label htmlFor="decisionRemarks" className="text-[13px] font-medium text-ink">
                Remarks <span className="font-normal text-ink-faint">(required to reject)</span>
              </label>
              <Textarea
                id="decisionRemarks"
                value={decisionRemarks}
                onChange={event => setDecisionRemarks(event.target.value)}
                maxLength={REMARKS_LIMIT}
                rows={5}
                disabled={!canDecide || submittingDecision !== null}
                placeholder="Approval notes, or the reason for rejection…"
              />
              <p className="text-right text-xs text-ink-faint">{decisionRemarks.length} / {REMARKS_LIMIT}</p>
            </CardContent>
            <div className="grid grid-cols-2 gap-3 border-t border-line px-6 py-4">
              <Button
                variant="danger-outline"
                size="lg"
                onClick={() => submitDecision('reject')}
                disabled={!canDecide || submittingDecision !== null}
              >
                <ThumbsDown aria-hidden="true" />
                {submittingDecision === 'reject' ? 'Rejecting…' : 'Reject'}
              </Button>
              <Button
                variant="accent"
                size="lg"
                onClick={() => submitDecision('approve')}
                disabled={!canDecide || submittingDecision !== null}
              >
                <ThumbsUp aria-hidden="true" />
                {submittingDecision === 'approve' ? 'Approving…' : 'Approve'}
              </Button>
            </div>
          </Card>
        </div>
      </div>
    </MfeShell>
  );
}

function StageRow({ label, status, current }: { label: string; status: string; current: boolean }) {
  return (
    <div className="flex items-center justify-between gap-3">
      <span className={cn('flex items-center gap-2 whitespace-nowrap text-[13px]', current ? 'font-semibold text-ink' : 'text-ink-muted')}>
        {current && <span className="size-1.5 rounded-full bg-accent-500" aria-hidden="true" />}
        {label}
        {current && <span className="sr-only">(this step)</span>}
      </span>
      <StatusBadge status={status} />
    </div>
  );
}