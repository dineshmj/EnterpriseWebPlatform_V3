import { createCipheriv, createDecipheriv, hkdfSync, randomBytes } from 'node:crypto';
import type { Request, Response } from 'express';

/** What the callback needs to finish a sign-in: the values sent to the IDP, and where to return. */
export interface PendingSignIn {
  state: string;
  nonce: string;
  codeVerifier: string;
  returnUrl: string;
}

const COOKIE_PREFIX = '__Host-KYC-OIDC-';
const LIFETIME_MS = 5 * 60 * 1000;
const STATE_FORMAT = /^[A-Za-z0-9_-]{16,128}$/;

/**
 * A sign-in in progress is kept in its OWN short-lived cookie - one per attempt, named after its
 * `state` - not in the session (as ASP.NET Core's correlation cookie does). Two reasons:
 *  - the session is saved whole by every request that changes it; a request that overlapped a
 *    sign-in (e.g. one refreshing tokens) would write back its older copy and erase the pending
 *    sign-in, so the callback found none and the officer saw a 401;
 *  - two sign-ins at once (two frames, two tabs) no longer overwrite each other.
 * The cookie is encrypted and authenticated (AES-256-GCM, bound to its name) with a key derived
 * from KYC_BFF_SESSION_KEY for this purpose only; HttpOnly, Secure, SameSite=Lax, `__Host-`
 * prefix, 5 minutes, and deleted when the callback reads it (single use).
 */
export function savePendingSignIn(res: Response, sessionKey: Buffer, pending: PendingSignIn): void {
  const name = COOKIE_PREFIX + pending.state;
  res.cookie(name, seal(JSON.stringify(pending), keyFrom(sessionKey), name), {
    httpOnly: true,
    secure: true,
    sameSite: 'lax',
    path: '/',
    maxAge: LIFETIME_MS,
  });
}

/** The pending sign-in named by the callback's `state` - read once, then deleted. */
export function takePendingSignIn(req: Request, res: Response, sessionKey: Buffer): PendingSignIn | undefined {
  const state = typeof req.query.state === 'string' ? req.query.state : '';
  if (!STATE_FORMAT.test(state)) return undefined;

  const name = COOKIE_PREFIX + state;
  const sealed = readCookie(req, name);
  res.clearCookie(name, { httpOnly: true, secure: true, sameSite: 'lax', path: '/' });
  if (!sealed) return undefined;

  try {
    const pending = JSON.parse(unseal(sealed, keyFrom(sessionKey), name)) as PendingSignIn;
    return pending.state === state ? pending : undefined;
  } catch {
    return undefined;   // tampered with, expired key, or not ours
  }
}

function keyFrom(sessionKey: Buffer): Buffer {
  return Buffer.from(hkdfSync('sha256', sessionKey, Buffer.alloc(0), 'ewp-kyc-bff pending sign-in', 32));
}

function seal(plain: string, key: Buffer, name: string): string {
  const iv = randomBytes(12);
  const cipher = createCipheriv('aes-256-gcm', key, iv);
  cipher.setAAD(Buffer.from(name));
  const body = Buffer.concat([cipher.update(plain, 'utf8'), cipher.final()]);
  return Buffer.concat([iv, cipher.getAuthTag(), body]).toString('base64url');
}

function unseal(sealed: string, key: Buffer, name: string): string {
  const raw = Buffer.from(sealed, 'base64url');
  const decipher = createDecipheriv('aes-256-gcm', key, raw.subarray(0, 12));
  decipher.setAAD(Buffer.from(name));
  decipher.setAuthTag(raw.subarray(12, 28));
  return Buffer.concat([decipher.update(raw.subarray(28)), decipher.final()]).toString('utf8');
}

function readCookie(req: Request, name: string): string | undefined {
  for (const part of (req.headers.cookie ?? '').split(';')) {
    const at = part.indexOf('=');
    if (at > 0 && part.slice(0, at).trim() === name) return decodeURIComponent(part.slice(at + 1).trim());
  }
  return undefined;
}