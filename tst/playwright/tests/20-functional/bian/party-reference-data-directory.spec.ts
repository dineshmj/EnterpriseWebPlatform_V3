import type { APIRequestContext, APIResponse } from '@playwright/test';
import { expect, test } from '../../../support/fixtures';
import { newSydneyCustomer } from '../../../support/data';
import { urls } from '../../../support/services';
import { customerOnboardingWebSignInToken } from '../../../support/tokens';

/**
 * ┌─ :: ARCHITECTURE :: The Customer Onboarding API speaks BIAN - shown live, next to its own /v1 ──
 * │ Why : /v2 claims BIAN's PartyReferenceDataDirectory contract (release 14.0.0). The unit tests
 * │       check every body against BIAN's published file; this shows the running API doing it -
 * │       and that /v2 is a second contract on the SAME business path, not a copy.
 * │ How : Sophie registers a customer through /v1 (BIAN's Register is not offered - its body has
 * │       no contact details); BIAN Retrieve and RetrieveReference show what /v1 shows; Mia, of
 * │       another branch, finds nothing (404); an Update without If-Match is refused (428); with
 * │       the ETag it changes the e-mail and /v1 sees it; the old ETag again is refused (412); a
 * │       name change through BIAN is refused (400). Also the demo script for doc/bian/README.md.
 * │ Ref : doc/bian/README.md | Customer Onboarding API README (/v2)
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
interface V1Customer { customerId: number; customerNumber: string; firstName: string; lastName: string; email: string; phoneNumber: string; version: number }
interface Contact { ContactPointType: string; LocationReference: { LocationAddress: string } }
interface Entry { PartyReference: { PartyName: { Name: string }; PartyIdentification: { PartyIdentification: { IdentifierValue: string } } }; PartyType: string }
interface Reference { PartyFullLegalName: { Name: string }; eMailAddress: Contact; CellPhoneNumber: Contact; ResidentialAddress: { TownName: { Name: string } } }

const v1 = `${urls.customerOnboardingApi}/v1/customers`;
const v2 = `${urls.customerOnboardingApi}/v2/PartyReferenceDataDirectory`;
const bearer = (token: string) => ({ Authorization: `Bearer ${token}` });

async function ok<T>(response: APIResponse): Promise<{ body: T; etag: string }> {
  expect(response.status(), await response.text()).toBe(200);
  return { body: await response.json() as T, etag: response.headers()['etag'] ?? '' };
}

function update(http: APIRequestContext, token: string, id: number, body: object, ifMatch?: string): Promise<APIResponse> {
  return http.put(`${v2}/${id}/Reference/primary/Update`, { data: body, headers: { ...bearer(token), ...(ifMatch ? { 'If-Match': ifMatch } : {}) } });
}

test.describe('BIAN - Party Reference Data Directory (/v2)', () => {
  test('ShouldShowAndChangeACustomerThroughBiansContract_OnTheSameBusinessPathAsV1', async ({ signedInAs, request }) => {
    // Arrange: Sophie's and Mia's tokens; Sophie registers a Sydney customer through /v1.
    const sophieToken = await customerOnboardingWebSignInToken(await signedInAs('sophie.cs'), request);
    const miaToken = await customerOnboardingWebSignInToken(await signedInAs('mia.cs'), request);
    const c = newSydneyCustomer('Bian', 'Party');
    const created = await request.post(v1, {
      data: {
        firstName: c.firstName, lastName: c.lastName, email: c.email, phoneNumber: c.phoneNumber, customerType: 1,
        residentialAddress: { addressLine1: c.addressLine1, city: c.city, state: c.state, postalCode: c.postalCode, countryCode: c.countryCode },
      },
      headers: bearer(sophieToken),
    });
    expect(created.status(), await created.text()).toBe(201);
    const { customerId } = await created.json() as { customerId: number };
    const before = (await ok<V1Customer>(await request.get(`${v1}/${customerId}`, { headers: bearer(sophieToken) }))).body;

    // Act + Assert 1: BIAN Retrieve and RetrieveReference show what /v1 shows, each with the version as ETag.
    const entry = await ok<Entry>(await request.get(`${v2}/${customerId}/Retrieve`, { headers: bearer(sophieToken) }));
    expect(entry.body.PartyType).toBe('Person');
    expect(entry.body.PartyReference.PartyName.Name).toBe(`${c.firstName} ${c.lastName}`);
    expect(entry.body.PartyReference.PartyIdentification.PartyIdentification.IdentifierValue).toBe(before.customerNumber);
    expect(entry.etag).toBe(`"${before.version}"`);
    const reference = await ok<Reference>(await request.get(`${v2}/${customerId}/Reference/primary/Retrieve`, { headers: bearer(sophieToken) }));
    expect(reference.body.eMailAddress).toEqual({ ContactPointType: 'ElectronicAddress', LocationReference: { LocationAddress: c.email } });
    expect(reference.body.CellPhoneNumber.LocationReference.LocationAddress).toBe(c.phoneNumber);
    expect(reference.body.ResidentialAddress.TownName.Name).toBe('Sydney');

    // Assert 2: BIAN's door keeps /v1's branch scope - Mia (Melbourne) finds no such customer.
    expect((await request.get(`${v2}/${customerId}/Retrieve`, { headers: bearer(miaToken) })).status(), 'another branch reads').toBe(404);
    expect((await update(request, miaToken, customerId, { eMailAddress: reference.body.eMailAddress }, reference.etag)).status(), 'another branch changes').toBe(404);

    // Assert 3: no If-Match, no change - a change made meanwhile must not be overwritten unseen.
    const newEmail = `new.${c.email}`;
    const change = { eMailAddress: { ContactPointType: 'ElectronicAddress', LocationReference: { LocationAddress: newEmail } } };
    expect((await update(request, sophieToken, customerId, change)).status(), 'Update without If-Match').toBe(428);

    // Act 4: Sophie changes the e-mail through BIAN with the ETag she retrieved.
    const updated = await ok<Reference>(await update(request, sophieToken, customerId, change, reference.etag));
    expect(updated.body.eMailAddress.LocationReference.LocationAddress).toBe(newEmail);
    expect(updated.body.CellPhoneNumber.LocationReference.LocationAddress, 'what was not sent stays').toBe(c.phoneNumber);
    expect(updated.etag).not.toBe(reference.etag);

    // Assert 4: /v1 sees the same change - one customer, one version history.
    const after = (await ok<V1Customer>(await request.get(`${v1}/${customerId}`, { headers: bearer(sophieToken) }))).body;
    expect(after.email).toBe(newEmail);
    expect(updated.etag).toBe(`"${after.version}"`);

    // Assert 5: the old ETag is stale now (412); BIAN's one name string cannot rename (400).
    expect((await update(request, sophieToken, customerId, change, reference.etag)).status(), 'stale ETag').toBe(412);
    const rename = await update(request, sophieToken, customerId, { PartyFullLegalName: { Name: `${c.firstName} Renamed` }, ...change }, updated.etag);
    expect(rename.status(), await rename.text()).toBe(400);
  });
});