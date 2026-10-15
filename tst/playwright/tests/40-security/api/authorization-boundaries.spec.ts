import { expect, test } from '../../../support/fixtures';
import { bffSessionFor } from '../../../support/bff';
import { urls } from '../../../support/services';

/**
 * The menu is not the security boundary: a person signed in to an MFE's BFF without the role for
 * it is refused by the domain API (403), even when calling the BFF's API directly. Doc:
 * BSS-Requirements.md section 15.
 */
test.describe('Authorization - the API decides, not the menu', () => {
  test('ShouldRefuse_AnAuditorListingPayments', async ({ signedInAs }) => {
    // Arrange
    const bff = await bffSessionFor(await signedInAs('sarah.audit'), urls.paymentsMfe);

    // Act
    const response = await bff.request.get(`${urls.paymentsMfe}/bff/api/payments`);

    // Assert
    expect(response.status()).toBe(403);
  });

  test('ShouldRefuse_ACustomerServiceAgentListingComplianceCases', async ({ signedInAs }) => {
    // Arrange
    const bff = await bffSessionFor(await signedInAs('sophie.cs'), urls.complianceMfe);

    // Act
    const response = await bff.request.get(`${urls.complianceMfe}/bff/api/compliance/cases`);

    // Assert
    expect(response.status()).toBe(403);
  });
});