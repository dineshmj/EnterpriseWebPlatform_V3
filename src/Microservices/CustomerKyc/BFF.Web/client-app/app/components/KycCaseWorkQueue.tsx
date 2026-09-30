 'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { MfeShell } from './MfeShell';
import { getJson } from '../lib/api';

export interface KycCase {
  kycCaseId: number;
  customerNumber: string;
  status: string;
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

interface Props {
  title: string;
  subtitle: string;
  description: string;
}

export function KycCaseWorkQueue({ title, subtitle, description }: Props) {
  const [data, setData] = useState<PageResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getJson<PageResult>('/bff/api/kyc/cases?pageNumber=1&pageSize=25&status=PENDING_REVIEW')
      .then(setData)
      .catch(error => setError(error instanceof Error ? error.message : 'Unable to load KYC cases.'));
  }, []);

  return (
    <MfeShell>
      <section className="card">
        <div className="header compact-header">
          <div>
            <h2>{title}</h2>
            <div className="hint">{subtitle}</div>
          </div>
          <span className="badge">PENDING REVIEW</span>
        </div>

        <p className="hint">{description}</p>

        {error && <div className="error">{error}</div>}
        {!data && !error && <div className="empty">Loading KYC cases...</div>}

        {data && (
          data.items.length === 0 ? (
            <div className="empty">No KYC cases are currently awaiting review.</div>
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>KYC Case ID</th>
                  <th>Customer</th>
                  <th>Status</th>
                  <th>Initiated By</th>
                  <th>Created</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map(item => (
                  <tr key={item.kycCaseId}>
                    <td>
                      <Link className="record-link" href={`/v1/kyc/cases/view-details?caseId=${item.kycCaseId}`}>
                        {item.kycCaseId}
                      </Link>
                    </td>
                    <td>{item.customerNumber}</td>
                    <td><span className="status">{item.status}</span></td>
                    <td>{item.initiatedByUserId ?? '—'}</td>
                    <td>{new Date(item.createdAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )
        )}
      </section>
    </MfeShell>
  );
}
