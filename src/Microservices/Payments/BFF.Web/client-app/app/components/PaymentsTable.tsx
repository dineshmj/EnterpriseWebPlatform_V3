'use client';

import { ChevronRight } from 'lucide-react';
import Link from 'next/link';
import { Table, Td, Th, formatDateTime } from './ui/data';
import { StatusBadge } from './ui/feedback';
import { type PaymentSummary, formatMoney, personLabel } from '../lib/payments';

/** The branch's payments as a table; each row opens the payment's status page. */
export function PaymentsTable({ items, me }: { items: PaymentSummary[]; me?: string }) {
  return (
    <Table>
      <thead>
        <tr>
          <Th>Payment</Th>
          <Th>Customer</Th>
          <Th>Payee</Th>
          <Th className="text-right">Amount</Th>
          <Th>Status</Th>
          <Th>Started by</Th>
          <Th>Started</Th>
          <Th><span className="sr-only">Open</span></Th>
        </tr>
      </thead>
      <tbody>
        {items.map(item => {
          const href = `/v1/payments/view-details/?paymentId=${item.paymentId}`;
          return (
            <tr key={item.paymentId} className="group hover:bg-subtle/70">
              <Td>
                <Link className="font-mono text-[13px] font-semibold text-brand-700 hover:underline" href={href}>{item.paymentNumber}</Link>
              </Td>
              <Td className="font-mono text-[13px] text-ink-muted">{item.customerNumber}</Td>
              <Td>
                <div className="font-medium text-ink">{item.payeeName}</div>
                <div className="font-mono text-xs text-ink-faint">{item.toBsb} {item.toAccountNumber}</div>
              </Td>
              <Td className="text-right font-semibold">{formatMoney(item.amount)}</Td>
              <Td><StatusBadge status={item.status} /></Td>
              <Td className="text-ink-muted">{personLabel(item.initiatedByUserId, item.initiatedByLanId, me)}</Td>
              <Td className="text-ink-muted">{formatDateTime(item.createdAt)}</Td>
              <Td className="text-right">
                <Link aria-label={`Open payment ${item.paymentNumber}`} href={href}
                  className="inline-flex size-8 items-center justify-center rounded-md text-ink-faint group-hover:text-brand-700">
                  <ChevronRight className="size-4" aria-hidden="true" />
                </Link>
              </Td>
            </tr>
          );
        })}
      </tbody>
    </Table>
  );
}