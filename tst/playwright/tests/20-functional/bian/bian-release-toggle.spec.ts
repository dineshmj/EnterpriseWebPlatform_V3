import { expect, test } from '../../../support/fixtures';
import { flagFileNotUsedReason, withFlag } from '../../../support/feature-flags';
import { urls } from '../../../support/services';

/**
 * ┌─ :: ARCHITECTURE + SECURITY :: The BIAN /v2 release toggle ────────────────────────────────────
 * │ Why : A new API surface is switched on and off without a redeploy - and while it is off it
 * │       must look like it does not exist, not like "you may not".
 * │ How : No sign-in: an anonymous call to each API's /v2 is 401 while the flag
 * │       bian-v2-endpoints is on (the endpoint exists, the caller is not authenticated) and 404
 * │       while it is off (answered before authentication). /v1 is 401 in both cases - the toggle
 * │       never touches it.
 * │ Ref : feature-flags/README.md | doc/bian/README.md | B8
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
const v2 = [
  `${urls.paymentsApi}/v2/PaymentOrderInitiation/1/Retrieve`,
  `${urls.accountsApi}/v2/CurrentAccount/1/Retrieve`,
  `${urls.customerOnboardingApi}/v2/PartyReferenceDataDirectory/1/Retrieve`,
  `${urls.documentsManagementApi}/v2/DocumentDirectory/00000000-0000-0000-0000-000000000001/Retrieve`,
];
const v1 = `${urls.accountsApi}/v1/accounts?pageSize=1`;

test.describe('BIAN - release toggle (/v2)', () => {
  test.describe.configure({ timeout: 3 * 60_000 });

  test.beforeAll(() => {
    test.skip(!!flagFileNotUsedReason, flagFileNotUsedReason);
  });

  test('ShouldHideEveryV2EndpointWhileTheToggleIsOff_AndLeaveV1Alone', async ({ request }) => {
    // Arrange + Assert: on (as committed) - /v2 exists.
    for (const url of v2) expect((await request.get(url)).status(), url).toBe(401);

    // Act + Assert: off - /v2 is gone, /v1 is not.
    await withFlag('bian-v2-endpoints', 'off', async () => {
      for (const url of v2) {
        const response = await request.get(url);
        expect(response.status(), url).toBe(404);
        expect(await response.text(), url).toBe('');
      }
      expect((await request.get(v1)).status(), v1).toBe(401);
    });

    // Assert: on again - /v2 is back.
    for (const url of v2) expect((await request.get(url)).status(), url).toBe(401);
  });
});