 'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { MfeShell } from '../../../../components/MfeShell';
import { getJson } from '../../../../lib/api';

interface KycCaseDetail {
  kycCaseId: number;
  customerNumber: string;
  status: string;
  initiatedByUserId?: string | null;
  createdAt: string;
  updatedAt: string;
}

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
      .then(setData)
      .catch(error => setError(error instanceof Error ? error.message : 'Unable to load the KYC case.'));
  }, []);

  return (
    <MfeShell>
      <section className="card">
        <div className="header compact-header">
          <div>
            <h2>KYC Case Details</h2>
            <div className="hint">Authoritative case information from the Customer KYC API.</div>
          </div>
          <span className="badge">CASE {caseId ?? '—'}</span>
        </div>

        {error && <div className="error">{error}</div>}
        {!data && !error && <div className="empty">Loading KYC case...</div>}

        {data && (
          <div className="detail-grid">
            <div className="detail-item"><span>KYC Case ID</span><strong>{data.kycCaseId}</strong></div>
            <div className="detail-item"><span>Customer Number</span><strong>{data.customerNumber}</strong></div>
            <div className="detail-item"><span>Status</span><strong><span className="status">{data.status}</span></strong></div>
            <div className="detail-item"><span>Initiated By User ID</span><strong>{data.initiatedByUserId ?? '—'}</strong></div>
            <div className="detail-item"><span>Created</span><strong>{new Date(data.createdAt).toLocaleString()}</strong></div>
            <div className="detail-item"><span>Last Updated</span><strong>{new Date(data.updatedAt).toLocaleString()}</strong></div>
          </div>
        )}

        <div className="detail-actions">
          <Link className="secondary-button" href="/v1/kyc/cases/view-all/">Back to KYC Cases</Link>
        </div>
      </section>
    </MfeShell>
  );
}
