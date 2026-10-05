'use client';

import { Landmark } from 'lucide-react';
import { Table, Td, Th, formatDateTime } from './ui/data';
import { EmptyState, StatusBadge } from './ui/feedback';
import { type Account, formatAccount, productLabel } from '../lib/accounts';

/** The opened accounts of the officer's branch (read-only). */
export function AccountsTable({ accounts }: { accounts: Account[] }) {
  if (accounts.length === 0) {
    return (
      <EmptyState icon={<Landmark />} title="No accounts yet">
        Accounts appear here once an approved application has been opened by the core-banking system.
      </EmptyState>
    );
  }

  return (
    <Table>
      <thead>
        <tr>
          <Th>Account</Th>
          <Th>Customer</Th>
          <Th>Product</Th>
          <Th>Status</Th>
          <Th>Opened</Th>
          <Th>Core-banking reference</Th>
        </tr>
      </thead>
      <tbody>
        {accounts.map(a => (
          <tr key={a.accountId} className="hover:bg-subtle/70">
            <Td className="font-mono text-[13px] font-semibold text-brand-700">{formatAccount(a.bsb, a.accountNumber)}</Td>
            <Td className="font-mono text-[13px]">{a.customerNumber}</Td>
            <Td className="text-ink-muted">{productLabel(a.product)}</Td>
            <Td><StatusBadge status={a.status} /></Td>
            <Td className="text-ink-muted">{formatDateTime(a.openedAt)}</Td>
            <Td className="font-mono text-xs text-ink-faint">{a.coreBankingReference}</Td>
          </tr>
        ))}
      </tbody>
    </Table>
  );
}