import { test as base, expect, type BrowserContext } from '@playwright/test';
import type { ShellPage } from './pages/ShellPage';
import type { PersonaName } from './personas';
import { contextFor } from './sign-in';

interface Fixtures {
  /**
   * The Shell signed in as this person, in a context of its own. Call it more than once for
   * several people at the same time - e.g. Sophie submits while Ethan watches his KYC queue:
   *
   *   const sophie = await signedInAs('sophie.cs');
   *   const ethan  = await signedInAs('ethan.kyc');
   */
  signedInAs: (username: PersonaName) => Promise<ShellPage>;
}

export const test = base.extend<Fixtures>({
  signedInAs: async ({ browser }, use) => {
    const contexts: BrowserContext[] = [];
    await use(async username => {
      const { context, shell } = await contextFor(browser, username);
      contexts.push(context);
      return shell;
    });
    await Promise.all(contexts.map(c => c.close()));
  },
});

export { expect };