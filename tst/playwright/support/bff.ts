import { expect, type APIRequestContext } from '@playwright/test';
import type { ShellPage } from './pages/ShellPage';

/**
 * Plain HTTP calls to an MFE's BFF as a signed-in person - what a script in that person's browser
 * could send. The browser context's cookies ride along (the BFF session and its anti-forgery cookie).
 */
export interface BffSession {
  origin: string;
  request: APIRequestContext;
  /** A fresh anti-forgery request token (GET /api/auth/csrf), for the X-CSRF-TOKEN header. */
  csrfToken(): Promise<string>;
}

/**
 * Signs the person in to the BFF at `origin` (silent sign-in through the IDP's single sign-on,
 * top-level - the same as the Shell's frame does), whether or not their menu offers that MFE.
 */
export async function bffSessionFor(person: ShellPage, origin: string): Promise<BffSession> {
  const context = person.page.context();
  const page = await context.newPage();
  try {
    await page.goto(`${origin}/api/auth/silent-login`);
    await page.waitForURL(url => url.origin === origin && !url.pathname.startsWith('/api/auth'), { timeout: 30_000 });
  } finally {
    await page.close();
  }
  const request = context.request;
  return {
    origin,
    request,
    async csrfToken() {
      const response = await request.get(`${origin}/api/auth/csrf`);
      expect(response.status(), 'GET /api/auth/csrf').toBe(200);
      const body = await response.json() as { token?: string };
      expect(body.token, 'the anti-forgery request token').toBeTruthy();
      return body.token!;
    },
  };
}