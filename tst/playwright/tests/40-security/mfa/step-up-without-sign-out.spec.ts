import { expect, test, type Page } from '@playwright/test';
import { isMfaCodePage, signInWithMfa } from '../../../support/mfa';
import { IdpConsentPage } from '../../../support/pages/IdpPages';
import { ShellPage } from '../../../support/pages/ShellPage';
import { urls } from '../../../support/services';
import { resetAllSimulators } from '../../../support/simulators';

/**
 * Step-up without signing out (Threat-Model R-13). A risky action needs a RECENT sign-in with the
 * authenticator code; when the person's code is older than that, the screen opens a small window
 * in which the IDP asks ONLY for a fresh code (no password), and the action then goes through -
 * the person never signs out.
 *
 * Runs only with MFA switched on (EWP_MFA_ENABLED=true in tst\playwright\.env and the settings in
 * tst\README.md, "MFA tests"); the rest of the suite runs with MFA off, so it is skipped there.
 * The max age set there (EWP_STEP_UP_MAX_AGE_MINUTES, 1 by default) is waited out on purpose.
 */
const mfaEnabled = process.env.EWP_MFA_ENABLED === 'true';
const maxAgeMinutes = Number(process.env.EWP_STEP_UP_MAX_AGE_MINUTES ?? '1');

// ┌─ :: SECURITY :: What this test proves ─────────────────────────────────────────────────────────
// │ Why : Threat-Model R-13: a risky action used to need a full sign-out to refresh MFA.
// │ How : Past the step-up age a risky change opens a code-only window (no password) and completes
// │       without signing out; while the code is fresh no window opens; the window returns to the
// │       fixed step-up-done address.
// │ Ref : Features 2.1.5 | RFC 9470 | Threat-Model R-13 | AI review 2: xAI F-3
// └────────────────────────────────────────────────────────────────────────────────────────────────
test.describe('MFA step-up without signing out', () => {
  test.skip(!mfaEnabled, 'MFA is off: set EWP_MFA_ENABLED=true and switch MFA on (tst\\README.md, MFA tests).');
  test.describe.configure({ mode: 'serial' });

  test.afterAll(async () => {
    await resetAllSimulators();
  });

  test('ShouldAskOnlyForAFreshCode_AndCompleteTheChange_WhenTheMfaSignInIsTooOld', async ({ browser }) => {
    test.setTimeout((maxAgeMinutes * 60 + 240) * 1_000);

    // Arrange: Daniel signs in with his code and opens Platform Health.
    const context = await browser.newContext();
    const page = await context.newPage();
    const authenticator = await signInWithMfa(page, 'daniel.ops');
    const signedInAt = Date.now();

    const shell = new ShellPage(page);
    await shell.openMenuItem('Operations', 'Platform Health');
    await shell.waitForScreen('Platform health');
    const paymentNetwork = shell.workspace().getByLabel('Behaviour of Payment Network', { exact: true });
    await expect(paymentNetwork, 'the simulator switch is shown (Development only)').toBeEnabled({ timeout: 30_000 });

    // ... and his MFA sign-in becomes older than the step-up allows.
    await page.waitForTimeout(Math.max(0, signedInAt + maxAgeMinutes * 60_000 + 5_000 - Date.now()));

    // Act: he switches the Payment Network to Slow - a risky change.
    const codeWindowOpens = context.waitForEvent('page');
    await paymentNetwork.selectOption('Slow');
    const codeWindow = await codeWindowOpens;

    // Assert: the window asks only for the code - no password, no new sign-in.
    await codeWindow.waitForURL(u => isMfaCodePage(u), { timeout: 30_000 });
    await expect(codeWindow.getByRole('heading', { name: 'Enter your code to continue', exact: true })).toBeVisible();
    await expect(codeWindow.getByLabel('Password')).toHaveCount(0);

    await codeWindow.getByRole('textbox', { name: 'Code', exact: true }).fill(await authenticator.nextCode(codeWindow));
    await codeWindow.getByRole('button', { name: 'Confirm', exact: true }).click();
    await waitUntilClosed(codeWindow);

    // ... the change went through (the switch shows what the simulator now does), and he is
    // still signed in (same Shell, same screen).
    await expect(paymentNetwork).toHaveValue('Slow', { timeout: 30_000 });
    await expect(shell.workspace().getByRole('row').filter({ has: paymentNetwork }), 'the change is recorded with his LAN ID').toContainText('dacoo');
    await expect(shell.contextButton('Operations')).toBeVisible();

    // ... and while the new code is recent, the next change needs no window at all.
    const noWindow = context.waitForEvent('page', { timeout: 5_000 }).then(() => 'window', () => 'none');
    await paymentNetwork.selectOption('Healthy');
    await expect(paymentNetwork).toHaveValue('Healthy', { timeout: 30_000 });
    expect(await noWindow, 'no code window for a change right after a fresh code').toBe('none');

    await context.close();
  });

  test('ShouldConfirmWithACodeOnly_ThroughThePaymentsBff_AndReturnToItsFixedAddress', async ({ browser }) => {
    test.setTimeout(180_000);

    // Arrange: Daniel signs in and opens Payments, so the Payments BFF has his session.
    const context = await browser.newContext();
    const page = await context.newPage();
    const authenticator = await signInWithMfa(page, 'daniel.ops');
    const shell = new ShellPage(page);
    await shell.openMenuItem('Payments', 'Payment Processing Monitor');
    await shell.waitForScreen('Payment processing monitor');

    // Act: the window the Payments screens open when an API answers 403 mfa_required.
    const codeWindow = await context.newPage();
    await codeWindow.goto(`${urls.paymentsMfe}/api/auth/step-up`);

    // Assert: code only, then back on the BFF's fixed "done" address.
    await codeWindow.waitForURL(u => isMfaCodePage(u), { timeout: 30_000 });
    await expect(codeWindow.getByLabel('Password')).toHaveCount(0);
    await codeWindow.getByRole('textbox', { name: 'Code', exact: true }).fill(await authenticator.nextCode(codeWindow));
    await codeWindow.getByRole('button', { name: 'Confirm', exact: true }).click();
    await allowConsentIfAsked(codeWindow);
    await codeWindow.waitForURL(`${urls.paymentsMfe}/api/auth/step-up-done`, { timeout: 30_000 });
    await expect(codeWindow.getByText('Confirmed.', { exact: false })).toBeVisible();

    await context.close();
  });
});

/** The screen closes the window once it is back on the BFF; a consent page (if the IDP asks) comes first. */
async function waitUntilClosed(window: Page): Promise<void> {
  const closed = window.waitForEvent('close', { timeout: 60_000 });
  await allowConsentIfAsked(window).catch(() => { /* already closed */ });
  await closed;
}

async function allowConsentIfAsked(window: Page): Promise<void> {
  const consent = await window.waitForURL(u => IdpConsentPage.isAt(u), { timeout: 3_000 }).then(() => true, () => false);
  if (consent) await new IdpConsentPage(window).allow();
}