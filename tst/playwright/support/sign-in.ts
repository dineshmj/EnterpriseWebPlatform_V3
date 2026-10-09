import * as fs from 'node:fs';
import * as path from 'node:path';
import { expect, type Browser, type BrowserContext, type Page } from '@playwright/test';
import { sessionReuseMinutes } from './env';
import { IdpConsentPage, IdpLoginPage, isMfaStep } from './pages/IdpPages';
import { ShellPage } from './pages/ShellPage';
import { passwordOf, type PersonaName } from './personas';

const authDir = path.resolve(__dirname, '..', '.auth');

/**
 * The whole sign-in, as a person does it: open the Shell → redirected to the IDP's sign-in page
 * → username and password → consent (if the IDP asks) → back in the Shell → the menu appears.
 */
export async function signInThroughShell(page: Page, username: PersonaName): Promise<ShellPage> {
  const shell = new ShellPage(page);
  const login = new IdpLoginPage(page);

  await shell.open();
  await login.waitUntilShown();
  await login.signIn(username, passwordOf(username));

  // Next stop: consent, the Shell, a two-step sign-in, or the sign-in page again (refused).
  await page.waitForURL(url => IdpConsentPage.isAt(url) || ShellPage.isAt(url) || isMfaStep(url) || IdpLoginPage.isAt(url));
  const url = new URL(page.url());
  if (isMfaStep(url)) {
    throw new Error(`${username} was asked for a two-step code: switch Mfa:Enabled off for this suite (the MFA tests enrol their own user).`);
  }
  if (IdpLoginPage.isAt(url) && !(await page.waitForURL(u => !IdpLoginPage.isAt(u), { timeout: 3_000 }).then(() => true, () => false))) {
    throw new Error(`The IDP refused ${username}: ${await login.refusal() || 'check the password in tst\\playwright\\.env'}`);
  }
  if (IdpConsentPage.isAt(new URL(page.url()))) {
    await new IdpConsentPage(page).allow();
  }

  await page.waitForURL(u => ShellPage.isAt(u));
  await shell.waitForMenu();
  return shell;
}

/**
 * A new, isolated browser context (like its own private window) signed in as this person.
 * The sign-in is saved to .auth\<user>.json and reused for EWP_SESSION_REUSE_MINUTES, so most
 * tests start signed in; a stale or rejected saved sign-in is replaced by a fresh one.
 */
export async function contextFor(browser: Browser, username: PersonaName): Promise<{ context: BrowserContext; shell: ShellPage }> {
  const stateFile = path.join(authDir, `${username}.json`);

  if (isFresh(stateFile)) {
    const context = await browser.newContext({ storageState: stateFile });
    const shell = new ShellPage(await context.newPage());
    await shell.open();
    // Still signed in: the menu appears. Not any more (signed out, BFF state re-created): the
    // Shell sends the browser to the IDP - its sign-in page, or straight to consent while the
    // saved IDP cookie is still accepted. Either way, decide at once, not after a long timeout.
    const outcome = await Promise.race([
      shell.waitForMenu().then(() => 'menu' as const),
      shell.page.waitForURL(u => !ShellPage.isAt(u), { timeout: 30_000 }).then(() => 'left-shell' as const),
    ]).catch(() => 'unknown' as const);
    if (outcome === 'menu' && ShellPage.isAt(new URL(shell.page.url()))) return { context, shell };
    await context.close();
  }

  const context = await browser.newContext();
  const shell = await signInThroughShell(await context.newPage(), username);
  fs.mkdirSync(authDir, { recursive: true });
  await context.storageState({ path: stateFile });
  return { context, shell };
}

function isFresh(file: string): boolean {
  return fs.existsSync(file) && Date.now() - fs.statSync(file).mtimeMs < sessionReuseMinutes * 60_000;
}

export { expect };