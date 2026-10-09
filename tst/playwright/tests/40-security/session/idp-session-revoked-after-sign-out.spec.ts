import { expect, test } from '@playwright/test';
import { IdpConsentPage, IdpLoginPage, IdpSignOutPages } from '../../../support/pages/IdpPages';
import { ShellPage } from '../../../support/pages/ShellPage';
import { signInThroughShell } from '../../../support/sign-in';
import { urls } from '../../../support/services';

/**
 * Sign-out must revoke the IDP's sign-in session ON THE SERVER, not only delete the cookie in
 * the browser. The attacker's view: copy the person's IDP cookies while they are signed in
 * (malware, a shared machine), wait for them to sign out, then replay the copy.
 *
 * With Duende's server-side sessions the copied cookie refers to a session row that sign-out
 * deleted, so the IDP asks for a password. Without them the cookie IS the session and the
 * replay goes straight through (to consent, then into the Shell) - the finding this guards.
 */
test.describe('IDP sign-in session after sign-out', () => {
  test('ShouldRefuse_ACopiedIdpCookie_AfterThePersonSignedOut', async ({ browser }) => {
    // Arrange: Sophie signs in, in a context of her own; the attacker copies her IDP cookies.
    const victim = await browser.newContext();
    const shell = await signInThroughShell(await victim.newPage(), 'sophie.cs');
    const idpHost = new URL(urls.idp).hostname;
    const copiedIdpCookies = (await victim.cookies()).filter(c => c.domain.replace(/^\./, '') === idpHost);
    expect(copiedIdpCookies.length, 'the IDP set its sign-in cookie').toBeGreaterThan(0);

    await shell.signOut();
    await new IdpSignOutPages(shell.page).confirmIfAsked();
    await expect(shell.page.getByRole('heading', { name: "You're signed out", exact: true })).toBeVisible();
    await victim.close();

    // Act: the attacker replays the copied cookies in a fresh browser and opens the Shell.
    const attacker = await browser.newContext();
    await attacker.addCookies(copiedIdpCookies);
    const page = await attacker.newPage();
    await new ShellPage(page).open();
    await page.waitForURL(url => IdpLoginPage.isAt(url) || IdpConsentPage.isAt(url) || ShellPage.isAt(url) && url.pathname !== '/');

    // Assert: the IDP no longer knows the session - it asks for a password.
    const landedOn = new URL(page.url());
    expect(IdpConsentPage.isAt(landedOn), 'the copied IDP cookie still signed Sophie in (went to consent): server-side sessions are off').toBe(false);
    await new IdpLoginPage(page).waitUntilShown();
    await attacker.close();
  });
});