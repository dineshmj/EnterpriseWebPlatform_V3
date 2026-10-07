'use client';

import { ArrowRightLeft, BadgeCheck, Building2, CircleDollarSign, Search, UserRound } from 'lucide-react';
import { useRouter } from 'next/navigation';
import { useEffect, useMemo, useState } from 'react';
import { MfeShell, publishSelection, setUnsavedChanges } from '../../../components/MfeShell';
import { Button } from '../../../components/ui/button';
import { Card, CardContent, CardFooter, CardHeader } from '../../../components/ui/card';
import { cn } from '../../../components/ui/cn';
import { Alert } from '../../../components/ui/feedback';
import { Field, Input } from '../../../components/ui/form';
import { getJson, postJson } from '../../../lib/api';
import {
  type BsbInfo, type PayeeConfirmation, type PayerAccount, type PaymentPolicy,
  formatMoney, normaliseBsb, productLabel,
} from '../../../lib/payments';

type CheckedConfirmation = PayeeConfirmation & { checkedFor: string };

const AMOUNT_PATTERN = /^\d{1,7}(\.\d{1,2})?$/;

export default function NewPaymentPage() {
  const router = useRouter();

  // One key per payment form: pressing Transfer twice, or resending after a lost answer,
  // finds the same payment instead of paying twice.
  const [idempotencyKey] = useState(() => crypto.randomUUID());
  const [policy, setPolicy] = useState<PaymentPolicy | null>(null);

  // 1 - From
  const [search, setSearch] = useState('');
  const [accounts, setAccounts] = useState<PayerAccount[] | null>(null);
  const [searchError, setSearchError] = useState<string | null>(null);
  const [payer, setPayer] = useState<PayerAccount | null>(null);

  // 2 - To
  const [bsb, setBsb] = useState('');
  const [bsbInfo, setBsbInfo] = useState<BsbInfo | null>(null);
  const [bsbError, setBsbError] = useState<string | null>(null);
  const [toAccount, setToAccount] = useState('');
  const [payeeName, setPayeeName] = useState('');
  const [confirmation, setConfirmation] = useState<CheckedConfirmation | null>(null);
  const [confirmationError, setConfirmationError] = useState<string | null>(null);
  const [checking, setChecking] = useState(false);
  const [mismatchConfirmed, setMismatchConfirmed] = useState(false);

  // 3 - Amount
  const [amount, setAmount] = useState('');
  const [reference, setReference] = useState('');

  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    getJson<PaymentPolicy>('/bff/api/payments/policy').then(setPolicy).catch(() => setPolicy(null));
  }, []);

  // Anything typed is protected by the Shell's "unsaved changes" check.
  useEffect(() => {
    setUnsavedChanges(!submitting && !!(payer || bsb || toAccount || payeeName || amount || reference));
  }, [payer, bsb, toAccount, payeeName, amount, reference, submitting]);
  useEffect(() => () => setUnsavedChanges(false), []);

  // Customer search (debounced): the branch's ACTIVE accounts by holder name or customer number.
  useEffect(() => {
    const term = search.trim();
    setSearchError(null);
    if (payer || term.length < 2) {
      setAccounts(null);
      return;
    }
    const timer = window.setTimeout(() => {
      getJson<PayerAccount[]>(`/bff/api/payer-accounts?search=${encodeURIComponent(term)}`)
        .then(setAccounts)
        .catch(e => setSearchError(e instanceof Error ? e.message : 'Unable to search accounts.'));
    }, 300);
    return () => window.clearTimeout(timer);
  }, [search, payer]);

  // BSB directory lookup as soon as six digits are there.
  const normalisedBsb = normaliseBsb(bsb);
  useEffect(() => {
    setBsbInfo(null);
    setBsbError(null);
    if (!/^\d{3}-\d{3}$/.test(normalisedBsb)) return;
    getJson<BsbInfo>(`/bff/api/payments/bsb/${normalisedBsb}`)
      .then(setBsbInfo)
      .catch(e => setBsbError(e instanceof Error ? e.message : 'Unable to check the BSB.'));
  }, [normalisedBsb]);

  // Confirmation of Payee belongs to exactly the details it was checked for.
  const payeeKey = `${normalisedBsb}|${toAccount.trim()}|${payeeName.trim().toLowerCase()}`;
  const currentConfirmation = confirmation?.checkedFor === payeeKey ? confirmation : null;
  useEffect(() => setMismatchConfirmed(false), [payeeKey]);

  const accountValid = /^\d{5,9}$/.test(toAccount.trim());
  const canCheckPayee = !!bsbInfo && accountValid && payeeName.trim().length > 0;

  const checkPayee = async () => {
    if (!canCheckPayee || currentConfirmation) return;
    setChecking(true);
    setConfirmationError(null);
    try {
      const result = await postJson<PayeeConfirmation>('/bff/api/payments/payee-confirmations',
        { bsb: normalisedBsb, accountNumber: toAccount.trim(), accountName: payeeName.trim() });
      setConfirmation({ ...result, checkedFor: payeeKey });
    } catch (e) {
      setConfirmationError(e instanceof Error ? e.message : 'Unable to confirm the payee.');
    } finally {
      setChecking(false);
    }
  };

  const applyHeldName = (name: string) => {
    setPayeeName(name);
    // The bank's own name for the account is, by definition, a match.
    setConfirmation({ result: 'MATCH', accountNameHeld: null, checkedFor: `${normalisedBsb}|${toAccount.trim()}|${name.trim().toLowerCase()}` });
  };

  const choosePayer = (account: PayerAccount) => {
    setPayer(account);
    publishSelection(
      [{ title: 'Customer Name', value: account.holderName }, { title: 'Customer Number', value: account.customerNumber }],
      [{ title: 'Paying Account', value: `${account.bsb} ${account.accountNumber}` }],
    );
  };

  // ---- validation (the API checks everything again; this explains it early)
  const amountValue = AMOUNT_PATTERN.test(amount.trim()) ? Number(amount.trim()) : NaN;
  const amountValid = Number.isFinite(amountValue) && amountValue > 0 && (!policy || amountValue <= policy.maxAmount);
  const insufficient = !!payer && amountValid && amountValue > payer.available;
  const needsApproval = !!policy && amountValid && amountValue > policy.approvalThreshold;
  const sameAccount = !!payer && normalisedBsb === payer.bsb && toAccount.trim() === payer.accountNumber;
  const payeeConfirmed = !!currentConfirmation && (currentConfirmation.result === 'MATCH' || mismatchConfirmed);
  const referenceValid = reference.trim().length <= (policy?.referenceMaxLength ?? 35);

  const blockers = useMemo(() => [
    !payer && 'Choose the paying account.',
    !bsbInfo && 'Enter a valid payee BSB.',
    !accountValid && 'Enter the payee account number (5 to 9 digits).',
    !payeeName.trim() && 'Enter the payee account name.',
    sameAccount && 'The payee account must differ from the paying account.',
    bsbInfo && accountValid && payeeName.trim() && !payeeConfirmed && 'Confirm the payee (Confirmation of Payee).',
    !amountValid && 'Enter an amount in dollars and cents.',
    insufficient && 'The amount is more than the available funds.',
    !referenceValid && 'The reference is too long.',
  ].filter(Boolean) as string[], [payer, bsbInfo, accountValid, payeeName, sameAccount, payeeConfirmed, amountValid, insufficient, referenceValid]);

  const transfer = async () => {
    if (!payer || blockers.length > 0) return;
    setSubmitting(true);
    setError(null);
    try {
      const result = await postJson<{ paymentId: number }>('/bff/api/payments', {
        customerNumber: payer.customerNumber,
        fromBsb: payer.bsb,
        fromAccountNumber: payer.accountNumber,
        payeeName: payeeName.trim(),
        toBsb: normalisedBsb,
        toAccountNumber: toAccount.trim(),
        amount: amountValue,
        reference: reference.trim() || null,
      }, { 'Idempotency-Key': idempotencyKey });
      setUnsavedChanges(false);
      router.push(`/v1/payments/view-details/?paymentId=${result.paymentId}`);
    } catch (e) {
      // The same Idempotency-Key is kept: trying again can never pay twice.
      setError(e instanceof Error ? e.message : 'The payment could not be started.');
      setSubmitting(false);
    }
  };

  return (
    <MfeShell title="New payment" subtitle="Payments">
      <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_380px]">
        <div className="flex flex-col gap-6">
          {/* 1 - From */}
          <Card>
            <CardHeader icon={<UserRound />} title="1 · From" description="Find the customer, then choose the account the payment comes from." />
            <CardContent className="space-y-4">
              {payer ? (
                <div className="flex items-center justify-between gap-4 rounded-control border border-accent-200 bg-accent-50/50 px-4 py-3">
                  <div className="min-w-0">
                    <p className="font-medium text-ink">{payer.holderName} <span className="font-mono text-xs text-ink-faint">{payer.customerNumber}</span></p>
                    <p className="font-mono text-[13px] text-ink-muted">{payer.bsb} {payer.accountNumber} · {productLabel(payer.product)}</p>
                  </div>
                  <div className="text-right">
                    <p className="text-xs uppercase tracking-wide text-ink-faint">Available</p>
                    <p className="font-semibold text-ink">{formatMoney(payer.available)}</p>
                  </div>
                  <Button variant="secondary" size="sm" onClick={() => { setPayer(null); setSearch(''); }}>Change</Button>
                </div>
              ) : (
                <>
                  <Field label="Customer" htmlFor="payer-search" hint="Name or customer number (CUST-…), at least 2 characters. Only active accounts of your branch.">
                    <div className="relative">
                      <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-ink-faint" aria-hidden="true" />
                      <Input id="payer-search" className="pl-9" value={search} onChange={e => setSearch(e.target.value)} placeholder="e.g. Jason or CUST-100002" autoComplete="off" />
                    </div>
                  </Field>
                  {searchError && <Alert tone="danger">{searchError}</Alert>}
                  {accounts && accounts.length === 0 && <p className="text-sm text-ink-muted">No active account of your branch matches “{search.trim()}”.</p>}
                  {accounts && accounts.length > 0 && (
                    <ul className="divide-y divide-line overflow-hidden rounded-control border border-line" aria-label="Matching accounts">
                      {accounts.map(a => (
                        <li key={`${a.bsb}-${a.accountNumber}`}>
                          <button type="button" onClick={() => choosePayer(a)}
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

          {/* 2 - To */}
          <Card>
            <CardHeader icon={<Building2 />} title="2 · To" description="The payee's account, exactly as the customer gives it." />
            <CardContent className="space-y-4">
              <div className="grid gap-4 sm:grid-cols-[160px_minmax(0,1fr)]">
                <Field label="BSB" htmlFor="payee-bsb" required hint="6 digits, e.g. 063-019.">
                  <Input id="payee-bsb" value={bsb} inputMode="numeric" maxLength={7} placeholder="000-000"
                    onChange={e => setBsb(e.target.value)} onBlur={() => setBsb(normaliseBsb(bsb))} />
                </Field>
                <div className="flex items-end pb-1">
                  {bsbInfo && (
                    <p className="flex items-center gap-2 text-sm text-ink">
                      <BadgeCheck className="size-4 text-success-700" aria-hidden="true" />
                      <span><span className="font-medium">{bsbInfo.bank}</span> · {bsbInfo.branch}</span>
                    </p>
                  )}
                  {bsbError && <p className="text-sm text-danger-700">{bsbError}</p>}
                </div>
              </div>
              <div className="grid gap-4 sm:grid-cols-[200px_minmax(0,1fr)]">
                <Field label="Account number" htmlFor="payee-account" required hint="5 to 9 digits.">
                  <Input id="payee-account" value={toAccount} inputMode="numeric" maxLength={9}
                    onChange={e => setToAccount(e.target.value.replace(/\D/g, ''))} />
                </Field>
                <Field label="Account name" htmlFor="payee-name" required hint="As the payee's bank holds it.">
                  <Input id="payee-name" value={payeeName} maxLength={140} autoComplete="off"
                    onChange={e => setPayeeName(e.target.value)} onBlur={() => void checkPayee()} />
                </Field>
              </div>

              {sameAccount && <Alert tone="danger">The payee account is the paying account.</Alert>}
              {confirmationError && <Alert tone="danger">{confirmationError}</Alert>}

              {!currentConfirmation && canCheckPayee && (
                <Button variant="secondary" size="sm" onClick={() => void checkPayee()} disabled={checking}>
                  {checking ? 'Checking…' : 'Check payee name'}
                </Button>
              )}
              {currentConfirmation?.result === 'MATCH' && (
                <Alert tone="success" title="Confirmation of Payee: match">The name matches the account at the payee's bank.</Alert>
              )}
              {currentConfirmation?.result === 'CLOSE_MATCH' && (
                <Alert tone="warning" title="Confirmation of Payee: close match">
                  <p>The payee's bank holds the name <strong>“{currentConfirmation.accountNameHeld}”</strong>.</p>
                  <div className="mt-2 flex flex-wrap items-center gap-3">
                    {currentConfirmation.accountNameHeld && (
                      <Button size="sm" variant="secondary" onClick={() => applyHeldName(currentConfirmation.accountNameHeld!)}>
                        Use “{currentConfirmation.accountNameHeld}”
                      </Button>
                    )}
                    <label className="flex items-center gap-2 text-[13px]">
                      <input type="checkbox" checked={mismatchConfirmed} onChange={e => setMismatchConfirmed(e.target.checked)} />
                      Confirmed with the customer: keep “{payeeName.trim()}”
                    </label>
                  </div>
                </Alert>
              )}
              {currentConfirmation?.result === 'NO_MATCH' && (
                <Alert tone="danger" title="Confirmation of Payee: no match">
                  <p>The name does not match this account. Check the details with the customer: money sent to the wrong account is hard to recover, and a mismatch is a common sign of a scam.</p>
                  <label className="mt-2 flex items-center gap-2 text-[13px]">
                    <input type="checkbox" checked={mismatchConfirmed} onChange={e => setMismatchConfirmed(e.target.checked)} />
                    I have confirmed the details with the customer and want to continue
                  </label>
                </Alert>
              )}
            </CardContent>
          </Card>

          {/* 3 - Amount */}
          <Card>
            <CardHeader icon={<CircleDollarSign />} title="3 · Amount" description="In Australian dollars; the reference appears on the payee's statement." />
            <CardContent className="grid gap-4 sm:grid-cols-[200px_minmax(0,1fr)]">
              <Field label="Amount (AUD)" htmlFor="amount" required
                hint={policy ? `Above ${formatMoney(policy.approvalThreshold)} a payments officer approves.` : undefined}>
                <Input id="amount" value={amount} inputMode="decimal" placeholder="0.00"
                  onChange={e => setAmount(e.target.value.replace(/[^\d.]/g, ''))} />
              </Field>
              <Field label="Reference" htmlFor="reference" hint={`Optional, up to ${policy?.referenceMaxLength ?? 35} characters.`}>
                <Input id="reference" value={reference} maxLength={policy?.referenceMaxLength ?? 35} onChange={e => setReference(e.target.value)} />
              </Field>
              {insufficient && payer && (
                <Alert tone="danger" className="sm:col-span-2">Insufficient available funds: {formatMoney(payer.available)} available.</Alert>
              )}
              {needsApproval && !insufficient && (
                <Alert tone="info" className="sm:col-span-2">This amount needs a payments officer's approval: the funds are reserved now and sent after approval.</Alert>
              )}
            </CardContent>
          </Card>
        </div>

        {/* 4 - Review */}
        <Card className="lg:sticky lg:top-6">
          <CardHeader icon={<ArrowRightLeft />} title="4 · Review" description="Read the payment back to the customer before you transfer." />
          <CardContent className="space-y-3 text-sm">
            <Row label="From" value={payer ? `${payer.holderName} · ${payer.bsb} ${payer.accountNumber}` : '—'} />
            <Row label="To" value={bsbInfo && accountValid ? `${payeeName.trim() || '—'} · ${normalisedBsb} ${toAccount.trim()}` : '—'} />
            <Row label="Payee bank" value={bsbInfo?.bank ?? '—'} />
            <Row label="Amount" value={amountValid ? formatMoney(amountValue) : '—'} strong />
            <Row label="Reference" value={reference.trim() || '—'} />
            {needsApproval && <Row label="Approval" value="Needs a payments officer" />}
            {error && <Alert tone="danger">{error}</Alert>}
            {blockers.length > 0 && (
              <ul className="list-inside list-disc space-y-0.5 text-[13px] text-ink-muted" aria-label="Still to do">
                {blockers.map(b => <li key={b}>{b}</li>)}
              </ul>
            )}
          </CardContent>
          <CardFooter>
            <Button className="w-full" size="lg" onClick={() => void transfer()} disabled={blockers.length > 0 || submitting}>
              {submitting ? 'Starting the payment…' : 'Transfer'}
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