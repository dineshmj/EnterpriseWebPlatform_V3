import { createHmac } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { expect, type Page } from '@playwright/test';
import { IdpConsentPage, IdpLoginPage } from './pages/IdpPages';
import { ShellPage } from './pages/ShellPage';
import { passwordOf, type PersonaName } from './personas';
import { urls } from './services';

/**
 * Two-step sign-in for the MFA tests (the rest of the suite runs with MFA off). The tests play
 * Google Authenticator themselves: they enrol the person once - reading the setup key the IDP
 * shows - and compute each 6-digit code (RFC 6238: HMAC-SHA1, 30-second steps), exactly as the
 * app on a phone does. The key is kept in .auth\mfa-<user>.key (git-ignored) for later runs.
 *
 * The IDP accepts each code only once (a replayed code is refused), so every code is taken from
 * a time step later than the last one used - which may mean waiting up to 30 seconds.
 */
const STEP_SECONDS = 30;
const authDir = path.resolve(__dirname, '..', '.auth');
const idpHost = new URL(urls.idp).host;

// ┌─ :: SECURITY :: A test authenticator (TOTP) ───────────────────────────────────────────────────
// │ Why : Step-up MFA can only be tested end to end if the test can produce real authenticator
// │       codes.
// │ How : The test enrols the demo persona from the IDP's own setup key and computes the codes
// │       itself (RFC 6238, HMAC-SHA1, 30-second steps); only demo IDP users, only when
// │       EWP_MFA_ENABLED=true.
// │ Ref : Features 2.1.5 | RFC 6238 (TOTP) | tst/README.md "MFA tests"
// └────────────────────────────────────────────────────────────────────────────────────────────────
export class Authenticator {
  private lastStep = -1;

  constructor(readonly username: PersonaName, private readonly secret: Buffer) {}

  /** The code of a time step not used yet - waits for the next step when necessary. */
  async nextCode(page: Page): Promise<string> {
    let step = currentStep();
    while (step <= this.lastStep) {
      await page.waitForTimeout(1_000);
      step = currentStep();
    }
    this.lastStep = step;
    return codeAt(this.secret, step);
  }
}

// ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
// │ One authenticator per person for the whole run (one worker), so a used step is never reused.
// └──────────────────────────────────────────────────────────────────────────────────────
const authenticators = new Map<PersonaName, Authenticator>();

/** The two-step pages of the IDP. */
export const isMfaCodePage = (url: URL) => url.host === idpHost && /\/Account\/Mfa$/i.test(url.pathname);
const isMfaSetupPage = (url: URL) => url.host === idpHost && /\/Account\/MfaSetup/i.test(url.pathname);

/**
 * The whole sign-in with MFA on: Shell → IDP → password → (first time: enrol and save the
 * recovery codes) → authenticator code → consent if asked → the Shell's menu.
 */
export async function signInWithMfa(page: Page, username: PersonaName): Promise<Authenticator> {
  const shell = new ShellPage(page);
  const login = new IdpLoginPage(page);
  await shell.open();
  await login.waitUntilShown();
  await login.signIn(username, passwordOf(username));

  await page.waitForURL(u => isMfaSetupPage(u) || isMfaCodePage(u) || ShellPage.isAt(u) || IdpLoginPage.isAt(u));
  const url = new URL(page.url());
  if (ShellPage.isAt(url)) {
    throw new Error('The IDP signed in without asking for a code: switch Mfa:Enabled on in the IDP (see tst\\README.md, MFA tests).');
  }
  if (IdpLoginPage.isAt(url)) {
    throw new Error(`The IDP refused ${username}: ${await login.refusal() || 'check the password in tst\\playwright\\.env'}`);
  }

  let authenticator: Authenticator;
  if (isMfaSetupPage(url)) {
    authenticator = await enrol(page, username);
  } else {
    authenticator = authenticators.get(username) ?? loadSaved(username) ?? notEnrolledHere(username);
    await page.getByRole('textbox', { name: 'Code', exact: true }).fill(await authenticator.nextCode(page));
    await page.getByRole('button', { name: 'Verify and sign in', exact: true }).click();
  }

  await page.waitForURL(u => IdpConsentPage.isAt(u) || ShellPage.isAt(u));
  if (IdpConsentPage.isAt(new URL(page.url()))) await new IdpConsentPage(page).allow();
  await page.waitForURL(u => ShellPage.isAt(u));
  await shell.waitForMenu();
  return authenticator;
}

/** First sign-in after the IDP database was re-created: enrol with the setup key the page shows. */
async function enrol(page: Page, username: PersonaName): Promise<Authenticator> {
  const key = ((await page.locator('.mfa-key').textContent()) ?? '').replace(/\s+/g, '').toUpperCase();
  expect(key, 'the IDP shows a setup key').toMatch(/^[A-Z2-7]{16,}$/);

  const authenticator = new Authenticator(username, base32Decode(key));
  authenticators.set(username, authenticator);
  fs.mkdirSync(authDir, { recursive: true });
  fs.writeFileSync(keyFile(username), key);

  await page.getByLabel('Then enter the 6-digit code the app shows').fill(await authenticator.nextCode(page));
  await page.getByRole('button', { name: 'Verify', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Save your recovery codes', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'I have saved them - continue', exact: true }).click();
  return authenticator;
}

function loadSaved(username: PersonaName): Authenticator | undefined {
  if (!fs.existsSync(keyFile(username))) return undefined;
  const authenticator = new Authenticator(username, base32Decode(fs.readFileSync(keyFile(username), 'utf8').trim()));
  authenticators.set(username, authenticator);
  return authenticator;
}

function notEnrolledHere(username: PersonaName): never {
  throw new Error(
    `${username} is already enrolled with an authenticator these tests do not know (enrolled by hand?). ` +
    'Re-create the IDP database (Reset.ps1 option 1), then run the MFA tests first.');
}

const keyFile = (username: PersonaName) => path.join(authDir, `mfa-${username}.key`);

const currentStep = () => Math.floor(Date.now() / 1000 / STEP_SECONDS);

/** RFC 6238 / 4226: HMAC-SHA1 over the 8-byte step counter, dynamic truncation, 6 digits. */
function codeAt(secret: Buffer, step: number): string {
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const hash = createHmac('sha1', secret).update(counter).digest();
  const offset = hash[hash.length - 1] & 0x0f;
  const value = (hash.readUInt32BE(offset) & 0x7fffffff) % 1_000_000;
  return value.toString().padStart(6, '0');
}

/** RFC 4648 base32 (the setup key's alphabet), padding optional. */
function base32Decode(text: string): Buffer {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = 0;
  let value = 0;
  const bytes: number[] = [];
  for (const char of text.split('=')[0]) {   // base32 padding ('=') is only ever at the end
    const index = alphabet.indexOf(char);
    if (index < 0) throw new Error(`Not a base32 setup key: "${char}".`);
    value = (value << 5) | index;
    bits += 5;
    if (bits >= 8) {
      bytes.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(bytes);
}