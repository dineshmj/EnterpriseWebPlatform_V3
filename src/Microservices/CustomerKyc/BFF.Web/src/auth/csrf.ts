import { timingSafeEqual } from 'node:crypto';

/** Constant-time comparison of a supplied CSRF token with the session's token. */
export function safeEqual(supplied: string | undefined, expected: string | undefined): boolean {
  if (!supplied || !expected) return false;

  const a = Buffer.from(supplied);
  const b = Buffer.from(expected);

  return a.length === b.length && timingSafeEqual(a, b);
}