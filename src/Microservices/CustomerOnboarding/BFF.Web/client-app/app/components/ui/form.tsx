import { forwardRef, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from 'react';
import { cn } from './cn';

const controlBase =
  'w-full rounded-control border border-line-strong bg-surface px-3 text-sm text-ink shadow-[0_1px_1px_rgba(16,35,58,0.03)] transition-colors placeholder:text-ink-faint hover:border-brand-300 focus:border-accent-500 focus:outline-none focus:ring-3 focus:ring-accent-100 disabled:cursor-not-allowed disabled:bg-subtle disabled:text-ink-muted read-only:bg-subtle read-only:text-ink-muted read-only:hover:border-line-strong';

/** A labelled form field: label, control, optional hint. */
export function Field({
  label,
  htmlFor,
  required,
  hint,
  className,
  children,
}: {
  label: ReactNode;
  htmlFor: string;
  required?: boolean;
  hint?: ReactNode;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div className={cn('flex flex-col gap-1.5', className)}>
      <label htmlFor={htmlFor} className="text-[13px] font-medium text-ink">
        {label}
        {required && <span className="ml-0.5 text-danger-700" aria-hidden="true">*</span>}
      </label>
      {children}
      {hint && <p className="text-xs leading-5 text-ink-muted">{hint}</p>}
    </div>
  );
}

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(
  ({ className, ...props }, ref) => <input ref={ref} className={cn(controlBase, 'h-10', className)} {...props} />,
);
Input.displayName = 'Input';

export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(
  ({ className, ...props }, ref) => <select ref={ref} className={cn(controlBase, 'h-10 pr-8', className)} {...props} />,
);
Select.displayName = 'Select';

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(
  ({ className, ...props }, ref) => <textarea ref={ref} className={cn(controlBase, 'min-h-28 py-2.5 leading-6', className)} {...props} />,
);
Textarea.displayName = 'Textarea';