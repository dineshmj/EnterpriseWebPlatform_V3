import { Activity } from 'lucide-react';
import { MfeShell } from '../../../../components/MfeShell';
import { Card, CardContent, CardHeader } from '../../../../components/ui/card';
import { Alert, Badge } from '../../../../components/ui/feedback';

const steps = [
  {
    title: 'Application submitted',
    text: 'The Customer Onboarding API commits the application and its OnboardingApplicationSubmitted event to the Outbox, in one transaction.',
    status: 'Live',
  },
  {
    title: 'Event published',
    text: 'The Customer Outbox Publisher relays onboarding.application.submitted to Kafka, in order, exactly as committed.',
    status: 'Live',
  },
  {
    title: 'KYC case opened',
    text: 'Customer KYC opens one case per application, scoped to the originating branch. Customer Onboarding moves the application to KYC in progress.',
    status: 'Live',
  },
  {
    title: 'Human KYC review',
    text: 'A KYC officer of the branch verifies the identity and tax proofs. The first decision assigns the case to that officer.',
    status: 'Live',
  },
  {
    title: 'Outcome recorded',
    text: 'KycCaseApproved / KycCaseRejected flows back; Customer Onboarding records KYC completed or rejected.',
    status: 'Live',
  },
  {
    title: 'Compliance and account opening',
    text: 'The next saga steps, with their own bounded contexts and human decision gates.',
    status: 'Planned',
  },
];

export default function WorkflowPage() {
  return (
    <MfeShell title="Onboarding workflow" subtitle="Customer Onboarding">
      <div className="grid items-start gap-6 xl:grid-cols-[minmax(0,1fr)_340px]">
        <Card>
          <CardHeader
            icon={<Activity />}
            title="How an onboarding moves through the bank"
            description="An asynchronous saga across bounded contexts: Outbox, Kafka and idempotent consumers."
          />
          <CardContent>
            <ol className="relative space-y-6 border-l border-line pl-8">
              {steps.map((step, index) => (
                <li key={step.title} className="relative">
                  <span className="absolute -left-[45px] flex size-7 items-center justify-center rounded-full border border-line bg-surface text-xs font-semibold text-brand-700 shadow-sm">
                    {index + 1}
                  </span>
                  <div className="flex flex-wrap items-center gap-2">
                    <h3 className="text-sm font-semibold text-ink">{step.title}</h3>
                    <Badge tone={step.status === 'Live' ? 'success' : 'neutral'}>{step.status}</Badge>
                  </div>
                  <p className="mt-1 text-[13px] leading-6 text-ink-muted">{step.text}</p>
                </li>
              ))}
            </ol>
          </CardContent>
        </Card>

        <Alert tone="info" title="Read-only view">
          A real-time workflow projection (SignalR) will turn this page into a live monitor of each application’s progress.
        </Alert>
      </div>
    </MfeShell>
  );
}