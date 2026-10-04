'use client';

import { CheckCircle2, Circle, FileText, IdCard, Landmark, MapPin, Send, UploadCloud, UserRound } from 'lucide-react';
import { useEffect, useState } from 'react';
import { getCsrfToken, getJson, postForm } from '../lib/api';
import { publishWorkspaceContext, setUnsavedChanges } from './MfeShell';
import { Button } from './ui/button';
import { Card, CardContent, CardHeader } from './ui/card';
import { cn } from './ui/cn';
import { Alert, Badge } from './ui/feedback';
import { Field, Input, Select } from './ui/form';

interface Result {
  customerId: number;
  customerNumber: string;
  applicationId: number;
  applicationNumber: string;
  status: string;
  documents: { documentType: string; documentId: string }[];
}

interface CustomerDetails {
  customerId: number;
  customerNumber: string;
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  customerType: string;
  status: string;
  branchId: number | null;
  version: number;
}

interface WorkspaceContextItem {
  title: string;
  value: string | number | boolean;
}

interface WorkspaceContext {
  persistentContext: WorkspaceContextItem[];
  currentContext: WorkspaceContextItem[];
  retainedContext: WorkspaceContextItem[];
}

interface FormValues {
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
}

interface AddressValues {
  addressLine1: string;
  addressLine2: string;
  city: string;
  state: string;
  postalCode: string;
  countryCode: string;
}

const emptyForm: FormValues = { firstName: '', lastName: '', email: '', phoneNumber: '' };
const emptyAddress: AddressValues = { addressLine1: '', addressLine2: '', city: '', state: '', postalCode: '', countryCode: 'AU' };

const AUSTRALIAN_STATES = [
  ['NSW', 'New South Wales'],
  ['VIC', 'Victoria'],
  ['QLD', 'Queensland'],
  ['SA', 'South Australia'],
  ['WA', 'Western Australia'],
  ['TAS', 'Tasmania'],
  ['ACT', 'Australian Capital Territory'],
  ['NT', 'Northern Territory'],
] as const;

