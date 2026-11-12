'use client';

import { useEffect, useRef } from 'react';

/** Fields a person types or picks in; buttons, check boxes, files and read-only fields are skipped. */
const FIELDS = [
  'input:not([type=hidden]):not([type=checkbox]):not([type=radio]):not([type=file]):not([type=button]):not([type=submit]):not(:disabled):not([readonly])',
  'select:not(:disabled)',
  'textarea:not(:disabled):not([readonly])',
].join(', ');

const isVisible = (el: HTMLElement) => el.getClientRects().length > 0;

const isEditing = (el: Element | null) =>
  !!el && (el instanceof HTMLInputElement || el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement
    || (el instanceof HTMLElement && el.isContentEditable));

// ┌─ :: ARCHITECTURE :: The first empty field takes the focus when a form shows ──────────────────
// │ Why : A person should type at once, and Enter should submit (a <form> with one type="submit"
// │       button; disabled while something is missing, so Enter does nothing until it is valid).
// │ How : The HTML autofocus attribute is ignored in a cross-origin frame - and this app always
// │       runs in the Shell's - so the focus is set from script once the form is on screen, and
// │       again when `when` changes (e.g. a customer was chosen and the next step appeared). It
// │       never takes the focus from a field the person is already in.
// │ Ref : WCAG 2.2 SC 2.4.3 (focus order) | HTML implicit submission
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function useFocusFirstField<T extends HTMLElement = HTMLFormElement>(when: unknown = true) {
  const ref = useRef<T>(null);
  useEffect(() => {
    const root = ref.current;
    if (!root || when === false || when === null || when === undefined) return;
    if (isEditing(document.activeElement)) return;
    const fields = [...root.querySelectorAll<HTMLInputElement | HTMLSelectElement | HTMLTextAreaElement>(FIELDS)].filter(isVisible);
    (fields.find((f) => !f.value) ?? fields[0])?.focus({ preventScroll: true });
  }, [when]);
  return ref;
}