import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import type { APIRequestContext, APIResponse } from '@playwright/test';
import { expect, test } from '../../../support/fixtures';
import { urls } from '../../../support/services';
import { clientIds, customerKycWebSignInToken, customerOnboardingWebSignInToken, exchanged } from '../../../support/tokens';

/**
 * ┌─ :: ARCHITECTURE :: The Documents Management API speaks BIAN - shown live, next to its own /v1 ─
 * │ Why : /v2 claims BIAN's DocumentDirectory contract (release 14.0.0). The unit tests check every
 * │       body against BIAN's published file; this shows the running API doing it - and that /v2
 * │       is a second contract on the SAME business path and branch scope, not a copy.
 * │ How : Sophie (Sydney) and Mia (Melbourne) upload a proof of identity through /v1 with the
 * │       Customer Onboarding BFF's token exchange (BIAN's Register carries no file); Liam, a
 * │       Sydney KYC officer, reads through the KYC BFF's exchange: BIAN Retrieve and
 * │       RetrieveDocumentReferenceProperties show what /v1 shows for Sophie's document; Mia's
 * │       does not exist for him (404), nor does another reference-properties id. The uploads are
 * │       removed again (still AVAILABLE). Also the demo script for doc/bian/README.md.
 * │ Ref : doc/bian/README.md | Documents Management API README (/v2)
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
interface V1Document { documentId: string; status: string; version: number; createdAt: string }
interface Entry { DocumentDirectoryEntryIdentification: string; DirectoryEntryDateType: string; DirectoryEntryDate: { DateContent: string }; DirectoryEntryStatus: { StatusReason: string } }
interface ReferenceProperties { VersionNumber: string; DocumentDirectoryEntryReference: { DocumentDirectoryEntryIdentification: string }; DocumentReferencePropertiesType: string }

const v1 = `${urls.documentsManagementApi}/v1/documents`;
const v2 = `${urls.documentsManagementApi}/v2/DocumentDirectory`;
const bearer = (token: string) => ({ Authorization: `Bearer ${token}` });
const identityProof = resolve(__dirname, '../../../fixtures/documents/camilla-parker/identity-proof.pdf');

async function ok<T>(response: APIResponse): Promise<T> {
  expect(response.status(), await response.text()).toBe(200);
  return await response.json() as T;
}

async function upload(http: APIRequestContext, token: string): Promise<string> {
  const response = await http.post(v1, {
    multipart: { File: { name: 'identity-proof.pdf', mimeType: 'application/pdf', buffer: readFileSync(identityProof) } },
    headers: { ...bearer(token), 'X-Document-Type': 'KYCProof' },
  });
  expect(response.status(), await response.text()).toBe(201);
  return (await response.json() as { documentId: string }).documentId;
}

test.describe('BIAN - Document Directory (/v2)', () => {
  test('ShouldShowADocumentThroughBiansContract_OnTheSameBusinessPathAndBranchScopeAsV1', async ({ signedInAs, request }) => {
    // Arrange: Sophie (SYD001) and Mia (MEL001) upload through the CO BFF's exchange; Liam (SYD001, KYC) reads through the KYC BFF's.
    const sophieUpload = await exchanged(request, clientIds.customerOnboardingWeb,
      await customerOnboardingWebSignInToken(await signedInAs('sophie.cs'), request), 'documents-management.write');
    const miaUpload = await exchanged(request, clientIds.customerOnboardingWeb,
      await customerOnboardingWebSignInToken(await signedInAs('mia.cs'), request), 'documents-management.write');
    const liamRead = await exchanged(request, clientIds.customerKycWeb,
      await customerKycWebSignInToken(await signedInAs('liam.kyc'), request), 'documents-management.read');
    const sophiesDocument = await upload(request, sophieUpload);
    const miasDocument = await upload(request, miaUpload);

    try {
      const before = await ok<V1Document>(await request.get(`${v1}/${sophiesDocument}`, { headers: bearer(liamRead) }));

      // Act + Assert 1: BIAN Retrieve shows what /v1 shows.
      const entry = await ok<Entry>(await request.get(`${v2}/${sophiesDocument}/Retrieve`, { headers: bearer(liamRead) }));
      expect(entry.DocumentDirectoryEntryIdentification).toBe(sophiesDocument);
      expect(entry.DirectoryEntryDateType).toBe('OpenDate');
      expect(entry.DirectoryEntryDate.DateContent).toBe(new Date(before.createdAt).toISOString().slice(0, 10));
      expect(entry.DirectoryEntryStatus.StatusReason).toBe(before.status);

      // Act + Assert 2: RetrieveDocumentReferenceProperties - the version and the type.
      const reference = await ok<ReferenceProperties>(
        await request.get(`${v2}/${sophiesDocument}/DocumentReferenceProperties/primary/Retrieve`, { headers: bearer(liamRead) }));
      expect(reference.VersionNumber).toBe(String(before.version));
      expect(reference.DocumentReferencePropertiesType).toBe('KYCProof');
      expect(reference.DocumentDirectoryEntryReference.DocumentDirectoryEntryIdentification).toBe(sophiesDocument);

      // Act + Assert 3: another branch's document does not exist for Liam - in /v2 as in /v1 (404, not 403).
      expect((await request.get(`${v2}/${miasDocument}/Retrieve`, { headers: bearer(liamRead) })).status()).toBe(404);
      expect((await request.get(`${v2}/${miasDocument}/DocumentReferenceProperties/primary/Retrieve`, { headers: bearer(liamRead) })).status()).toBe(404);
      expect((await request.get(`${v1}/${miasDocument}`, { headers: bearer(liamRead) })).status()).toBe(404);

      // Act + Assert 4: EWP keeps one reference record per document - any other id is not found.
      expect((await request.get(`${v2}/${sophiesDocument}/DocumentReferenceProperties/other/Retrieve`, { headers: bearer(liamRead) })).status()).toBe(404);
    } finally {
      // Cleanup: both uploads are still AVAILABLE, so the uploading agent may remove them.
      expect((await request.delete(`${v1}/${sophiesDocument}`, { headers: bearer(sophieUpload) })).status()).toBe(204);
      expect((await request.delete(`${v1}/${miasDocument}`, { headers: bearer(miaUpload) })).status()).toBe(204);
    }
  });
});