function getCustomerId(context: WorkspaceContext): number | null {
  // Customer is the persistent/root business context. Prefer it over
  // current/retained entries so a stale subordinate context can never
  // override the current customer identity.
  const item = context.persistentContext.find(
    x => x.title.toLowerCase() === 'customer id',
  );

  if (item === undefined) return null;

  const id = Number(item.value);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

function formatSize(bytes: number) {
  return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

interface CustomerOnboardingFormProps {
  onApplicationCreated?: () => void | Promise<void>;
}

export function CustomerOnboardingForm({
  onApplicationCreated,
}: CustomerOnboardingFormProps) {
  const [busy, setBusy] = useState(false);
  const [loadingCustomer, setLoadingCustomer] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<Result | null>(null);
  const [selectedCustomer, setSelectedCustomer] = useState<CustomerDetails | null>(null);
  const [formValues, setFormValues] = useState<FormValues>(emptyForm);
  const [address, setAddress] = useState<AddressValues>(emptyAddress);
  const [kycFile, setKycFile] = useState<File | null>(null);
  const [taxFile, setTaxFile] = useState<File | null>(null);

  useEffect(() => {
    const handleContextHandoff = (event: Event) => {
      const context = (event as CustomEvent<WorkspaceContext>).detail;
      if (!context) return;

      const customerId = getCustomerId(context);
      if (customerId === null) return;

      let cancelled = false;

      setLoadingCustomer(true);
      setError(null);
      setResult(null);
      setUnsavedChanges(false);

      getJson<CustomerDetails>(`/bff/api/customers/${customerId}`)
        .then(customer => {
          if (cancelled) return;

          setSelectedCustomer(customer);
          setFormValues({
            firstName: customer.firstName,
            lastName: customer.lastName,
            email: customer.email,
            phoneNumber: customer.phoneNumber,
          });

          // Loading the authoritative customer data is not itself an edit.
          setUnsavedChanges(false);
        })
        .catch(e => {
          if (cancelled) return;
          setSelectedCustomer(null);
          setError(
            e instanceof Error
              ? `Unable to load customer ${customerId}: ${e.message}`
              : `Unable to load customer ${customerId}.`,
          );
        })
        .finally(() => {
          if (!cancelled) setLoadingCustomer(false);
        });

      return () => {
        cancelled = true;
      };
    };

    window.addEventListener('bss-context-handoff', handleContextHandoff);
    return () => window.removeEventListener('bss-context-handoff', handleContextHandoff);
  }, []);

  function updateField(field: keyof FormValues, value: string) {
    setFormValues(previous => ({ ...previous, [field]: value }));
    setUnsavedChanges(true);
  }

  function updateAddress(field: keyof AddressValues, value: string) {
    setAddress(previous => ({ ...previous, [field]: value }));
    setUnsavedChanges(true);
  }

  function selectFile(setter: (file: File | null) => void, file: File | null) {
    setter(file);
    setUnsavedChanges(true);
  }

  const detailsComplete = Object.values(formValues).every(value => value.trim() !== '');
  const addressComplete = selectedCustomer !== null ||
    (address.addressLine1.trim() !== '' && address.city.trim() !== '' && address.state.trim() !== '' &&
      address.postalCode.trim() !== '' && /^[A-Za-z]{2}$/.test(address.countryCode.trim()));
  const isAustralia = address.countryCode.trim().toUpperCase() === 'AU';

  async function submit(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setResult(null);

    const formElement = event.currentTarget;
    const form = new FormData(formElement);

    if (selectedCustomer) {
      form.set('customerId', String(selectedCustomer.customerId));
    }

    const kyc = form.get('kycProof') as File | null;
    const tax = form.get('taxProof') as File | null;

    if (!kyc?.name || !tax?.name) {
      setError('Please select both PDF documents.');
      return;
    }

    if (
      !kyc.name.toLowerCase().endsWith('.pdf') ||
      !tax.name.toLowerCase().endsWith('.pdf')
    ) {
      setError('Both documents must be PDF files.');
      return;
    }

    setBusy(true);

    try {
      const csrfToken = await getCsrfToken();
      const response = await postForm<Result>(
        '/bff/api/onboarding/applications',
        form,
        csrfToken,
      );

      setResult(response.data);

      // Publish the authoritative result of the successful onboarding
      // operation back to the Shell. The Shell treats this as opaque
      // workspace context; the Customer Onboarding MFE owns the meaning.
      publishWorkspaceContext({
        persistentContext: [
          { title: 'Customer ID', value: response.data.customerId },
          { title: 'Customer Number', value: response.data.customerNumber },
        ],
        currentContext: [
          { title: 'Application ID', value: response.data.applicationId },
          { title: 'Application Number', value: response.data.applicationNumber },
          { title: 'Status', value: response.data.status },
        ],
        retainedContext: [],
      });

      await onApplicationCreated?.();

      formElement.reset();
      setFormValues(emptyForm);
      setAddress(emptyAddress);
      setKycFile(null);
      setTaxFile(null);
      setSelectedCustomer(null);
      setUnsavedChanges(false);
    } catch (e) {
      setError(
        e instanceof Error
          ? e.message
          : 'Onboarding submission failed.',
      );
    } finally {
      setBusy(false);
    }
  }

  const checklist = [
    { label: 'Customer details', done: detailsComplete },
    { label: 'Residential address', done: addressComplete },
    { label: 'Identity proof (PDF)', done: kycFile !== null },
    { label: 'Tax proof (PDF)', done: taxFile !== null },
  ];
  const readyToSubmit = checklist.every(item => item.done);

  return (
    <form onSubmit={submit} className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
      <div className="flex min-w-0 flex-col gap-6">
        {error && <Alert tone="danger" title="Submission failed">{error}</Alert>}

        {result && (
          <Alert tone="success" title="Application submitted">
            Customer <strong>{result.customerNumber}</strong> · application <strong>{result.applicationNumber}</strong>.
            Both documents were stored by Documents Management and KYC review has been requested.
          </Alert>
        )}

        <Card>
          <CardHeader
            icon={<UserRound />}
            title="Customer details"
            description={selectedCustomer ? 'An existing customer is selected; these details come from their record.' : 'The applicant’s legal name and contact details.'}
            actions={selectedCustomer && <Badge tone="info">Existing · {selectedCustomer.customerNumber}</Badge>}
          />
          <CardContent>
            {loadingCustomer ? (
              <p className="text-sm text-ink-muted">Loading the selected customer…</p>
            ) : (
              <div className="grid gap-x-5 gap-y-4 sm:grid-cols-12">
                <Field label="First name" htmlFor="firstName" required className="sm:col-span-6">
                  <Input id="firstName" name="firstName" required maxLength={100} autoComplete="given-name"
                    value={formValues.firstName} onChange={e => updateField('firstName', e.target.value)} />
                </Field>
                <Field label="Last name" htmlFor="lastName" required className="sm:col-span-6">
                  <Input id="lastName" name="lastName" required maxLength={100} autoComplete="family-name"
                    value={formValues.lastName} onChange={e => updateField('lastName', e.target.value)} />
                </Field>
                <Field label="Email" htmlFor="email" required className="sm:col-span-7">
                  <Input id="email" name="email" type="email" required maxLength={254} autoComplete="email"
                    value={formValues.email} onChange={e => updateField('email', e.target.value)} />
                </Field>
                <Field label="Phone number" htmlFor="phoneNumber" required className="sm:col-span-5" hint="Australian format, e.g. +61 4xx xxx xxx">
                  <Input id="phoneNumber" name="phoneNumber" type="tel" required maxLength={30} autoComplete="tel"
                    value={formValues.phoneNumber} onChange={e => updateField('phoneNumber', e.target.value)} />
                </Field>
              </div>
            )}
          </CardContent>
        </Card>

        {!selectedCustomer && (
          <Card>
            <CardHeader
              icon={<MapPin />}
              title="Primary residential address"
              description="Determines the serving branch. It must be in your branch’s city."
            />
            <CardContent>
              <div className="grid gap-x-5 gap-y-4 sm:grid-cols-12">
                <Field label="Address line 1" htmlFor="addressLine1" required className="sm:col-span-7">
                  <Input id="addressLine1" name="addressLine1" required maxLength={200} autoComplete="address-line1"
                    value={address.addressLine1} onChange={e => updateAddress('addressLine1', e.target.value)} />
                </Field>
                <Field label="Address line 2" htmlFor="addressLine2" className="sm:col-span-5" hint="Unit, level or building (optional)">
                  <Input id="addressLine2" name="addressLine2" maxLength={200} autoComplete="address-line2"
                    value={address.addressLine2} onChange={e => updateAddress('addressLine2', e.target.value)} />
                </Field>
                <Field label="City / suburb" htmlFor="city" required className="sm:col-span-5">
                  <Input id="city" name="city" required maxLength={100} autoComplete="address-level2"
                    value={address.city} onChange={e => updateAddress('city', e.target.value)} />
                </Field>
                <Field label="State" htmlFor="state" required className="sm:col-span-3">
                  {isAustralia ? (
                    <Select id="state" name="state" required value={address.state} onChange={e => updateAddress('state', e.target.value)}>
                      <option value="">Select…</option>
                      {AUSTRALIAN_STATES.map(([code, name]) => <option key={code} value={code}>{name}</option>)}
                    </Select>
                  ) : (
                    <Input id="state" name="state" required maxLength={100} autoComplete="address-level1"
                      value={address.state} onChange={e => updateAddress('state', e.target.value)} />
                  )}
                </Field>
                <Field label="Postcode" htmlFor="postalCode" required className="sm:col-span-2">
                  <Input id="postalCode" name="postalCode" required maxLength={20} inputMode={isAustralia ? 'numeric' : undefined}
                    autoComplete="postal-code" value={address.postalCode} onChange={e => updateAddress('postalCode', e.target.value)} />
                </Field>
                <Field label="Country" htmlFor="countryCode" required className="sm:col-span-2" hint="ISO code">
                  <Input id="countryCode" name="countryCode" required minLength={2} maxLength={2} pattern="[A-Za-z]{2}"
                    className="uppercase" value={address.countryCode} onChange={e => updateAddress('countryCode', e.target.value)} />
                </Field>
              </div>
            </CardContent>
          </Card>
        )}

        <Card>
          <CardHeader
            icon={<FileText />}
            title="KYC evidence"
            description="PDF only. Stored by Documents Management; the Customer Onboarding API never receives the files."
          />
          <CardContent>
            <div className="grid gap-4 md:grid-cols-2">
              <FileDrop id="kycProof" name="kycProof" label="Identity proof" description="Driver licence or passport"
                icon={<IdCard />} file={kycFile} onChange={file => selectFile(setKycFile, file)} />
              <FileDrop id="taxProof" name="taxProof" label="Tax proof" description="ATO Notice of Assessment"
                icon={<Landmark />} file={taxFile} onChange={file => selectFile(setTaxFile, file)} />
            </div>
          </CardContent>
        </Card>
      </div>

      <aside className="xl:sticky xl:top-6">
        <Card>
          <CardHeader title="Application summary" description={selectedCustomer ? `For ${selectedCustomer.firstName} ${selectedCustomer.lastName}` : 'New customer onboarding'} />
          <CardContent className="space-y-3">
            <ul className="space-y-2.5">
              {checklist.map(item => (
                <li key={item.label} className="flex items-center gap-2.5 text-sm">
                  {item.done
                    ? <CheckCircle2 className="size-4 text-success-700" aria-hidden="true" />
                    : <Circle className="size-4 text-ink-faint" aria-hidden="true" />}
                  <span className={cn(item.done ? 'text-ink' : 'text-ink-muted')}>{item.label}</span>
                  <span className="sr-only">{item.done ? '(complete)' : '(incomplete)'}</span>
                </li>
              ))}
            </ul>
            <p className="border-t border-line pt-3 text-xs leading-5 text-ink-muted">
              Submitting creates the application and requests KYC review. A KYC officer of your branch will verify both documents.
            </p>
          </CardContent>
          <div className="border-t border-line px-6 py-4">
            <Button type="submit" variant="accent" size="lg" className="w-full" disabled={busy || loadingCustomer}>
              <Send aria-hidden="true" />
              {busy ? 'Submitting…' : 'Submit application'}
            </Button>
            {!readyToSubmit && !busy && (
              <p className="mt-2 text-center text-xs text-ink-faint">Complete the checklist to submit.</p>
            )}
          </div>
        </Card>
      </aside>
    </form>
  );
}

/** A labelled PDF picker that shows the chosen file. The native input stays keyboard-accessible. */
function FileDrop({
  id,
  name,
  label,
  description,
  icon,
  file,
  onChange,
}: {
  id: string;
  name: string;
  label: string;
  description: string;
  icon: React.ReactNode;
  file: File | null;
  onChange: (file: File | null) => void;
}) {
  return (
    <div>
      <input
        id={id}
        name={name}
        type="file"
        accept="application/pdf,.pdf"
        required
        className="peer sr-only"
        onChange={e => onChange(e.target.files?.[0] ?? null)}
      />
      <label
        htmlFor={id}
        className={cn(
          'flex cursor-pointer items-center gap-4 rounded-card border border-dashed px-4 py-4 transition-colors peer-focus-visible:ring-3 peer-focus-visible:ring-accent-100',
          file ? 'border-success-200 bg-success-50/60' : 'border-line-strong bg-subtle hover:border-accent-500 hover:bg-accent-50/50',
        )}
      >
        <span className={cn('flex size-10 shrink-0 items-center justify-center rounded-lg [&_svg]:size-5', file ? 'bg-success-50 text-success-700' : 'bg-surface text-brand-600 shadow-sm')}>
          {file ? <CheckCircle2 /> : icon}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block text-sm font-semibold text-ink">
            {label} <span className="text-danger-700" aria-hidden="true">*</span>
          </span>
          <span className="block truncate text-xs text-ink-muted">
            {file ? `${file.name} · ${formatSize(file.size)}` : description}
          </span>
        </span>
        <span className="flex shrink-0 items-center gap-1.5 text-xs font-semibold text-accent-700">
          <UploadCloud className="size-4" aria-hidden="true" />
          {file ? 'Change' : 'Choose PDF'}
        </span>
      </label>
    </div>
  );
}