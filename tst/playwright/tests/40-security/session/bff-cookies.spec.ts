import { expect, test } from '../../../support/fixtures';
import { bffSessionFor } from '../../../support/bff';
import { urls } from '../../../support/services';

/**
 * The anti-forgery cookie of a BFF is HttpOnly, Secure, host-only (__Host-) and SameSite=Lax:
 * script never reads it (the screen gets the request token from /api/auth/csrf), so an XSS bug
 * cannot either, and another site can neither read nor send it. (Replaces the iframe-sandbox
 * check: the sandbox was not adopted - see checklist 02.)
 */
test.describe('BFF cookies', () => {
  test('ShouldKeep_TheAntiForgeryCookie_HttpOnlySecureHostOnlyAndSameSiteLax', async ({ signedInAs }) => {
    // Arrange
    const sophie = await signedInAs('sophie.cs');
    const bff = await bffSessionFor(sophie, urls.paymentsMfe);

    // Act: asking for a request token sets the cookie.
    await bff.csrfToken();
    const cookie = (await sophie.page.context().cookies(urls.paymentsMfe)).find(c => c.name === '__Host-Payments-Bff-CSRF');

    // Assert
    expect(cookie, 'the anti-forgery cookie').toBeTruthy();
    expect(cookie!.httpOnly, 'HttpOnly').toBe(true);
    expect(cookie!.secure, 'Secure').toBe(true);
    expect(cookie!.sameSite).toBe('Lax');
    expect(cookie!.path).toBe('/');
    expect(cookie!.domain.startsWith('.'), 'host-only (no Domain attribute)').toBe(false);
  });
});