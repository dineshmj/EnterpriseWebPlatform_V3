'use client';

import { Coins, FileSignature, ReceiptText, Search, UserRound } from 'lucide-react';
import { FormEvent, useCallback, useEffect, useState } from 'react';
import { formatAmount, formatTime, MfeShell, signInAgain } from '@/app/components/MfeShell';
import { Button } from '@/app/components/ui/button';
import { Card, CardContent, CardHeader } from '@/app/components/ui/card';
import { DescriptionList, Table, Td, Th } from '@/app/components/ui/data';
import { Alert, Badge, EmptyState } from '@/app/components/ui/feedback';
import { Field, Input, Select } from '@/app/components/ui/form';
import type { ActionResult, CustomerAtCounter, CustomerMatch, GoldApplication } from '@/lib/partner-products-types';
import { loadApplications, loadCustomer, recordFormVii, recordTransferSlip, searchCustomers, sendFormVii } from '../../actions';

/** Today in Sydney (yyyy-mm-dd) - the date a transfer slip must carry. */
const sydneyToday = () => new Intl.DateTimeFormat('en-CA', { timeZone: 'Australia/Sydney' }).format(new Date());

const applicationTone = (status: string) =>
  status === 'OPENED' ? 'success' : status === 'REFUSED' ? 'danger' : status === 'SENT' ? 'info' : 'warning';

const applicationLabel: Record<string, string> = {
  RECORDED: 'Recorded - to be sent to the Partner Bank',
  SENT: 'Sent - no answer yet from the Partner Bank',
  OPENED: 'Account open',
  REFUSED: 'Refused by the Partner Bank',
};

/**
 * The counter's Digital Gold (EWP as the Partner Bank's distributor): find the customer, then
 * record the paper form they signed - Form VII once, to open their Digital Gold account; a
 * transfer authorization slip for each purchase. The Partner Bank is the system of record.
 */
