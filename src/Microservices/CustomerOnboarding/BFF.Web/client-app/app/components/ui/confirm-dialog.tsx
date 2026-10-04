import { AlertTriangle } from 'lucide-react';
import type { ReactNode } from 'react';
import { Button } from './button';

/** Modal confirmation (role="alertdialog"). Focus starts on the safe choice. */
export function ConfirmDialog({
  title,
  children,
  cancelLabel,
  confirmLabel,
  onCancel,
  onConfirm,
}: {
  title: string;
  children: ReactNode;
  cancelLabel: string;
  confirmLabel: string;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  return (
    <div role="presentation" className="fixed inset-0 z-50 flex items-center justify-center bg-brand-950/45 p-4 backdrop-blur-[2px]">
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="ewp-confirm-title"
        aria-describedby="ewp-confirm-description"
        className="w-full max-w-md rounded-card border border-line bg-surface p-6 shadow-raised"
      >
        <div className="flex gap-4">
          <span className="flex size-10 shrink-0 items-center justify-center rounded-full bg-warning-50 text-warning-700">
            <AlertTriangle className="size-5" aria-hidden="true" />
          </span>
          <div>
            <h2 id="ewp-confirm-title" className="text-base font-semibold text-ink">{title}</h2>
            <div id="ewp-confirm-description" className="mt-1.5 text-sm leading-6 text-ink-muted">{children}</div>
          </div>
        </div>
        <div className="mt-6 flex justify-end gap-3">
          <Button variant="secondary" onClick={onCancel} autoFocus>{cancelLabel}</Button>
          <Button variant="danger" onClick={onConfirm}>{confirmLabel}</Button>
        </div>
      </div>
    </div>
  );
}