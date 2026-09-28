'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useEffect, useRef, useState } from 'react';
import { MfeShell, publishWorkspaceContext, replacePersistentContext, type WorkspaceContext } from '../../../components/MfeShell';
import { getJson } from '../../../lib/api';

interface Customer {
  customerId: number;
  customerNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  status: string;
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
    <MfeShell title="Customers" subtitle="Customer Management · View Customers">
      <div className="card">
        <div className="actions" style={{ marginTop: 0, justifyContent: 'space-between' }}>
          <div>
            <h2 style={{ marginBottom: 4 }}>Customer directory</h2>
            <div style={{ color: '#667085', fontSize: '.75rem' }}>
              Authoritative data is owned by the Customer Onboarding API.
            </div>
          </div>
          <Link className="primary" href="/v1/onboarding/applications/view-all">
            Raise onboarding application
          </Link>
        </div>

        {error && <div className="error" style={{ marginTop: 14 }}>{error}</div>}
        {!data && !error && <div className="empty">Loading customers...</div>}

        {data && (
          <table className="table">
            <thead>
              <tr>
                <th>Customer</th>
                <th>Name</th>
                <th>Email</th>
                <th>Status</th>
                <th>Action</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map(c => (
                <tr key={c.customerId}>
                  <td>{c.customerNumber}</td>
                  <td>{c.firstName} {c.lastName}</td>
                  <td>{c.email}</td>
                  <td>{c.status}</td>
                  <td>
                    <button
                      className="primary"
                      type="button"
                      style={{ padding: '6px 10px', fontSize: '.72rem' }}
                      onClick={() => startOnboarding(c)}
                    >
                      Start Onboarding
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}

        {data?.items.length === 0 && <div className="empty">No customers found.</div>}
      </div>
    </MfeShell>
  );
}
