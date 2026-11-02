import type { WebSocket } from '@playwright/test';
import { expect, test } from '../../../support/fixtures';
import { fundedCustomer, payees, startPayment } from '../../../support/journeys/payments';
import { resetAllSimulators } from '../../../support/simulators';

/**
 * ┌─ :: ARCHITECTURE + SECURITY :: The bell is pushed live - over a WebSocket, no reload ──────────
 * │ Why : The other notification journeys reload the Shell between looks, so they would still pass
 * │       if the live channel fell back to polling or failed. The push itself is a feature: the
 * │       browser -> Shell BFF -> Notifications API hub chain, where the BFF adds the person's
 * │       DPoP-bound token and a proof for the WebSocket upgrade (RFC 9449).
 * │ How : Emily's Shell opens the hub over a WebSocket and completes SignalR's handshake; Sophie
 * │       starts a payment above the approval tier; Emily's "new work" notification arrives as a
 * │       frame on THAT socket and shows in the bell - the page is never reloaded.
 * │ Ref : Features 1.5.4 (SignalR), 2.1.1 (DPoP) | Shell README | Notifications API README
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
const RECORD_SEPARATOR = '\u001e';

test.describe('Notifications - pushed live', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('ShouldPushNewWorkToTheBell_OverTheHubsWebSocket_WithoutAReload', async ({ signedInAs }) => {
    // Arrange: Emily's Shell, reloaded once with the socket watched from the start.
    const customer = await fundedCustomer(signedInAs, 'LivePush');
    const emily = await signedInAs('emily.payments');
    const frames: string[] = [];
    const hub = emily.page.waitForEvent('websocket', {
      predicate: (socket: WebSocket) => socket.url().includes('/hubs/notifications'),
    });
    emily.page.on('websocket', (socket) => {
      if (socket.url().includes('/hubs/notifications'))
        socket.on('framereceived', ({ payload }) => frames.push(payload.toString()));
    });
    await emily.page.reload();
    await emily.waitForMenu();
    const socket = await hub;

    // Assert 1: SignalR's handshake answered on the socket - the upgrade passed the BFF and the API.
    await expect.poll(() => frames.some((f) => f.startsWith(`{}${RECORD_SEPARATOR}`)), {
      message: 'the hub answers the SignalR handshake over the WebSocket', timeout: 30_000,
    }).toBe(true);

    // Act: Sophie starts a payment above the approval tier - new work for the payments officers.
    await startPayment(await signedInAs('sophie.cs'), customer, payees.matching, '1300.00', 'Live push');

    // Assert 2: the notification arrives as a frame on the same socket, and the bell shows it.
    await expect.poll(() => frames.some((f) => f.includes('"target":"notification"') && f.includes(customer)), {
      message: `the "${customer} needs approval" notification pushed over the WebSocket`, timeout: 2 * 60_000,
    }).toBe(true);
    expect(socket.isClosed(), 'the same socket is still open').toBe(false);
    await emily.page.getByRole('button', { name: /^Notifications/ }).click();
    await expect(emily.page.getByRole('dialog', { name: 'Notifications' }))
      .toContainText(`${customer} needs a payments officer's approval`);
  });
});