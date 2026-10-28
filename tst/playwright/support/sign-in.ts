import * as fs from 'node:fs';
import * as path from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Page, type Response } from '@playwright/test';
import { sessionReuseMinutes } from './env';
import { IdpConsentPage, IdpLoginPage, isMfaStep } from './pages/IdpPages';
import { ShellPage } from './pages/ShellPage';
import { passwordOf, type PersonaName } from './personas';
import { urls } from './services';

const authDir = path.resolve(__dirname, '..', '.auth');

/**
 * The whole sign-in, as a person does it: open the Shell → redirected to the IDP's sign-in page
 * → username and password → consent (if the IDP asks) → back in the Shell → the menu appears.
 */
export async function signInThroughShell(page: Page, username: PersonaName): Promise<ShellPage> {
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ The IDP throttles its sign-in pages per IP: 20 requests a fixed minute, and opening the
  // │ page counts as well as posting it - about 10 sign-ins a minute (IDP Program.cs, "login").
  // │ After a database re-create every saved sign-in is stale, so a run signs everyone in at
  // │ once and meets that limit. The limit is a control and stays; the tests wait it out ONCE
  // │ and say so, instead of failing on an unrelated-looking step.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const throttled = watchIdpThrottling(page);
  try {
    return await signInOnce(page, username);
  } catch (error) {
    if (!throttled.seen()) throw error;
    test.info().annotations.push({ type: 'note', description: `The IDP throttled sign-ins (429) for ${username}: waited ${loginWindowMs / 1000} s and signed in again.` });
    test.info().setTimeout(test.info().timeout + loginWindowMs + 30_000);
    await page.waitForTimeout(loginWindowMs);
    throttled.reset();
    try {
      return await signInOnce(page, username);
    } catch (retryError) {
      if (!throttled.seen()) throw retryError;
      throw new Error(`The IDP still throttles sign-ins from this machine (429) after a ${loginWindowMs / 1000} s wait, signing in ${username}: ${String(retryError)}`);
    }
  } finally {
    throttled.stop();
  }
}

// The IDP's "login" limiter uses a fixed one-minute window; a little more covers clock edges.
const loginWindowMs = 65_000;
const idpHost = new URL(urls.idp).host;

function watchIdpThrottling(page: Page) {
  let count = 0;
  const onResponse = (response: Response) => {
    if (response.status() === 429 && new URL(response.url()).host === idpHost) count++;
  };
  page.on('response', onResponse);
  return {
    seen: () => count > 0,
    reset: () => { count = 0; },
    stop: () => page.off('response', onResponse),
  };
}

async function signInOnce(page: Page, username: PersonaName): Promise<ShellPage> {
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