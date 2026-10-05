'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { MfeShell, publishWorkspaceContext, replacePersistentContext, type WorkspaceContext } from '../../../components/MfeShell';
import { getJson } from '../../../lib/api';
import { ArrowRight, CheckCircle2, Hourglass, UserPlus, Users } from 'lucide-react';
import { Button, buttonVariants } from '../../../components/ui/button';
import { Card, CardHeader } from '../../../components/ui/card';
import { Table, Td, Th } from '../../../components/ui/data';
import { Alert, EmptyState, StatusBadge } from '../../../components/ui/feedback';

interface Customer {
  customerId: number;
  customerNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  status: string;
}

/** Only a prospect can start onboarding: the Customer Onboarding API enforces it; the grid only reflects it. */
function onboardingState(status: string): 'can-start' | 'in-progress' | 'onboarded' | 'unavailable' {
  switch (status.toUpperCase()) {
    case 'PROSPECT': return 'can-start';
    case 'ONBOARDING': return 'in-progress';
    case 'ACTIVE': return 'onboarded';
    default: return 'unavailable';
  }
}

interface Page {
  items: Customer[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

export default function CustomersPage() {
  const router = useRouter();
  const [data, setData] = useState<Page | null>(null);
  const [error, setError] = useState<string | null>(null);
  const workspaceContextRef = useRef<WorkspaceContext | null>(null);

  useEffect(() => {
    const handleContextHandoff = (event: Event) => {
      workspaceContextRef.current =
        (event as CustomEvent<WorkspaceContext>).detail ?? null;
    };

    window.addEventListener('bss-context-handoff', handleContextHandoff);
    return () =>
      window.removeEventListener('bss-context-handoff', handleContextHandoff);
  }, []);

  useEffect(() => {
    getJson<Page>('/bff/api/customers?pageNumber=1&pageSize=25')
      .then(setData)
      .catch(e => setError(e.message));
  }, []);

  function startOnboarding(customer: Customer) {
    if (onboardingState(customer.status) !== 'can-start') return;

    const previousContext = workspaceContextRef.current;

    const previousCustomerId = previousContext?.persistentContext.find(
      item => item.title.toLowerCase() === 'customer id',
    )?.value;

    const rootCustomerChanged =
      previousCustomerId === undefined ||
      Number(previousCustomerId) !== customer.customerId;

    const persistentContext = [
      { title: 'Customer ID', value: customer.customerId },
      { title: 'Customer Number', value: customer.customerNumber },
    ];

    if (rootCustomerChanged) {
      // Changing the root customer invalidates all subordinate context.
      replacePersistentContext(persistentContext);
    } else {
      // Re-selecting the same customer does not change the business scope, so
      // previously retained/current context may still be useful.
      publishWorkspaceContext({
        persistentContext,
        currentContext: previousContext?.currentContext ?? [],
        retainedContext: previousContext?.retainedContext ?? [],
      });
    }

    router.push('/v1/onboarding/applications/view-all');
  }

  return (
    <MfeShell title="Customers" subtitle="Customer Onboarding">
      <Card>
        <CardHeader
          icon={<Users />}
          title="Customer directory"
          description="Customers of your branch. Onboarding can start for a prospect (new, or after a rejected application); onboarded customers and those with an application in progress are read-only."
          actions={
            <Link className={buttonVariants({ variant: 'accent', size: 'sm' })} href="/v1/onboarding/applications/view-all">
              <UserPlus aria-hidden="true" />New customer
            </Link>
          }
        />

        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading customers…</p>}
        {data?.items.length === 0 && (
          <EmptyState icon={<Users />} title="No customers yet">Customers you onboard will appear here.</EmptyState>
        )}

        {data && data.items.length > 0 && (
          <Table>
            <thead>
              <tr>
                <Th>Customer</Th>
                <Th>Name</Th>
                <Th>Email</Th>
                <Th>Status</Th>
                <Th className="text-right">Action</Th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(c => (
                <tr key={c.customerId} className="hover:bg-subtle/70">
                  <Td className="font-mono text-[13px] font-medium">{c.customerNumber}</Td>
                  <Td className="font-medium">{c.firstName} {c.lastName}</Td>
                  <Td className="text-ink-muted">{c.email}</Td>
                  <Td><StatusBadge status={c.status} /></Td>
                  <Td className="text-right">
                    {onboardingState(c.status) === 'can-start' && (
                      <Button variant="secondary" size="sm" onClick={() => startOnboarding(c)}>
                        Start onboarding<ArrowRight aria-hidden="true" />
                      </Button>
                    )}
                    {onboardingState(c.status) === 'in-progress' && (
                      <span className="inline-flex items-center gap-1.5 text-[13px] text-ink-muted"><Hourglass className="size-4" aria-hidden="true" />Application in progress</span>
                    )}
                    {onboardingState(c.status) === 'onboarded' && (
                      <span className="inline-flex items-center gap-1.5 text-[13px] text-success-700"><CheckCircle2 className="size-4" aria-hidden="true" />Onboarded</span>
                    )}
                    {onboardingState(c.status) === 'unavailable' && <span className="text-[13px] text-ink-faint">—</span>}
                  </Td>
                </tr>
              ))}
            </tbody>
          </Table>
        )}
      </Card>
    </MfeShell>
  );
}