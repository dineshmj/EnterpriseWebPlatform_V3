import { timingSafeEqual } from 'node:crypto';

// ┌─ :: SECURITY :: Anti-forgery token compared in constant time ──────────────────────────────────
// │ Why : A comparison that stops at the first wrong character leaks the token byte by byte through
// │       timing.
// │ How : timingSafeEqual on equal-length buffers; a missing token never matches.
// │ Ref : Features 2.3.3 | OWASP CSRF Prevention Cheat Sheet | CWE-208, CWE-352
// └────────────────────────────────────────────────────────────────────────────────────────────────
/** Constant-time comparison of a supplied CSRF token with the session's token. */
export function safeEqual(supplied: string | undefined, expected: string | undefined): boolean {
  if (!supplied || !expected) return false;

  const a = Buffer.from(supplied);
  const b = Buffer.from(expected);

  return a.length === b.length && timingSafeEqual(a, b);
}