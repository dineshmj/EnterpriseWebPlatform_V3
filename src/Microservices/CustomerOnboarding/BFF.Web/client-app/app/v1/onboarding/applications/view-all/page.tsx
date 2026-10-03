'use client';

import { ClipboardList, RefreshCw } from 'lucide-react';
import { useEffect, useState } from 'react';
import { MfeShell } from '../../../../components/MfeShell';
import { CustomerOnboardingForm } from '../../../../components/CustomerOnboardingForm';
import { Button } from '../../../../components/ui/button';
import { Card, CardHeader } from '../../../../components/ui/card';
import { Table, Td, Th, formatDateTime } from '../../../../components/ui/data';
import { Alert, EmptyState, StatusBadge } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';

interface Application { applicationId: number; applicationNumber: string; customerId: number; status: string; createdAt: string; version: number; }
interface Page { items: Application[]; pageNumber: number; pageSize: number; totalCount: number; }

export default function ApplicationsPage() {
  const [data, setData] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadApplications = async () => {
    try {
      setError(null);
      const page = await getJson<Page>(
        '/bff/api/onboarding/applications?pageNumber=1&pageSize=25',
      );
      setData(page);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unable to load applications.');
    }
  };

  useEffect(() => {
    void loadApplications();
  }, []);

  return (
    <MfeShell title="New onboarding application" subtitle="Customer Onboarding">
      <div className="flex flex-col gap-8">
        <CustomerOnboardingForm onApplicationCreated={loadApplications} />

        <Card>
          <CardHeader
            icon={<ClipboardList />}
            title="Recent applications"
            description="Applications in your branch, newest first. Status follows the onboarding workflow (KYC → Compliance → Account opening)."
            actions={<Button variant="ghost" size="sm" onClick={() => void loadApplications()}><RefreshCw aria-hidden="true" />Refresh</Button>}
          />
          {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
          {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading applications…</p>}
          {data && data.items.length === 0 && (
            <EmptyState icon={<ClipboardList />} title="No applications yet">Submitted applications will appear here.</EmptyState>
          )}
          {data && data.items.length > 0 && (
            <Table>
              <thead>
                <tr>
                  <Th>Application</Th>
                  <Th>Customer ID</Th>
                  <Th>Status</Th>
                  <Th>Created</Th>
                  <Th className="text-right">Version</Th>
                </tr>
              </thead>
              <tbody>
                {data.items.map(a => (
                  <tr key={a.applicationId} className="hover:bg-subtle/70">
                    <Td className="font-mono text-[13px] font-medium">{a.applicationNumber}</Td>
                    <Td>{a.customerId}</Td>
                    <Td><StatusBadge status={a.status} /></Td>
                    <Td className="text-ink-muted">{formatDateTime(a.createdAt)}</Td>
                    <Td className="text-right text-ink-muted">{a.version}</Td>
                  </tr>
                ))}
              </tbody>
            </Table>
          )}
        </Card>
      </div>
    </MfeShell>
  );
}
