import { expect, test } from '../../../support/fixtures';
import { urls } from '../../../support/services';
import { accountsWebSignInToken, authorizationFor } from '../../../support/tokens';

/**
 * ┌─ :: SECURITY :: A DPoP-bound token is useless without its key ────────────────────────────────
 * │ Why : The staff web apps' tokens are bound to their DPoP key (RFC 9449, FAPI 2.0). A token
 * │       copied from a log, a memory dump or a proxy must not work on its own - and neither may a
 * │       copied proof.
 * │ How : Jack's Accounts web app token (bound). With a fresh proof the Accounts API answers; the
 * │       same token as Bearer is refused (401); the same proof sent again is refused (replay).
 * │ Ref : Features 2.1.1 | API READMEs (DPoP row) | RFC 9449 §4.3, §11.1 | Threat-Model
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
test.describe('DPoP - a bound token needs its key, and a proof works once', () => {
  test('ShouldRefuse_ABoundTokenAsBearer_AndAReplayedProof', async ({ signedInAs, request }) => {
    // Arrange
    const token = await accountsWebSignInToken(await signedInAs('jack.accounts'), request);
    const accounts = `${urls.accountsApi}/v1/accounts?pageSize=1`;
    const withProof = authorizationFor(token, 'GET', accounts);
    expect(withProof.Authorization, 'the Accounts web app\'s token is DPoP-bound').toMatch(/^DPoP /);

    // Act + Assert 1: with its proof, the token works.
    expect((await request.get(accounts, { headers: withProof })).status(), 'token + fresh proof').toBe(200);

    // Act + Assert 2: the same token as a bearer token is refused - and the API says why.
    const asBearer = await request.get(accounts, { headers: { Authorization: `Bearer ${token}` } });
    expect(asBearer.status(), 'a bound token sent as Bearer').toBe(401);
    expect(asBearer.headers()['www-authenticate'] ?? '').toContain('cnf');

    // Act + Assert 3: the proof already used is refused (its jti is remembered).
    expect((await request.get(accounts, { headers: withProof })).status(), 'the same proof again').toBe(401);
  });
});