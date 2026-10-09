import { expect, type FrameLocator, type Locator, type Page } from '@playwright/test';
import { menuAreas } from '../personas';
import { urls } from '../services';

/**
 * The BSS Shell: the top menu (context → area → item, opened by hovering), the user menu,
 * and the frame in which each context's MFE is shown.
 */
export class ShellPage {
  constructor(readonly page: Page) {}

  static isAt(url: URL): boolean {
    return url.origin === urls.shell;
  }

  async open(): Promise<void> {
    await this.page.goto(urls.shell);
  }

  /** The top-level menu buttons, one per context the person may use. */
  contextButtons(): Locator {
    return this.page.locator('nav').getByRole('button');
  }

  contextButton(context: string): Locator {
    return this.page.locator('nav').getByRole('button', { name: new RegExp(`${escape(context)}\\s*▾?$`) });
  }

  /** The menu has loaded (it comes from the Shell BFF after sign-in). */
  async waitForMenu(): Promise<void> {
    await expect(this.contextButtons().first()).toBeVisible({ timeout: 30_000 });
  }

  /** The contexts shown in the menu, by name (the button's own text, without its icon letter and arrow). */
  async contexts(): Promise<string[]> {
    return this.contextButtons().evaluateAll(buttons =>
      buttons.map(b => [...b.childNodes].filter(n => n.nodeType === Node.TEXT_NODE).map(n => n.textContent ?? '').join('').trim()));
  }

  /** The items of one context: hover the context, then each of its areas in turn. */
  async itemsOf(context: string): Promise<string[]> {
    await this.contextButton(context).hover();
    const items = new Set<string>();
    for (const area of new Set(Object.values(menuAreas))) {
      const label = this.page.locator('nav').getByText(area, { exact: true });
      if (await label.count() === 0) continue;          // not an area of this context
      await label.first().hover();
      // The Shell opens an area's items 350 ms after the pointer arrives (AREA_HOVER_DELAY_MS in
      // TopNavMenu.tsx), so wait for them rather than reading at once.
      const links = this.page.locator('nav li a');
      await expect(links.first()).toBeVisible();
      for (const item of await links.allInnerTexts()) items.add(item.replace(/\s*\.\.\.$/, '').trim());
    }
    return [...items];
  }

  /** Opens a menu item: hover its context and area, click the item. */
  async openMenuItem(context: string, item: string): Promise<void> {
    const area = menuAreas[item];
    if (!area) throw new Error(`Unknown menu item "${item}" (add it to menuAreas in personas.ts).`);
    await this.contextButton(context).hover();
    await this.page.locator('nav').getByText(area, { exact: true }).first().hover();
    await this.page.locator('nav').getByRole('link', { name: item, exact: true }).click();
  }

  /** The MFE shown in the workspace frame. */
  workspace(): FrameLocator {
    return this.page.frameLocator('iframe[title="Microservice application workspace"]');
  }

  /**
   * Reloads the MFE in the workspace (its own page, same session) - to see work that arrived
   * through Kafka. Never in the middle of a sign-in: replaying the one-time OIDC callback is
   * refused (401), and a new sign-in renews the session ID, so a request already on its way
   * with the old one is refused too. So: wait until the frame shows an MFE page, navigate to
   * that page, and wait until it has settled again.
   */
  async reloadWorkspace(): Promise<void> {
    const handle = await this.page.locator('iframe[title="Microservice application workspace"]').elementHandle();
    const frame = await handle?.contentFrame();
    if (!frame) throw new Error('No MFE is open in the workspace.');
    await expect.poll(() => isSignInUrl(frame.url()), { timeout: 30_000 }).toBe(false);
    await frame.goto(frame.url());
    await expect.poll(() => isSignInUrl(frame.url()), { timeout: 30_000 }).toBe(false);
  }

  /** The MFE has signed in silently and shows its screen (its title is the frame's level-1 heading). */
  async waitForScreen(title: string): Promise<void> {
    await expect(this.workspace().getByRole('heading', { level: 1, name: title, exact: true })).toBeVisible({ timeout: 30_000 });
  }

  async signOut(): Promise<void> {
    await this.page.locator('button[aria-haspopup="menu"]').click();
    await this.page.getByRole('menuitem', { name: 'Sign out', exact: true }).click();
  }
}

/** A URL of the sign-in round trip (BFF sign-in endpoints, the OIDC callback, the IDP). */
function isSignInUrl(url: string): boolean {
  return /\/api\/auth\/|\/signin-oidc|\/bff\/login|\/connect\/|idp\.dev\.localhost/i.test(url) || url === 'about:blank';
}

function escape(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}