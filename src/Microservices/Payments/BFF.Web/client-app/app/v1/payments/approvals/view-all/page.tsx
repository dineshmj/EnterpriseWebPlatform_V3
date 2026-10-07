'use client';

import { CheckCircle2, ShieldCheck } from 'lucide-react';
import { useCallback, useEffect, useState } from 'react';
import { MfeShell, type ShellNotification, useShellNotifications } from '../../../../components/MfeShell';
import { PaymentsTable } from '../../../../components/PaymentsTable';
import { Card, CardHeader } from '../../../../components/ui/card';
import { Alert, EmptyState } from '../../../../components/ui/feedback';
import { getJson } from '../../../../lib/api';
import { type Page, type PaymentPolicy, type PaymentSummary, type StaffUser, formatMoney, getStaffUser } from '../../../../lib/payments';

/**
 * The payments officer's queue: the branch's payments above the approval tier, waiting for
 * a decision. Their funds are already reserved. Opening one shows Approve / Reject.
 */
export default function PaymentApprovalsPage() {
  const [data, setData] = useState<Page<PaymentSummary> | null>(null);
  const [user, setUser] = useState<StaffUser | null>(null);
  const [policy, setPolicy] = useState<PaymentPolicy | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);

  useEffect(() => {
    getStaffUser().then(setUser).catch(() => setUser(null));
    getJson<PaymentPolicy>('/bff/api/payments/policy').then(setPolicy).catch(() => setPolicy(null));
  }, []);

  // New work (relayed by the Shell) reloads the queue.
  useShellNotifications(useCallback((n: ShellNotification) => {
    if (n.target?.mfe === 'payments') setRefresh(r => r + 1);
  }, []));

  useEffect(() => {
    setError(null);
    getJson<Page<PaymentSummary>>('/bff/api/payments?pageNumber=1&pageSize=50&status=PENDING_APPROVAL')
      .then(setData)
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load the approval queue.'));
  }, [refresh]);

  const limit = policy?.yourApprovalLimit;
  const limitText = limit === undefined ? null
    : limit === null ? 'Your clearance may approve any amount.'
      : limit === 0 ? 'Your clearance does not allow approving payments; you may still reject.'
        : `Your clearance may approve up to ${formatMoney(limit)}.`;

  return (
    <MfeShell title="Payment approvals" subtitle="Payments">
      <Card>
        <CardHeader
          icon={<ShieldCheck />}
          title="Awaiting approval"
          description={`Payments of your branch above ${policy ? formatMoney(policy.approvalThreshold) : 'the approval tier'}. The funds are reserved; open one to approve or reject it.${limitText ? ` ${limitText}` : ''}`}
        />
        {error && <div className="p-6"><Alert tone="danger">{error}</Alert></div>}
        {!data && !error && <p className="px-6 py-8 text-sm text-ink-muted">Loading the approval queue…</p>}
        {data && data.items.length === 0 && (
          <EmptyState icon={<CheckCircle2 />} title="Nothing to approve">No payment of your branch is waiting for a decision.</EmptyState>
        )}
        {data && data.items.length > 0 && <PaymentsTable items={data.items} me={user?.sub} />}
      </Card>
    </MfeShell>
  );
}