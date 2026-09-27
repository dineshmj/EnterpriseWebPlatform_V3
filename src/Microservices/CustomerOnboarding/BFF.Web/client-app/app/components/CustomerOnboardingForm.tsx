'use client';

import { useEffect, useState } from 'react';
import { getCsrfToken, getJson, postForm } from '../lib/api';
import { setUnsavedChanges } from './MfeShell';

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

const emptyForm: FormValues = {
  firstName: '',
  lastName: '',
  email: '',
  phoneNumber: '',
};

function getCustomerId(context: WorkspaceContext): number | null {
  const item = [
    ...context.currentContext,
    ...context.retainedContext,
    ...context.persistentContext,
  ].find(x => x.title.toLowerCase() === 'customer id');

  if (item === undefined) return null;

  const id = Number(item.value);
  return Number.isSafeInteger(id) && id > 0 ? id : null;
}

export function CustomerOnboardingForm({
  onApplicationCreated,
}: {
  onApplicationCreated?: () => void;
}) {
  const [busy, setBusy] = useState(false);
  const [loadingCustomer, setLoadingCustomer] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<Result | null>(null);
  const [selectedCustomer, setSelectedCustomer] = useState<CustomerDetails | null>(null);
  const [formValues, setFormValues] = useState<FormValues>(emptyForm);

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
      onApplicationCreated?.();
      formElement.reset();
      setFormValues(emptyForm);
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

  return (
    <form onSubmit={submit}>
      {loadingCustomer && (
        <div className="card">
          Loading selected customer details...
        </div>
      )}

      {selectedCustomer && (
        <div className="card">
          <strong>Existing customer selected:</strong>{' '}
          {selectedCustomer.customerNumber} — {selectedCustomer.firstName}{' '}
          {selectedCustomer.lastName}
        </div>
      )}

      {error && (
        <div className="error">
          <strong>Submission failed:</strong> {error}
        </div>
      )}

      {result && (
        <div className="success">
          <strong>201 Created.</strong> Customer {result.customerNumber},
          application {result.applicationNumber} was submitted successfully.
          Two documents were persisted by DM.
        </div>
      )}

      <div className="card">
        <h2>Customer details</h2>

        <div className="grid">
          <div className="field">
            <label htmlFor="firstName">First name</label>
            <input
              id="firstName"
              name="firstName"
              required
              maxLength={100}
              value={formValues.firstName}
              onChange={event => updateField('firstName', event.target.value)}
            />
          </div>

          <div className="field">
            <label htmlFor="lastName">Last name</label>
            <input
              id="lastName"
              name="lastName"
              required
              maxLength={100}
              value={formValues.lastName}
              onChange={event => updateField('lastName', event.target.value)}
            />
          </div>

          <div className="field">
            <label htmlFor="email">Email</label>
            <input
              id="email"
              name="email"
              type="email"
              required
              maxLength={254}
              value={formValues.email}
              onChange={event => updateField('email', event.target.value)}
            />
          </div>

          <div className="field">
            <label htmlFor="phoneNumber">Phone number</label>
            <input
              id="phoneNumber"
              name="phoneNumber"
              required
              maxLength={30}
              value={formValues.phoneNumber}
              onChange={event => updateField('phoneNumber', event.target.value)}
            />
          </div>
        </div>
      </div>

      <div className="card">
        <h2>KYC proof documents</h2>

        <div className="grid">
          <div className="field">
            <label htmlFor="kycProof">Identity / KYC proof (PDF)</label>
            <input
              id="kycProof"
              name="kycProof"
              type="file"
              accept="application/pdf,.pdf"
              required
              onChange={() => setUnsavedChanges(true)}
            />
            <small>
              Stored by Documents Management; the CO API never receives the
              binary.
            </small>
          </div>

          <div className="field">
            <label htmlFor="taxProof">Tax proof (PDF)</label>
            <input
              id="taxProof"
              name="taxProof"
              type="file"
              accept="application/pdf,.pdf"
              required
              onChange={() => setUnsavedChanges(true)}
            />
            <small>
              Uploaded using the CO BFF's Documents Management M2M identity.
            </small>
          </div>
        </div>

        <div className="actions">
          <button className="primary" type="submit" disabled={busy || loadingCustomer}>
            {busy ? 'Submitting onboarding...' : 'Submit Onboarding Application'}
          </button>
        </div>
      </div>
    </form>
  );
}
