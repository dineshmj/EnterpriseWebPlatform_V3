import { cva, type VariantProps } from 'class-variance-authority';
import { AlertTriangle, CheckCircle2, Info, XCircle } from 'lucide-react';
import type { HTMLAttributes, ReactNode } from 'react';
import { cn } from './cn';

export const badgeVariants = cva(
  'inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border px-2.5 py-0.5 text-xs font-medium',
  {
    variants: {
      tone: {
        neutral: 'border-line bg-subtle text-ink-muted',
        brand: 'border-brand-100 bg-brand-50 text-brand-700',
        info: 'border-info-200 bg-info-50 text-info-700',
        success: 'border-success-200 bg-success-50 text-success-700',
        warning: 'border-warning-200 bg-warning-50 text-warning-700',
        danger: 'border-danger-200 bg-danger-50 text-danger-700',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export function Badge({
  tone,
  dot,
  className,
  children,
}: VariantProps<typeof badgeVariants> & { dot?: boolean; className?: string; children: ReactNode }) {
  return (
    <span className={cn(badgeVariants({ tone }), className)}>
      {dot && <span className="size-1.5 rounded-full bg-current" aria-hidden="true" />}
      {children}
    </span>
  );
}

/** "PENDING_REVIEW" → "Pending review". */
export function statusLabel(status: string | null | undefined) {
  const code = (status ?? '').toUpperCase();
  return code ? code.toLowerCase().replace(/_/g, ' ').replace(/^\w/, c => c.toUpperCase()) : '—';
}

/** Workflow status codes (UPPER_SNAKE_CASE) shown as readable, colour-coded badges. */
export function StatusBadge({ status }: { status: string | null | undefined }) {
  const code = (status ?? '').toUpperCase();
  const tone =
    ['APPROVED', 'COMPLETED', 'KYC_COMPLETED', 'COMPLIANCE_COMPLETED', 'ACTIVE', 'OPENED'].includes(code) ? 'success'
      : ['REJECTED', 'CANCELLED', 'COMPENSATION_FAILED', 'SUSPENDED', 'CLOSED', 'FAILED'].includes(code) ? 'danger'
        : ['PENDING_REVIEW', 'UNDER_REVIEW', 'ON_HOLD', 'SUBMITTED', 'DRAFT', 'PROSPECT'].includes(code) ? 'warning'
          : code.endsWith('_IN_PROGRESS') || code === 'ONBOARDING' || code === 'SCREENING' || code === 'OPENING' ? 'info'
            : 'neutral';

  return <Badge tone={tone} dot>{statusLabel(code)}</Badge>;
}

const alertVariants = cva('flex gap-3 rounded-control border px-4 py-3 text-sm leading-6', {
  variants: {
    tone: {
      info: 'border-info-200 bg-info-50 text-info-700',
      success: 'border-success-200 bg-success-50 text-success-700',
      warning: 'border-warning-200 bg-warning-50 text-warning-700',
      danger: 'border-danger-200 bg-danger-50 text-danger-700',
    },
  },
  defaultVariants: { tone: 'info' },
});

const alertIcons = { info: Info, success: CheckCircle2, warning: AlertTriangle, danger: XCircle };

export function Alert({
  tone = 'info',
  title,
  children,
  className,
}: { tone?: 'info' | 'success' | 'warning' | 'danger'; title?: ReactNode; children?: ReactNode; className?: string }) {
  const Icon = alertIcons[tone];
  return (
    <div role={tone === 'danger' ? 'alert' : 'status'} className={cn(alertVariants({ tone }), className)}>
      <Icon className="mt-0.5 size-4 shrink-0" aria-hidden="true" />
      <div className="min-w-0">
        {title && <p className="font-semibold">{title}</p>}
        {children && <div className={cn(title && 'mt-0.5', 'break-words')}>{children}</div>}
      </div>
    </div>
  );
}

export function EmptyState({ icon, title, children }: { icon?: ReactNode; title: ReactNode; children?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center gap-2 px-6 py-12 text-center">
      {icon && <span className="flex size-10 items-center justify-center rounded-full bg-subtle text-ink-faint [&_svg]:size-5">{icon}</span>}
      <p className="text-sm font-medium text-ink">{title}</p>
      {children && <p className="max-w-md text-[13px] text-ink-muted">{children}</p>}
    </div>
  );
}

export function Skeleton({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('animate-pulse rounded-md bg-line/70', className)} {...props} />;
}