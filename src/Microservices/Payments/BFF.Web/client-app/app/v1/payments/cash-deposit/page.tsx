'use client';

import { Banknote, CircleDollarSign, ReceiptText, Search, UserRound } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';
import { MfeShell, publishSelection, setUnsavedChanges } from '../../../components/MfeShell';
import { Button } from '../../../components/ui/button';
import { Card, CardContent, CardFooter, CardHeader } from '../../../components/ui/card';
import { cn } from '../../../components/ui/cn';
import { Alert } from '../../../components/ui/feedback';
import { Field, Input } from '../../../components/ui/form';
import { getJson, postJson } from '../../../lib/api';
import {
  type CashDepositReceipt, type PayerAccount,
  CASH_DEPOSIT_MAX, formatMoney, productLabel,
} from '../../../lib/payments';

const AMOUNT_PATTERN = /^\d{1,7}(\.\d{1,2})?$/;

// The amount a demo customer usually brings in: enough for a few payments and a gold purchase.
const DEFAULT_AMOUNT = '5000.00';

export default function CashDepositPage() {
  // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
  // │ One key per deposit form: pressing Deposit twice, or resending after a lost answer,
  // │ finds the same deposit instead of crediting the cash twice. A new deposit gets a new key.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const [idempotencyKey, setIdempotencyKey] = useState(() => crypto.randomUUID());

  // 1 - Customer
  const [search, setSearch] = useState('');
  const [accounts, setAccounts] = useState<PayerAccount[] | null>(null);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [account, setAccount] = useState<PayerAccount | null>(null);

  // 2 - Cash
  const [amount, setAmount] = useState(DEFAULT_AMOUNT);
  const [counted, setCounted] = useState(false);

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<CashDepositReceipt | null>(null);

  // Anything chosen is protected by the Shell's "unsaved changes" check, until it is recorded.
  useEffect(() => {
    setUnsavedChanges(!submitting && !receipt && !!account);
  }, [account, submitting, receipt]);
  useEffect(() => () => setUnsavedChanges(false), []);

  // Customer search (debounced): the branch's ACTIVE accounts by holder name or customer number.
  useEffect(() => {
    const term = search.trim();
    setSearchError(null);
    if (account || term.length < 2) {
      setAccounts(null);
      return;
    }
    const timer = window.setTimeout(() => {
      getJson<PayerAccount[]>(`/bff/api/payer-accounts?search=${encodeURIComponent(term)}`)
        .then(setAccounts)
        .catch(e => setSearchError(e instanceof Error ? e.message : 'Unable to search accounts.'));
    }, 300);
    return () => window.clearTimeout(timer);
  }, [search, account]);

  const chooseAccount = (chosen: PayerAccount) => {
    setAccount(chosen);
    publishSelection(
      [{ title: 'Customer Name', value: chosen.holderName }, { title: 'Customer Number', value: chosen.customerNumber }],
      [{ title: 'Deposit Account', value: `${chosen.bsb} ${chosen.accountNumber}` }],
    );
  };

  // ---- validation (the Accounts API checks everything again; this explains it early)
  const amountValue = AMOUNT_PATTERN.test(amount.trim()) ? Number(amount.trim()) : NaN;
  const amountValid = Number.isFinite(amountValue) && amountValue > 0 && amountValue <= CASH_DEPOSIT_MAX;

  const blockers = useMemo(() => [
    !account && 'Choose the account the cash goes into.',
    !amountValid && `Enter an amount in dollars and cents, at most ${formatMoney(CASH_DEPOSIT_MAX)}.`,
    !counted && 'Confirm that you counted the cash.',
  ].filter(Boolean) as string[], [account, amountValid, counted]);

  const deposit = async () => {
    if (!account || blockers.length > 0) return;
    setSubmitting(true);
    setError(null);
    try {
      const result = await postJson<CashDepositReceipt>('/bff/api/cash-deposits', {
        customerNumber: account.customerNumber,
        bsb: account.bsb,
        accountNumber: account.accountNumber,
        amount: amountValue,
      }, { 'Idempotency-Key': idempotencyKey });
      setUnsavedChanges(false);
      setReceipt(result);
    } catch (e) {
      // The same Idempotency-Key is kept: trying again can never credit the cash twice.
      setError(e instanceof Error ? e.message : 'The deposit could not be recorded.');
    } finally {
      setSubmitting(false);
    }
  };

  const startAgain = () => {
    setIdempotencyKey(crypto.randomUUID());
    setReceipt(null);
    setAccount(null);
    setSearch('');
    setAmount(DEFAULT_AMOUNT);
    setCounted(false);
    setError(null);
  };

  if (receipt) {
    return (
      <MfeShell title="Cash deposit" subtitle="Payments">
        <Card className="mx-auto max-w-xl">
          <CardHeader icon={<ReceiptText />} title="Deposit recorded" description="Read the receipt back to the customer." />
          <CardContent className="space-y-3 text-sm">
            <Alert tone="success" title={`${formatMoney(receipt.amount)} credited`}>
              The cash is in the customer's account and available at once.
            </Alert>
            <Row label="Deposit number" value={receipt.depositNumber} strong />
            <Row label="Customer" value={account ? `${account.holderName} · ${receipt.customerNumber}` : receipt.customerNumber} />
            <Row label="Account" value={`${receipt.bsb} ${receipt.accountNumber}`} />
            <Row label="Amount" value={formatMoney(receipt.amount)} />
            <Row label="Balance after" value={formatMoney(receipt.balanceAfter)} strong />
            <Row label="Recorded" value={new Date(receipt.recordedAt).toLocaleString('en-AU')} />
          </CardContent>
          <CardFooter>
            <Button className="w-full" size="lg" onClick={startAgain}>New deposit</Button>
          </CardFooter>
        </Card>
      </MfeShell>
    );
  }

  return (
    <MfeShell title="Cash deposit" subtitle="Payments">
      <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_380px]">
        <div className="flex flex-col gap-6">
          {/* 1 - Customer */}
          <Card>
            <CardHeader icon={<UserRound />} title="1 · Customer" description="Find the customer, then choose the account the cash goes into." />
            <CardContent className="space-y-4">
              {account ? (
                <div className="flex items-center justify-between gap-4 rounded-control border border-accent-200 bg-accent-50/50 px-4 py-3">
                  <div className="min-w-0">
                    <p className="font-medium text-ink">{account.holderName} <span className="font-mono text-xs text-ink-faint">{account.customerNumber}</span></p>
                    <p className="font-mono text-[13px] text-ink-muted">{account.bsb} {account.accountNumber} · {productLabel(account.product)}</p>
                  </div>
                  <div className="text-right">
                    <p className="text-xs uppercase tracking-wide text-ink-faint">Available</p>
                    <p className="font-semibold text-ink">{formatMoney(account.available)}</p>
                  </div>
                  <Button variant="secondary" size="sm" onClick={() => { setAccount(null); setSearch(''); }}>Change</Button>
                </div>
              ) : (
                <>
                  <Field label="Customer" htmlFor="deposit-search" hint="Name or customer number (CUST-…), at least 2 characters. Only active accounts of your branch.">
                    <div className="relative">
                      <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-ink-faint" aria-hidden="true" />
                      <Input id="deposit-search" className="pl-9" value={search} onChange={e => setSearch(e.target.value)} placeholder="e.g. Camilla or CUST-100001" autoComplete="off" />
                    </div>
                  </Field>
                  {searchError && <Alert tone="danger">{searchError}</Alert>}
                  {accounts && accounts.length === 0 && <p className="text-sm text-ink-muted">No active account of your branch matches “{search.trim()}”.</p>}
                  {accounts && accounts.length > 0 && (
                    <ul className="divide-y divide-line overflow-hidden rounded-control border border-line" aria-label="Matching accounts">
                      {accounts.map(a => (
                        <li key={`${a.bsb}-${a.accountNumber}`}>
                          <button type="button" onClick={() => chooseAccount(a)}
                            className="flex w-full items-center justify-between gap-4 px-4 py-3 text-left hover:bg-subtle">
                            <span className="min-w-0">
                              <span className="block font-medium text-ink">{a.holderName} <span className="font-mono text-xs text-ink-faint">{a.customerNumber}</span></span>
                              <span className="block font-mono text-[13px] text-ink-muted">{a.bsb} {a.accountNumber} · {productLabel(a.product)}</span>
                            </span>
                            <span className="text-right">
                              <span className="block text-xs uppercase tracking-wide text-ink-faint">Available</span>
                              <span className="block font-semibold text-ink">{formatMoney(a.available)}</span>
                            </span>
                          </button>
                        </li>
                      ))}
                    </ul>
                  )}
                </>
              )}
            </CardContent>
          </Card>

          {/* 2 - Cash */}
          <Card>
            <CardHeader icon={<Banknote />} title="2 · Cash" description="Count the notes in front of the customer, then enter the total." />
            <CardContent className="space-y-4">
              <Field label="Amount (AUD)" htmlFor="deposit-amount" required hint={`Cash only; at most ${formatMoney(CASH_DEPOSIT_MAX)} per deposit.`}>
                <Input id="deposit-amount" className="sm:max-w-[200px]" value={amount} inputMode="decimal" placeholder="0.00"
                  onChange={e => setAmount(e.target.value.replace(/[^\d.]/g, ''))} />
              </Field>
              <label className="flex items-center gap-2 text-[13px] text-ink">
                <input type="checkbox" checked={counted} onChange={e => setCounted(e.target.checked)} />
                I have counted the cash and it matches the amount
              </label>
            </CardContent>
          </Card>
        </div>

        {/* 3 - Review */}
        <Card className="lg:sticky lg:top-6">
          <CardHeader icon={<CircleDollarSign />} title="3 · Review" description="Read the deposit back to the customer before you record it." />
          <CardContent className="space-y-3 text-sm">
            <Row label="Customer" value={account ? `${account.holderName} · ${account.customerNumber}` : '—'} />
            <Row label="Account" value={account ? `${account.bsb} ${account.accountNumber}` : '—'} />
            <Row label="Method" value="Cash" />
            <Row label="Amount" value={amountValid ? formatMoney(amountValue) : '—'} strong />
            <Row label="Available after" value={account && amountValid ? formatMoney(account.available + amountValue) : '—'} />
            {error && <Alert tone="danger">{error}</Alert>}
            {blockers.length > 0 && (
              <ul className="list-inside list-disc space-y-0.5 text-[13px] text-ink-muted" aria-label="Still to do">
                {blockers.map(b => <li key={b}>{b}</li>)}
              </ul>
            )}
          </CardContent>
          <CardFooter>
            <Button className="w-full" size="lg" onClick={() => void deposit()} disabled={blockers.length > 0 || submitting}>
              {submitting ? 'Recording the deposit…' : 'Deposit'}
            </Button>
          </CardFooter>
        </Card>
      </div>
    </MfeShell>
  );
}

function Row({ label, value, strong }: { label: string; value: string; strong?: boolean }) {
  return (
    <div className="flex justify-between gap-4">
      <span className="text-ink-muted">{label}</span>
      <span className={cn('min-w-0 break-words text-right text-ink', strong && 'font-semibold')}>{value}</span>
    </div>
  );
}