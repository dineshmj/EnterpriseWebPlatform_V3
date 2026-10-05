'use client';

import { ArrowLeft, FileSearch, IdCard, Landmark } from 'lucide-react';
import Link from 'next/link';
import { useEffect, useState } from 'react';
import { MfeShell, publishSelection } from '../../../../components/MfeShell';
import { buttonVariants } from '../../../../components/ui/button';
import { Card, CardContent, CardHeader } from '../../../../components/ui/card';
import { cn } from '../../../../components/ui/cn';
import { DescriptionList, formatDateTime } from '../../../../components/ui/data';
import { Alert, Skeleton, StatusBadge, statusLabel } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';
import { type ApplicantAddress, formatAddress } from '../../../../lib/applicant';

interface KycCaseDetail {
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
  identityVerificationStatus?: string;
  identityVerificationByUserId?: string | null;
  identityVerificationAt?: string | null;
  identityVerificationRemarks?: string | null;
  documentVerificationStatus?: string;
  documentVerificationByUserId?: string | null;
  documentVerificationAt?: string | null;
  documentVerificationRemarks?: string | null;
  initiatedByUserId?: string | null;
  decisionAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

const shortId = (id: string | null | undefined) => (id ? `${id.slice(0, 8)}…` : '—');

export default function KycCaseDetailsPage() {
  const [caseId, setCaseId] = useState<number | null>(null);
  const [data, setData] = useState<KycCaseDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const raw = new URLSearchParams(window.location.search).get('caseId');
    const parsed = Number(raw);

    if (!Number.isSafeInteger(parsed) || parsed <= 0) {
      setError('A valid KYC Case ID was not supplied.');
      return;
    }

    setCaseId(parsed);
    getJson<KycCaseDetail>(`/bff/api/kyc/cases/${parsed}`)
      .then(detail => {
        setData(detail);
        publishSelection(
          [{ title: 'Customer Name', value: detail.customerName }, { title: 'Customer Number', value: detail.customerNumber }],
          [
            { title: 'Application Number', value: detail.applicationNumber ?? '—' },
            { title: 'KYC Case', value: `#${detail.kycCaseId}` },
            { title: 'KYC Status', value: statusLabel(detail.status) },
          ],
        );
      })
      .catch(error => setError(error instanceof Error ? error.message : 'Unable to load the KYC case.'));
  }, []);

  return (
    <MfeShell title={data?.applicationNumber ? `KYC case · ${data.applicationNumber}` : `KYC case ${caseId ?? ''}`} subtitle="Customer KYC">
      <div className="mb-6">
        <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/kyc/cases/view-all/">
          <ArrowLeft aria-hidden="true" />Back to work queue
        </Link>
      </div>

      {error && <Alert tone="danger">{error}</Alert>}
      {!data && !error && <Skeleton className="h-64" />}

      {data && (
        <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_380px]">
          <Card>
            <CardHeader icon={<FileSearch />} title="Case details" description="Authoritative information from the Customer KYC API." actions={<StatusBadge status={data.status} />} />
            <CardContent>
              <DescriptionList
                columns={3}
                items={[
                  { label: 'Application', value: <span className="font-mono text-[13px]">{data.applicationNumber ?? '—'}</span> },
                  { label: 'Applicant (as submitted)', value: <span className="font-medium">{data.customerName}</span> },
                  { label: 'Customer', value: <span className="font-mono text-[13px]">{data.customerNumber}</span> },
                  { label: 'Residential address', value: formatAddress(data.residentialAddress) },
                  { label: 'Branch', value: data.branchCode ?? '—' },
                  { label: 'KYC case', value: `#${data.kycCaseId}` },
                  { label: 'Assigned officer', value: data.assignedOfficerUserId ? <span className="font-mono text-[13px]">{data.staff?.assignedOfficer ?? shortId(data.assignedOfficerUserId)}</span> : 'Unassigned' },
                  { label: 'Initiated by', value: <span className="font-mono text-[13px]">{data.staff?.initiatedBy ?? shortId(data.initiatedByUserId)}</span> },
                  { label: 'Opened', value: formatDateTime(data.createdAt) },
                  { label: 'Last updated', value: formatDateTime(data.updatedAt) },
                  { label: 'Decided', value: formatDateTime(data.decisionAt) },
                ]}
              />
            </CardContent>
          </Card>

          <div className="flex flex-col gap-4">
            <StageCard
              icon={<IdCard />}
              title="Identity verification"
              status={data.identityVerificationStatus}
              decidedBy={data.staff?.identityVerificationBy ?? data.identityVerificationByUserId}
              decidedAt={data.identityVerificationAt}
              remarks={data.identityVerificationRemarks}
              href={`/v1/kyc/identity-verification/view-all?caseId=${data.kycCaseId}`}
            />
            <StageCard
              icon={<Landmark />}
              title="Document verification"
              status={data.documentVerificationStatus}
              decidedBy={data.staff?.documentVerificationBy ?? data.documentVerificationByUserId}
              decidedAt={data.documentVerificationAt}
              remarks={data.documentVerificationRemarks}
              href={`/v1/kyc/documents/view-all?caseId=${data.kycCaseId}`}
            />
          </div>
        </div>
      )}
    </MfeShell>
  );
}

function StageCard({
  icon, title, status, decidedBy, decidedAt, remarks, href,
}: {
  icon: React.ReactNode;
  title: string;
  status?: string;
  decidedBy?: string | null;
  decidedAt?: string | null;
  remarks?: string | null;
  href: string;
}) {
  const pending = (status ?? '').toUpperCase() === 'PENDING_REVIEW';
  return (
    <Card>
      <CardHeader icon={icon} title={title} actions={<StatusBadge status={status} />} />
      <CardContent className="space-y-3">
        {pending ? (
          <Link className={cn(buttonVariants({ variant: 'accent', size: 'sm' }), 'w-full')} href={href}>Review evidence</Link>
        ) : (
          <>
            <p className="text-xs text-ink-muted">
              Decided by <span className="font-mono">{decidedBy && decidedBy.length <= 20 ? decidedBy : shortId(decidedBy)}</span> · {formatDateTime(decidedAt)}
            </p>
            {remarks && <p className="rounded-control bg-subtle px-3 py-2 text-[13px] text-ink">{remarks}</p>}
          </>
        )}
      </CardContent>
    </Card>
  );
}