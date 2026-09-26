'use client';

import { useEffect, useState } from 'react';
import { MfeShell } from '../../../../components/MfeShell';
import { CustomerOnboardingForm } from '../../../../components/CustomerOnboardingForm';
import { getJson } from '../../../../lib/api';

interface Application { applicationId: number; applicationNumber: string; customerId: number; status: string; createdAt: string; version: number; }
interface Page { items: Application[]; pageNumber: number; pageSize: number; totalCount: number; }

export default function ApplicationsPage() {
  const [data, setData] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { getJson<Page>('/bff/api/onboarding/applications?pageNumber=1&pageSize=25').then(setData).catch(e => setError(e.message)); }, []);

  return <MfeShell title="Onboarding Applications" subtitle="Onboarding Applications · Create, review and manage applications">
    <CustomerOnboardingForm />
    <div className="card">
      <h2>Recent applications</h2>
      {error && <div className="error">{error}</div>}
      {!data && !error && <div className="empty">Loading applications...</div>}
      {data && <table className="table"><thead><tr><th>Application</th><th>Customer ID</th><th>Status</th><th>Created</th><th>Version</th></tr></thead><tbody>
        {data.items.map(a => <tr key={a.applicationId}><td>{a.applicationNumber}</td><td>{a.customerId}</td><td>{a.status}</td><td>{new Date(a.createdAt).toLocaleString()}</td><td>{a.version}</td></tr>)}
      </tbody></table>}
    </div>
  </MfeShell>;
}
