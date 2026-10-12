'use client';

import Link from 'next/link';
import { ClipboardList, FilePlus2, RefreshCw } from 'lucide-react';
import { useCallback, useEffect, useRef, useState } from 'react';
import { MfeShell, getWorkspaceContext, publishSelection, publishWorkspaceContext, useShellNotifications } from '../../../../components/MfeShell';
import { Button, buttonVariants } from '../../../../components/ui/button';
import { Card, CardHeader } from '../../../../components/ui/card';
import { cn } from '../../../../components/ui/cn';
import { Table, Td, Th, formatDateTime } from '../../../../components/ui/data';
import { Alert, EmptyState, StatusBadge, statusLabel } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';

interface Application { applicationId: number; applicationNumber: string; customerId: number; status: string; createdAt: string; version: number; }
interface Page { items: Application[]; pageNumber: number; pageSize: number; totalCount: number; }
interface CustomerSummary { customerId: number; customerNumber: string; }

/**
 * Keeps the Application Workspace honest: when the application shown there has moved on
 * (e.g. a live notification reloaded this list), its Status chip is updated too.
 */
function syncWorkspaceStatus(items: Application[]) {
  const context = getWorkspaceContext();
  const byTitle = (title: string) => context.currentContext.find(x => x.title.toLowerCase() === title);
  const applicationId = byTitle('application id');
  const status = byTitle('status');
  if (!applicationId || !status) return;

  const application = items.find(a => a.applicationId === Number(applicationId.value));
  if (!application) return;

  const label = statusLabel(application.status);
  if (String(status.value) === label || String(status.value) === application.status) return;

  publishWorkspaceContext({
    ...context,
    currentContext: context.currentContext.map(x => (x === status ? { ...x, value: label } : x)),
  });
}

/** APP-yyyyMMdd-nnnnnn: the only shape a notification's "?applicationNumber=" may have. */
const APPLICATION_NUMBER = /^APP-\d{8}-\d{6}$/;

/** The application a notification pointed at, if the address names a well-formed one. */
function highlightedNumber(): string | null {
  const value = new URLSearchParams(window.location.search).get('applicationNumber');
  return value && APPLICATION_NUMBER.test(value) ? value : null;
}

export default function ApplicationsPage() {
  const [data, setData] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [highlight, setHighlight] = useState<string | null>(null);
  const highlightedRow = useRef<HTMLTableRowElement | null>(null);

  const loadApplications = async () => {
    try {
      setError(null);
      const page = await getJson<Page>(
        '/bff/api/onboarding/applications?pageNumber=1&pageSize=25',
      );
      setData(page);
      syncWorkspaceStatus(page.items);
      return page;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Unable to load applications.');
      return null;
    }
  };

  // Opened from a notification ("?applicationNumber=APP-..."): that application is selected,
  // shown in the Application Workspace and scrolled into view.
  useEffect(() => {
    const wanted = highlightedNumber();
    setHighlight(wanted);
    void loadApplications().then(page => {
      const application = wanted ? page?.items.find(a => a.applicationNumber === wanted) : undefined;
      if (application) void selectApplication(application);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    highlightedRow.current?.scrollIntoView({ block: 'center' });
  }, [data, highlight]);

  // An application of yours moved (relayed by the Shell): show its new status.
  useShellNotifications(useCallback(() => { void loadApplications(); }, []));

  // Picking an application tells the Shell, so the Application Workspace shows it.
  async function selectApplication(application: Application) {
    setSelectedId(application.applicationId);

    const root: { title: string; value: string | number }[] = [{ title: 'Customer ID', value: application.customerId }];
    try {
      const customer = await getJson<CustomerSummary>(`/bff/api/customers/${application.customerId}`);
      root.push({ title: 'Customer Number', value: customer.customerNumber });
    } catch {
      // The customer number is a convenience for the workspace; the ID alone still identifies the customer.
    }

    publishSelection(root, [
      { title: 'Application ID', value: application.applicationId },
      { title: 'Application Number', value: application.applicationNumber },
      { title: 'Status', value: statusLabel(application.status) },
    ]);
  }

  const highlightMissing = highlight !== null && data !== null && !data.items.some(a => a.applicationNumber === highlight);

  return (
    <MfeShell title="Onboarding applications" subtitle="Customer Onboarding">
      <div className="flex flex-col gap-8">
        <Card>
          <CardHeader
            icon={<ClipboardList />}
            title="Applications"
            description="Applications in your branch, newest first. Select one to show it in the Application Workspace. Status follows the onboarding workflow (KYC → Compliance → Account opening)."
            actions={
              <>
                <Button variant="ghost" size="sm" onClick={() => void loadApplications()}><RefreshCw aria-hidden="true" />Refresh</Button>
                <Link className={buttonVariants({ variant: 'accent', size: 'sm' })} href="/v1/onboarding/applications/new">
                  <FilePlus2 aria-hidden="true" />New application
                </Link>
              </>
            }
          />
          {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
          {highlightMissing && (
            <div className="px-6 pt-6">
              <Alert tone="info">{highlight} is not among the 25 newest applications of your branch.</Alert>
            </div>
          )}
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
                  <tr
                    key={a.applicationId}
                    ref={a.applicationNumber === highlight ? highlightedRow : undefined}
                    aria-selected={a.applicationId === selectedId}
                    aria-current={a.applicationNumber === highlight ? 'true' : undefined}
                    onClick={() => void selectApplication(a)}
                    className={cn(
                      'cursor-pointer',
                      a.applicationId === selectedId ? 'bg-accent-50/70' : 'hover:bg-subtle/70',
                      a.applicationNumber === highlight && 'outline outline-2 -outline-offset-2 outline-accent-500',
                    )}
                  >
                    <Td className="font-mono text-[13px] font-medium">
                      <button
                        type="button"
                        className="text-left hover:underline"
                        onClick={event => { event.stopPropagation(); void selectApplication(a); }}
                      >
                        {a.applicationNumber}
                      </button>
                    </Td>
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