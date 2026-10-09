import { expect, type Page } from '@playwright/test';
import { urls } from '../services';

const idpHost = new URL(urls.idp).host;

/** The IDP's sign-in page (Pages/Account/Login). */
export class IdpLoginPage {
  constructor(private readonly page: Page) {}

  static isAt(url: URL): boolean {
    return url.host === idpHost && /\/Account\/Login/i.test(url.pathname);
  }

  async waitUntilShown(): Promise<void> {
    await this.page.waitForURL(url => IdpLoginPage.isAt(url));
    await expect(this.page.getByRole('heading', { name: 'Welcome back', exact: true })).toBeVisible();
  }

  async signIn(username: string, password: string): Promise<void> {
    await this.page.getByLabel('Username').fill(username);
    await this.page.getByLabel('Password').fill(password);
    await this.page.getByRole('button', { name: 'Sign in', exact: true }).click();
  }

  /** The message shown when the IDP refuses a sign-in (wrong password, locked account …). */
  async refusal(): Promise<string> {
    const alert = this.page.getByRole('alert').first();
    return (await alert.isVisible().catch(() => false)) ? ((await alert.textContent())?.trim() ?? '') : '';
  }
}

/** The IDP's consent page (the Shell client requires consent). */
export class IdpConsentPage {
  constructor(private readonly page: Page) {}

  static isAt(url: URL): boolean {
    return url.host === idpHost && /\/consent/i.test(url.pathname);
  }

  /** Allow - without "remember my decision", so every sign-in behaves the same way. */
  async allow(): Promise<void> {
    await this.page.getByRole('button', { name: 'Allow', exact: true }).click();
  }
}

/** Two-step sign-in pages: the suite runs with Mfa:Enabled off; the MFA suite drives them itself. */
export function isMfaStep(url: URL): boolean {
  return url.host === idpHost && /\/Account\/Mfa/i.test(url.pathname);
}

/** The IDP's "Sign out?" confirmation and "You're signed out" pages. */
export class IdpSignOutPages {
  constructor(private readonly page: Page) {}

  async confirmIfAsked(): Promise<void> {
    const confirm = this.page.getByRole('button', { name: 'Yes, sign me out', exact: true });
    if (await confirm.isVisible({ timeout: 5_000 }).catch(() => false)) await confirm.click();
  }
}