export default function DigitalGoldPage() {
  const [search, setSearch] = useState('');
  const [matches, setMatches] = useState<CustomerMatch[] | null>(null);
  const [customer, setCustomer] = useState<CustomerAtCounter | null>(null);
  const [applications, setApplications] = useState<GoldApplication[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const handle = useCallback(<T,>(result: ActionResult<T>): T | null => {
    if (result.ok) return result.data;
    if (result.signedOut) signInAgain();
    else setError(result.error);
    return null;
  }, []);

  const refreshApplications = useCallback(async () => {
    const list = handle(await loadApplications());
    if (list) setApplications(list);
  }, [handle]);

  useEffect(() => { void refreshApplications(); }, [refreshApplications]);

  const find = async (event: FormEvent) => {
    event.preventDefault();
    setError(null); setNotice(null); setCustomer(null);
    setBusy(true);
    const found = handle(await searchCustomers(search));
    setBusy(false);
    setMatches(found);
  };

  const open = async (customerId: number) => {
    setError(null); setNotice(null);
    setBusy(true);
    const loaded = handle(await loadCustomer(customerId));
    setBusy(false);
    if (loaded) { setCustomer(loaded); setMatches(null); }
  };

  const recorded = async (message: string) => {
    setNotice(message);
    if (customer) await open(customer.customerId);
    setNotice(message);
    await refreshApplications();
  };

  return (
    <MfeShell title="Digital Gold">
      <div className="flex flex-col gap-6">
        <Alert tone="info" title="EWP acts for the Partner Bank here (distributor)">
          The customer signs the paper form at the counter: <strong>Form VII</strong> once, to open a Digital Gold account at the Partner Bank,
          and a <strong>transfer authorization slip</strong> for each purchase. Record the form here with its scan. The customer never signs in at the
          Partner Bank on this computer; the Partner Bank keeps the account and the gold.
        </Alert>

        {error && <Alert tone="danger">{error}</Alert>}
        {notice && <Alert tone="success">{notice}</Alert>}

        <Card>
          <CardHeader title="Find the customer" description="Customers of your branch, by name or customer number." icon={<Search />} />
          <CardContent className="p-6">
            <form onSubmit={find} className="flex flex-wrap items-end gap-3">
              <Field label="Name or customer number" htmlFor="search" className="min-w-72 flex-1">
                <Input id="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="e.g. Camilla or CUST-100009" maxLength={100} />
              </Field>
              <Button type="submit" disabled={busy || search.trim().length < 2}>Search</Button>
            </form>
            {matches && (
              matches.length === 0
                ? <EmptyState icon={<UserRound />} title="No customer found">Check the spelling, or search by the customer number.</EmptyState>
                : (
                  <div className="mt-4">
                    <Table>
                      <thead><tr><Th>Customer</Th><Th>Name</Th><Th>Status</Th><Th /></tr></thead>
                      <tbody>
                        {matches.map((m) => (
                          <tr key={m.customerId}>
                            <Td className="font-mono text-xs">{m.customerNumber}</Td>
                            <Td>{m.name}</Td>
                            <Td><Badge tone={m.status === 'ACTIVE' ? 'success' : 'neutral'}>{m.status}</Badge></Td>
                            <Td className="text-right"><Button size="sm" variant="secondary" onClick={() => open(m.customerId)}>Select</Button></Td>
                          </tr>
                        ))}
                      </tbody>
                    </Table>
                  </div>
                )
            )}
          </CardContent>
        </Card>

        {customer && <CustomerPanel customer={customer} onRecorded={recorded} onError={setError} />}

        <Card>
          <CardHeader title="Form VII recorded at your branch" description="The latest applications; those still to be sent to the Partner Bank first." icon={<FileSignature />} />
          {applications.length === 0
            ? <EmptyState icon={<FileSignature />} title="No Form VII recorded yet" />
            : (
              <Table>
                <thead><tr><Th>Application</Th><Th>Customer</Th><Th>Form VII</Th><Th>Status</Th><Th>Recorded</Th></tr></thead>
                <tbody>
                  {applications.map((a) => (
                    <tr key={a.number}>
                      <Td className="font-mono text-xs">{a.number}</Td>
                      <Td>{a.customerName} <span className="font-mono text-xs text-ink-muted">{a.customerNumber}</span></Td>
                      <Td className="font-mono text-xs">{a.formNumber}</Td>
                      <Td><Badge tone={applicationTone(a.status)} dot>{applicationLabel[a.status] ?? a.status}</Badge></Td>
                      <Td className="text-xs text-ink-muted">{formatTime(a.recordedAt)} by {a.recordedByLanId}</Td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            )}
        </Card>
      </div>
    </MfeShell>
  );
}

function CustomerPanel({ customer, onRecorded, onError }: {
  customer: CustomerAtCounter;
  onRecorded: (message: string) => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const application = customer.digitalGold.application;
  const active = application && application.status !== 'REFUSED' ? application : null;
  const [sending, setSending] = useState(false);

  const send = async (number: string) => {
    onError(null);
    setSending(true);
    const result = await sendFormVii(number);
    setSending(false);
    if (result.ok) {
      await onRecorded(result.data.status === 'OPENED'
        ? `The Partner Bank opened Digital Gold account ${result.data.partnerAccountNumber}.`
        : `The Partner Bank refused the application: ${result.data.statusReason}`);
    } else if (result.signedOut) signInAgain();
    // Reload first (the status may now be SENT), then show why: the reload clears old messages.
    else { await onRecorded(''); onError(result.error); }
  };

  return (
    <>
      <Card>
        <CardHeader title={customer.name} description={`Customer ${customer.customerNumber} - EWP's record`} icon={<UserRound />}
          actions={<Badge tone={customer.kycVerified ? 'success' : 'warning'} dot>{customer.kycVerified ? 'Onboarding complete (KYC)' : customer.status}</Badge>} />
        <CardContent className="p-6">
          <DescriptionList columns={3} items={[
            { label: 'Mobile on record', value: customer.mobileEnding ? `ending ${customer.mobileEnding}` : <span className="text-danger-700">not an Australian mobile</span> },
            { label: 'Paying accounts', value: customer.accountsAvailable ? `${customer.accounts.length} active` : 'not available now' },
            { label: 'Digital Gold', value: active ? <Badge tone={applicationTone(active.status)} dot>{applicationLabel[active.status]}</Badge> : 'no account yet' },
          ]} />
        </CardContent>
      </Card>

      {application?.status === 'REFUSED' && (
        <Alert tone="danger" title={`The Partner Bank refused Form VII ${application.formNumber}`}>{application.statusReason}</Alert>
      )}

      {!active && <FormViiCard customer={customer} onRecorded={onRecorded} onError={onError} />}

      {active && (
        <Card>
          <CardHeader title="Digital Gold account" description={`Form VII ${active.formNumber}, signed ${active.formSignedOn}`} icon={<Coins />} />
          <CardContent className="p-6">
            <DescriptionList columns={3} items={[
              { label: 'EWP reference', value: <span className="font-mono text-xs">{active.number}</span> },
              { label: 'Partner Bank account', value: active.partnerAccountNumber ?? 'not yet open' },
              { label: 'Recorded', value: `${formatTime(active.recordedAt)} by ${active.recordedByLanId}` },
            ]} />
            {(active.status === 'RECORDED' || active.status === 'SENT') && (
              <div className="mt-4 flex flex-wrap items-center gap-3">
                <Button onClick={() => send(active.number)} disabled={sending}>{sending ? 'Sending…' : active.status === 'SENT' ? 'Send again to the Partner Bank' : 'Send to the Partner Bank'}</Button>
                <span className="text-[13px] text-ink-muted">
                  {active.status === 'SENT'
                    ? 'No answer came last time. Sending again sends the same signed instruction: the Partner Bank opens one account only.'
                    : 'EWP sends a signed instruction over mutual TLS; the Partner Bank opens the account and tells the customer by SMS.'}
                </span>
              </div>
            )}
            {active.status === 'OPENED' && <HoldingView holding={customer.holding} />}
          </CardContent>
        </Card>
      )}

      {active?.status === 'OPENED' && <TransferSlipCard customer={customer} onRecorded={onRecorded} onError={onError} />}

      {customer.digitalGold.purchases.length > 0 && (
        <Card>
          <CardHeader title="Transfer authorization slips" icon={<ReceiptText />} />
          <Table>
            <thead><tr><Th>Purchase</Th><Th>Slip</Th><Th>Amount</Th><Th>From</Th><Th>Status</Th><Th>Recorded</Th></tr></thead>
            <tbody>
              {customer.digitalGold.purchases.map((p) => (
                <tr key={p.number}>
                  <Td className="font-mono text-xs">{p.number}</Td>
                  <Td className="font-mono text-xs">{p.slipNumber}</Td>
                  <Td>{formatAmount(p.amountAud, 'AUD')}</Td>
                  <Td className="font-mono text-xs">{p.debtorBsb} {p.debtorAccountNumber}</Td>
                  <Td><Badge tone="info" dot>{p.status}</Badge></Td>
                  <Td className="text-xs text-ink-muted">{formatTime(p.recordedAt)} by {p.recordedByLanId}</Td>
                </tr>
              ))}
            </tbody>
          </Table>
        </Card>
      )}
    </>
  );
}

/** The customer's gold, as the Partner Bank keeps it (read from it on every look). */
function HoldingView({ holding }: { holding: CustomerAtCounter['holding'] }) {
  if (!holding) return <Alert tone="warning" className="mt-4">The Partner Bank could not be reached for the holding. Try again shortly.</Alert>;
  const h = holding.holding;
  return (
    <div className="mt-6 flex flex-col gap-4">
      <DescriptionList columns={3} items={[
        { label: 'Gold held', value: `${h.grams.toFixed(4)} g` },
        { label: 'Value today', value: `${formatAmount(h.valueAud, 'AUD')} at ${formatAmount(h.pricePerGram, 'AUD')}/g` },
        { label: 'Priced at', value: formatTime(h.pricedAt) },
      ]} />
      <div>
        <p className="text-xs font-medium uppercase tracking-wide text-ink-faint">At the Partner Bank</p>
        <ul className="mt-2 flex flex-col gap-1.5 text-[13px] text-ink">
          {h.activity.slice(0, 8).map((a, i) => (
            <li key={i}><span className="text-ink-muted">{formatTime(a.at)}</span> - {a.text}</li>
          ))}
        </ul>
      </div>
    </div>
  );
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The customer's identity is never typed here: name, mobile and KYC status come from
// │ EWP's record (read again on the server). What the staff member enters is what is ON
// │ the paper form (its number, the date it was signed, the date of birth) and what they
// │ checked (photo ID, signature) - plus the scan, the evidence the Partner Bank relies on.
// └──────────────────────────────────────────────────────────────────────────────────────
function FormViiCard({ customer, onRecorded, onError }: {
  customer: CustomerAtCounter;
  onRecorded: (message: string) => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const [busy, setBusy] = useState(false);
  const blocked = !customer.kycVerified
    ? 'Only a customer whose onboarding (KYC) is complete can apply.'
    : !customer.mobileEnding ? 'The customer\'s phone number on record is not an Australian mobile: correct the customer\'s record first.' : null;

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    onError(null);
    const form = new FormData(event.currentTarget);
    form.set('identityChecked', form.get('identityChecked') === 'on' ? 'true' : 'false');
    form.set('signatureChecked', form.get('signatureChecked') === 'on' ? 'true' : 'false');
    setBusy(true);
    const result = await recordFormVii(customer.customerId, form);
    setBusy(false);
    if (result.ok) await onRecorded(`Form VII ${result.data.formNumber} recorded (${result.data.number}).`);
    else if (result.signedOut) signInAgain();
    else onError(result.error);
  };

  return (
    <Card>
      <CardHeader title="Open a Digital Gold account - Form VII" description="The Partner Bank's application, signed by the customer at this counter." icon={<FileSignature />} />
      <CardContent className="p-6">
        {blocked ? <Alert tone="warning">{blocked}</Alert> : (
          <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
            <Field label="Form VII number" htmlFor="formNumber" required hint="As printed on the form.">
              <Input id="formNumber" name="formNumber" required maxLength={32} pattern="[A-Za-z0-9][A-Za-z0-9\-]{3,31}" />
            </Field>
            <Field label="Signed on" htmlFor="formSignedOn" required hint="Within the last 30 days.">
              <Input id="formSignedOn" name="formSignedOn" type="date" required max={sydneyToday()} defaultValue={sydneyToday()} />
            </Field>
            <Field label="Date of birth (on the form)" htmlFor="dateOfBirth" required>
              <Input id="dateOfBirth" name="dateOfBirth" type="date" required max={sydneyToday()} />
            </Field>
            <Field label="Scan of the signed form" htmlFor="scan" required hint="PDF, PNG or JPEG, at most 5 MB." className="sm:col-span-2">
              <Input id="scan" name="scan" type="file" required accept="application/pdf,image/png,image/jpeg" className="pt-2" />
            </Field>
            <div className="flex flex-col gap-2 text-sm text-ink sm:col-span-2 xl:col-span-3">
              <label className="flex items-center gap-2"><input type="checkbox" name="identityChecked" required /> I checked the date of birth against the customer&apos;s photo ID.</label>
              <label className="flex items-center gap-2"><input type="checkbox" name="signatureChecked" required /> I checked the customer&apos;s signature on Form VII.</label>
            </div>
            <div className="sm:col-span-2 xl:col-span-3"><Button type="submit" disabled={busy}>{busy ? 'Recording…' : 'Record Form VII'}</Button></div>
          </form>
        )}
      </CardContent>
    </Card>
  );
}

function TransferSlipCard({ customer, onRecorded, onError }: {
  customer: CustomerAtCounter;
  onRecorded: (message: string) => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const [busy, setBusy] = useState(false);

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    onError(null);
    const form = new FormData(event.currentTarget);
    form.set('signatureChecked', form.get('signatureChecked') === 'on' ? 'true' : 'false');
    setBusy(true);
    const result = await recordTransferSlip(customer.customerId, form);
    setBusy(false);
    if (result.ok) await onRecorded(`Transfer slip ${result.data.slipNumber} recorded (${result.data.number}).`);
    else if (result.signedOut) signInAgain();
    else onError(result.error);
  };

  return (
    <Card>
      <CardHeader title="Buy Digital Gold - transfer authorization slip" description="Signed today; it buys at today's price." icon={<ReceiptText />} />
      <CardContent className="p-6">
        {!customer.accountsAvailable || customer.accounts.length === 0
          ? <Alert tone="warning">No paying account can be shown for this customer now.</Alert>
          : (
            <form onSubmit={submit} className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">
              <Field label="Slip number" htmlFor="slipNumber" required>
                <Input id="slipNumber" name="slipNumber" required maxLength={32} pattern="[A-Za-z0-9][A-Za-z0-9\-]{3,31}" />
              </Field>
              <Field label="Amount (AUD)" htmlFor="amount" required>
                <Input id="amount" name="amount" required inputMode="decimal" pattern="\d{1,5}(\.\d{1,2})?" placeholder="20.00" />
              </Field>
              <Field label="Paying account" htmlFor="debtorAccountNumber" required>
                <Select id="debtorAccountNumber" name="debtorAccountNumber" required>
                  {customer.accounts.map((a) => (
                    <option key={a.accountNumber} value={a.accountNumber}>{a.bsb} {a.accountNumber} - {a.product} ({formatAmount(a.available, 'AUD')} available)</option>
                  ))}
                </Select>
              </Field>
              <Field label="Phone number on the slip" htmlFor="mobileOnSlip" required hint={`Must be the mobile on record (ending ${customer.mobileEnding}).`}>
                <Input id="mobileOnSlip" name="mobileOnSlip" required maxLength={30} />
              </Field>
              <Field label="Signed on" htmlFor="slipSignedOn" required hint="Today.">
                <Input id="slipSignedOn" name="slipSignedOn" type="date" required defaultValue={sydneyToday()} min={sydneyToday()} max={sydneyToday()} />
              </Field>
              <Field label="Scan of the signed slip" htmlFor="slipScan" required hint="PDF, PNG or JPEG, at most 5 MB.">
                <Input id="slipScan" name="scan" type="file" required accept="application/pdf,image/png,image/jpeg" className="pt-2" />
              </Field>
              <label className="flex items-center gap-2 text-sm text-ink sm:col-span-2 xl:col-span-3"><input type="checkbox" name="signatureChecked" required /> I checked the customer&apos;s signature on the slip.</label>
              <div className="sm:col-span-2 xl:col-span-3"><Button type="submit" disabled={busy}>{busy ? 'Recording…' : 'Record transfer slip'}</Button></div>
            </form>
          )}
      </CardContent>
    </Card>
  );
}