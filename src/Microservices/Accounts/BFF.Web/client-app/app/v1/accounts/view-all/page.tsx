'use client';

import { Landmark } from 'lucide-react';
import { useEffect, useState } from 'react';
import { AccountsTable } from '../../../components/AccountsTable';
import { MfeShell } from '../../../components/MfeShell';
import { Card, CardHeader } from '../../../components/ui/card';
import { Alert } from '../../../components/ui/feedback';
import { getJson } from '../../../lib/api';
import type { Account, Page } from '../../../lib/accounts';

export default function AccountsPage() {
  const [data, setData] = useState<Page<Account> | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getJson<Page<Account>>('/bff/api/accounts/accounts?pageNumber=1&pageSize=100')
      .then(setData)
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load accounts.'));
  }, []);

  return (
    <MfeShell title="Accounts" subtitle="Accounts">
      <Card>
        <CardHeader
          icon={<Landmark />}
          title="Accounts"
          description="Accounts of your branch opened by the core-banking system, newest first."
        />
        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading accounts…</p>}
        {data && <AccountsTable accounts={data.items} />}
      </Card>
    </MfeShell>
  );
}