'use client';

import Link from 'next/link';
import { ClipboardList } from 'lucide-react';
import { MfeShell } from '../../../../components/MfeShell';
import { CustomerOnboardingForm } from '../../../../components/CustomerOnboardingForm';
import { buttonVariants } from '../../../../components/ui/button';

/**
 * Starting an onboarding: the form only (customer details, address, the two documents).
 * Following applications that are already on their way is a separate screen - the one the
 * bell's progress notifications open - so "new" and "how is it going" never share a page.
 */
export default function NewApplicationPage() {
  return (
    <MfeShell title="New onboarding application" subtitle="Customer Onboarding">
      <div className="flex flex-col gap-6">
        <div className="flex justify-end">
          <Link className={buttonVariants({ variant: 'ghost', size: 'sm' })} href="/v1/onboarding/applications/view-all">
            <ClipboardList aria-hidden="true" />View onboarding applications
          </Link>
        </div>
        <CustomerOnboardingForm />
      </div>
    </MfeShell>
  );
}