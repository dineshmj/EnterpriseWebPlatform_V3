'use client';

import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { useEffect, useState } from 'react';
import { MfeShell } from '../../../components/MfeShell';
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

  useEffect(() => {
    getJson<Page>('/bff/api/customers?pageNumber=1&pageSize=25')
      .then(setData)
      .catch(e => setError(e.message));
  }, []);

  function startOnboarding(customer: Customer) {
    // The MFE does not interpret the workspace context. It publishes the
    // selected customer as the current context to the Shell. The Shell keeps
    // that context and sends it back through BSS_CONTEXT_HANDOFF when the
    // onboarding page becomes ready.
    const parentOrigin = document.referrer ? new URL(document.referrer).origin : '*';

    window.parent?.postMessage(
      {
        type: 'BSS_CONTEXT_UPDATE',
        context: {
          persistentContext: [],
          currentContext: [
            { title: 'Customer ID', value: customer.customerId },
          ],
          retainedContext: [],
        },
      },
      parentOrigin,
    );

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
