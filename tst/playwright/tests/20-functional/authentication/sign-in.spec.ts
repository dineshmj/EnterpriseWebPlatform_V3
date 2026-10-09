import { expect, test } from '../../../support/fixtures';
import { IdpConsentPage, IdpLoginPage, IdpSignOutPages } from '../../../support/pages/IdpPages';
import { ShellPage } from '../../../support/pages/ShellPage';
import { allContexts, passwordOf, personas } from '../../../support/personas';
import { urls } from '../../../support/services';

test.describe('Sign-in through the Shell', () => {
  test('Sophie signs in step by step and lands on the Shell with her menu', async ({ page }) => {
    // Arrange
    const shell = new ShellPage(page);
    const login = new IdpLoginPage(page);

    // Act + Assert, step by step
    await test.step('Opening the Shell redirects to the IDP sign-in page', async () => {
      await shell.open();
      await login.waitUntilShown();
      expect(new URL(page.url()).origin).toBe(urls.idp);
    });

    await test.step('Username and password are accepted', async () => {
      await login.signIn('sophie.cs', passwordOf('sophie.cs'));
      await page.waitForURL(url => IdpConsentPage.isAt(url) || ShellPage.isAt(url));
    });

    await test.step('Consent is given when the IDP asks for it', async () => {
      if (IdpConsentPage.isAt(new URL(page.url()))) {
        await expect(page.getByRole('button', { name: 'Allow', exact: true })).toBeVisible();
        await new IdpConsentPage(page).allow();
      } else {
        test.info().annotations.push({ type: 'note', description: 'Consent was not asked (already remembered for sophie.cs).' });
      }
    });

    await test.step('Back in the Shell, the menu appears', async () => {
      await page.waitForURL(url => ShellPage.isAt(url));
      await shell.waitForMenu();
      expect((await shell.contexts()).sort()).toEqual(Object.keys(personas['sophie.cs'].menu).sort());
    });

    await test.step('A menu item opens its screen in the workspace', async () => {
      await shell.openMenuItem('Customer Onboarding', 'View Customers');
      await shell.waitForScreen('Customers');
    });
  });

  // An unknown username, not a real user's wrong password: failed attempts count towards a real
  // user's lockout (5 → 15 minutes), and the IDP must answer both the same way anyway.
  test('An unknown username is refused with the same message as a wrong password', async ({ page }) => {
    // Arrange
    const shell = new ShellPage(page);
    const login = new IdpLoginPage(page);
    await shell.open();
    await login.waitUntilShown();

    // Act
    await login.signIn(`nobody.${Date.now()}`, 'not-a-password');

    // Assert
    await expect(page.getByText('Invalid username or password')).toBeVisible();
    expect(IdpLoginPage.isAt(new URL(page.url()))).toBe(true);
  });

  test('Signing out ends the session: the Shell asks for a new sign-in', async ({ signedInAs }) => {
    // Arrange
    const shell = await signedInAs('sophie.cs');
    const page = shell.page;

    // Act
    await shell.signOut();
    await new IdpSignOutPages(page).confirmIfAsked();

    // Assert
    await expect(page.getByRole('heading', { name: "You're signed out", exact: true })).toBeVisible();
    await shell.open();
    await new IdpLoginPage(page).waitUntilShown();
  });
});

test.describe('Each person sees exactly the menus of their role', () => {
  for (const persona of Object.values(personas)) {
    test(`${persona.username} (${persona.role})`, async ({ signedInAs }) => {
      // Arrange
      const shell = await signedInAs(persona.username);
      const expectedContexts = Object.keys(persona.menu).sort();

      // Act
      const contexts = (await shell.contexts()).sort();

      // Assert
      expect(contexts, 'menu contexts').toEqual(expectedContexts);
      for (const hidden of allContexts.filter(c => !expectedContexts.includes(c))) {
        await expect(shell.contextButton(hidden), `${hidden} must not be offered`).toHaveCount(0);
      }
      for (const [context, items] of Object.entries(persona.menu)) {
        expect((await shell.itemsOf(context)).sort(), `items of ${context}`).toEqual([...items].sort());
      }
    });
  }
});