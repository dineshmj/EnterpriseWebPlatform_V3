'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { MfeShell, publishWorkspaceContext, replacePersistentContext, type WorkspaceContext } from '../../../components/MfeShell';
import { getJson } from '../../../lib/api';
import { ArrowRight, CheckCircle2, Hourglass, Search, UserPlus, Users, X } from 'lucide-react';
import { Button, buttonVariants } from '../../../components/ui/button';
import { Card, CardHeader } from '../../../components/ui/card';
import { Table, Td, Th } from '../../../components/ui/data';
import { Alert, EmptyState, StatusBadge } from '../../../components/ui/feedback';
import { useFocusFirstField } from '../../../components/ui/focus';
import { Input } from '../../../components/ui/form';

interface Customer {
  customerId: number;
  customerNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  status: string;
  /** The customer's most recent onboarding application, if any. */
  latestApplication: { applicationNumber: string; status: string; updatedAt: string } | null;
  /** True once the customer's key was destroyed: name and e-mail are empty, and nothing can be done. */
  personalDataErased: boolean;
}

/** "Last application APP-… rejected 14 Oct" - only for a prospect whose last attempt ended without an account. */
function lastAttempt(c: Customer): string | null {
  const latest = c.latestApplication;
  if (!latest || c.status.toUpperCase() !== 'PROSPECT') return null;
  const outcome = latest.status.toUpperCase();
  if (outcome !== 'REJECTED' && outcome !== 'CANCELLED') return null;
  const when = new Date(latest.updatedAt).toLocaleDateString(undefined, { day: '2-digit', month: 'short' });
  return `Last application ${latest.applicationNumber} ${outcome.toLowerCase()} ${when}`;
}

/** Only a prospect can start onboarding: the Customer Onboarding API enforces it; the grid only reflects it. */
function onboardingState(c: Customer): 'can-start' | 'in-progress' | 'onboarded' | 'unavailable' {
  if (c.personalDataErased) return 'unavailable';
  switch (c.status.toUpperCase()) {
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
  const [searchInput, setSearchInput] = useState('');
  const [searched, setSearched] = useState('');
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

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ Names and e-mail addresses are encrypted at rest, so the API cannot search INSIDE
  // │ them: a customer number matches in part, an e-mail address or a surname only in full
  // │ (case and outer spaces ignored). The hint and the empty result say so.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const load = useCallback((search: string) => {
    const query = new URLSearchParams({ pageNumber: '1', pageSize: '25' });
    if (search) query.set('search', search);
    setData(null);
    setError(null);
    setSearched(search);
    getJson<Page>(`/bff/api/customers?${query.toString()}`)
      .then(setData)
      .catch(e => setError(e.message));
  }, []);

  useEffect(() => load(''), [load]);

  function submitSearch(event: FormEvent) {
    event.preventDefault();
    load(searchInput.trim());
  }

  function clearSearch() {
    setSearchInput('');
    load('');
  }

  function startOnboarding(customer: Customer) {
    if (onboardingState(customer) !== 'can-start') return;

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

    router.push('/v1/onboarding/applications/new');
  }

  const searchFormRef = useFocusFirstField();

  return (
    <MfeShell title="Customers" subtitle="Customer Onboarding">
      <Card>
        <CardHeader
          icon={<Users />}
          title="Customer directory"
          description="Customers of your branch. Onboarding can start for a prospect (new, or after a rejected application); onboarded customers and those with an application in progress are read-only."
          actions={
            <Link className={buttonVariants({ variant: 'accent', size: 'sm' })} href="/v1/onboarding/applications/new">
              <UserPlus aria-hidden="true" />New customer
            </Link>
          }
        />

        <form ref={searchFormRef} role="search" onSubmit={submitSearch} className="flex flex-wrap items-center gap-2 border-b border-line px-6 py-4">
          <label htmlFor="customer-search" className="sr-only">Search customers</label>
          <Input
            id="customer-search"
            type="search"
            maxLength={254}
            value={searchInput}
            onChange={e => setSearchInput(e.target.value)}
            placeholder="Customer number, full e-mail address or surname"
            className="max-w-md flex-1"
          />
          <Button type="submit" variant="secondary" size="sm"><Search aria-hidden="true" />Search</Button>
          {searched && (
            <Button type="button" variant="ghost" size="sm" onClick={clearSearch}><X aria-hidden="true" />Clear</Button>
          )}
          <p className="basis-full text-xs text-ink-muted">
            A customer number matches in part; an e-mail address or surname only in full (any case).
          </p>
        </form>

        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading customers…</p>}
        {data?.items.length === 0 && !searched && (
          <EmptyState icon={<Users />} title="No customers yet">Customers you onboard will appear here.</EmptyState>
        )}
        {data?.items.length === 0 && searched && (
          <EmptyState icon={<Search />} title={`No customer matches “${searched}”`}>
            E-mail addresses and surnames are matched in full only: try the whole address or surname, or the customer number.
          </EmptyState>
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
                  {/* SEC: an erased customer's key is gone - there is nothing to show, and it says so. */}
                  {c.personalDataErased ? (
                    <Td colSpan={2} className="italic text-ink-muted">Personal data erased</Td>
                  ) : (
                    <>
                      <Td className="font-medium">{c.firstName} {c.lastName}</Td>
                      <Td className="text-ink-muted">{c.email}</Td>
                    </>
                  )}
                  <Td>
                    <StatusBadge status={c.status} />
                    {lastAttempt(c) && <p className="mt-1 text-[12px] text-ink-muted">{lastAttempt(c)}</p>}
                  </Td>
                  <Td className="text-right">
                    {onboardingState(c) === 'can-start' && (
                      <Button variant="secondary" size="sm" onClick={() => startOnboarding(c)}>
                        Start onboarding<ArrowRight aria-hidden="true" />
                      </Button>
                    )}
                    {onboardingState(c) === 'in-progress' && (
                      <span className="inline-flex items-center gap-1.5 text-[13px] text-ink-muted"><Hourglass className="size-4" aria-hidden="true" />Application in progress</span>
                    )}
                    {onboardingState(c) === 'onboarded' && (
                      <span className="inline-flex items-center gap-1.5 text-[13px] text-success-700"><CheckCircle2 className="size-4" aria-hidden="true" />Onboarded</span>
                    )}
                    {onboardingState(c) === 'unavailable' && <span className="text-[13px] text-ink-faint">—</span>}
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