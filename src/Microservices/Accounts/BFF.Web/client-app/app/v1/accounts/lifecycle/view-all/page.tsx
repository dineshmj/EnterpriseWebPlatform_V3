'use client';

import { RefreshCw } from 'lucide-react';
import { useEffect, useState } from 'react';
import { AccountsTable } from '../../../../components/AccountsTable';
import { MfeShell } from '../../../../components/MfeShell';
import { Card, CardContent, CardHeader } from '../../../../components/ui/card';
import { Alert } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';
import type { Account, Page } from '../../../../lib/accounts';

const STATES = ['ACTIVE', 'FROZEN', 'CLOSED'] as const;

export default function AccountLifecyclePage() {
  const [data, setData] = useState<Page<Account> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getJson<Page<Account>>('/bff/api/accounts/accounts?pageNumber=1&pageSize=100')
      .then(setData)
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load accounts.'));
  }, []);

  const count = (state: string) => data?.items.filter(a => a.status === state).length ?? 0;

  return (
    <MfeShell title="Account lifecycle" subtitle="Accounts">
      <div className="flex flex-col gap-6">
        <Card>
          <CardHeader
            icon={<RefreshCw />}
            title="Lifecycle overview"
            description="An account is ACTIVE when the core-banking system opens it; FROZEN and CLOSED follow from lifecycle operations."
          />
          <CardContent className="space-y-4">
            <dl className="grid grid-cols-3 gap-4">
              {STATES.map(state => (
                <div key={state} className="rounded-control border border-line bg-subtle/60 px-4 py-3">
                  <dt className="text-xs font-medium uppercase tracking-wide text-ink-muted">{state.toLowerCase()}</dt>
                  <dd className="mt-1 text-2xl font-semibold text-ink">{data ? count(state) : '–'}</dd>
                </div>
              ))}
            </dl>
            <Alert tone="info">
              Freezing and closing accounts are planned with the Payments work (funds reservations need an account's lifecycle);
              this page shows each account's current state.
            </Alert>
          </CardContent>
        </Card>

        <Card>
          <CardHeader title="Accounts of your branch" />
          {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
          {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading accounts…</p>}
          {data && <AccountsTable accounts={data.items} />}
        </Card>
      </div>
    </MfeShell>
  );
}