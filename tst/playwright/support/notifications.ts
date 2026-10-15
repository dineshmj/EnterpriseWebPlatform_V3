import { expect } from '@playwright/test';
import { WORKFLOW_WAIT } from './pages/mfes';
import type { ShellPage } from './pages/ShellPage';

/**
 * Waits until the person's bell holds a notification containing `text` (it arrives through Kafka
 * and the Notifications service). Opens the bell's panel, and reloads the Shell between looks.
 */
export async function expectNotification(person: ShellPage, text: string | RegExp): Promise<void> {
  const page = person.page;
  await expect(async () => {
    const panel = page.getByRole('dialog', { name: 'Notifications' });
    if (!(await panel.isVisible())) {
      await page.getByRole('button', { name: /^Notifications/ }).click();
    }
    try {
      await expect(panel).toContainText(text, { timeout: 3_000 });
    } catch (error) {
      await page.keyboard.press('Escape');
      await page.reload();
      await person.waitForMenu();
      throw error;
    }
  }).toPass(WORKFLOW_WAIT);
}