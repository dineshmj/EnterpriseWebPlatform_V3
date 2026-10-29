import { createHash, createPrivateKey, randomBytes, randomUUID, sign } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import type { ShellPage } from './pages/ShellPage';
import { urls } from './services';

/**
 * Real tokens from the IDP, for the API-level security tests: what a stolen client credential or a
 * compromised service could obtain - and what the IDP and the APIs must still refuse.
 *
 * The DEVELOPMENT-ONLY client credentials are found through the IDP's appsettings.Development.json
 * at run time - never copied into the tests: a client secret (machines), or the path of a
 * private_key_jwt client's public key, whose private key lies next to it (the staff web apps:
 * ps/pki/New-EwpLocalPki.ps1). Outside Development neither exists in files.
 */
export const clientIds = {
  partnerProductsWeb: 'PartnerProducts.Microservice.Web.ClientID',
  partnerProductsJourneyApi: 'PartnerProducts.JourneyApi.ClientID',
  partnerProductsWorker: 'PartnerProducts.Worker.M2M.ClientID',
  auditWeb: 'Audit.Microservice.Web.ClientID',
  paymentsWeb: 'Payments.Microservice.BFF.ClientID',
  accountsWeb: 'Accounts.Microservice.BFF.ClientID',
  customerOnboardingWeb: 'CustomerOnboarding.Microservice.BFF.ClientID',
  customerKycWeb: 'CustomerKYC.Microservice.BFF.ClientID',
  paymentsSagaReplySubscriber: 'Payments.SagaReplySubscriber.To.PaymentsApi.M2M.ClientID',
  kycCaseOpeningSubscriber: 'CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID',
} as const;

const idpSettings = resolve(__dirname, '../../../src/IDP/appsettings.Development.json');

let issuer: Promise<string> | undefined;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The client proves itself the way the IDP registered it: private_key_jwt for the staff
// │ web apps (an assertion signed with the client's own key, as the BFFs build it: aud =
// │ the issuer only, typ client-authentication+jwt, 60 s, a new jti), else its secret.
// └──────────────────────────────────────────────────────────────────────────────────────
/** The client authentication form parameters for ONE call to the IDP. */
export async function clientAuthenticationOf(http: APIRequestContext, clientId: string): Promise<Record<string, string>> {
  const settings = JSON.parse(readFileSync(idpSettings, 'utf-8')) as { ClientSecrets?: Record<string, string>; ClientKeys?: Record<string, string> };
  const publicKeyPath = settings.ClientKeys?.[clientId];
  if (!publicKeyPath) {
    const secret = settings.ClientSecrets?.[clientId];
    expect(secret, `the development secret or key of ${clientId} (src/IDP/appsettings.Development.json)`).toBeTruthy();
    return { client_secret: secret! };
  }

  issuer ??= http.get(`${urls.idp}/.well-known/openid-configuration`)
    .then(async (response) => (await response.json() as { issuer: string }).issuer);
  const keyPath = publicKeyPath.replace(/%USERPROFILE%/gi, process.env.USERPROFILE ?? '').replace(/\.public\.jwk$/, '.key.pem');
  const key = createPrivateKey({ key: readFileSync(keyPath), format: 'pem', passphrase: readFileSync(`${keyPath}.password`, 'utf-8').trim() });
  const { kid } = JSON.parse(readFileSync(publicKeyPath.replace(/%USERPROFILE%/gi, process.env.USERPROFILE ?? ''), 'utf-8')) as { kid: string };
  const now = Math.floor(Date.now() / 1000);
  const part = (value: object) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const unsigned = `${part({ alg: 'ES256', typ: 'client-authentication+jwt', kid })}.${part({
    iss: clientId, sub: clientId, aud: await issuer, iat: now, nbf: now, exp: now + 60, jti: randomUUID(),
  })}`;
  const signature = sign('sha256', Buffer.from(unsigned), { key, dsaEncoding: 'ieee-p1363' }).toString('base64url');
  return {
    client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
    client_assertion: `${unsigned}.${signature}`,
  };
}

/** A machine's own token (client credentials, no person). */
export async function clientCredentialsToken(http: APIRequestContext, clientId: string, scope: string): Promise<string> {
  const response = await http.post(`${urls.idp}/connect/token`, {
    form: { grant_type: 'client_credentials', client_id: clientId, ...await clientAuthenticationOf(http, clientId), scope },
  });
  expect(response.status(), `a client-credentials token for ${clientId} (${scope})`).toBe(200);
  return (await response.json() as { access_token: string }).access_token;
}

/** RFC 8693 token exchange by `clientId` - the raw answer, so a test can check a refusal. */
export async function exchange(http: APIRequestContext, clientId: string, subjectToken: string, scope: string): Promise<APIResponse> {
  return http.post(`${urls.idp}/connect/token`, {
    form: {
      grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
      client_id: clientId,
      ...await clientAuthenticationOf(http, clientId),
      subject_token: subjectToken,
      subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
      scope,
    },
  });
}

/** A token exchange that must succeed; returns the new access token. */
export async function exchanged(http: APIRequestContext, clientId: string, subjectToken: string, scope: string): Promise<string> {
  const response = await exchange(http, clientId, subjectToken, scope);
  expect(response.status(), `${clientId} exchanges for ${scope}: ${await response.text()}`).toBe(200);
  return (await response.json() as { access_token: string }).access_token;
}

/** The claims of a JWT (decoded, NOT verified - the APIs verify; the tests only read). */
export function claimsOf(jwt: string): Record<string, unknown> {
  return JSON.parse(Buffer.from(jwt.split('.')[1], 'base64url').toString('utf-8')) as Record<string, unknown>;
}

/**
 * The person's own sign-in token of the Partner Products web BFF - the token that BFF holds on
 * the server after sign-in. Obtained the way the BFF does (Authorization Code + PKCE, silently
 * through the person's IDP session in this browser context); the redirect back to the BFF is
 * caught before it arrives, so the BFF's own session is untouched.
 */
export function partnerWebSignInToken(person: ShellPage, http: APIRequestContext): Promise<string> {
  return webSignInToken(person, http, clientIds.partnerProductsWeb, `${urls.partnerProductsMfe}/api/auth/callback`, 'openid profile roles organization');
}

/**
 * The person's own Payments BFF sign-in token, with the scopes the BFF signs in with (payments and
 * accounts - the counter's cash deposit goes to the Accounts API): the token it calls those APIs
 * with - for the tests that call them directly (e.g. their BIAN /v2 contracts). Same silent flow
 * as partnerWebSignInToken.
 */
export function paymentsWebSignInToken(person: ShellPage, http: APIRequestContext): Promise<string> {
  return webSignInToken(person, http, clientIds.paymentsWeb, `${urls.paymentsMfe}/signin-oidc`,
    'openid profile roles organization payments.read payments.write accounts.read accounts.write');
}

/** The person's own Accounts BFF sign-in token (an account officer's view of the Accounts API). */
export function accountsWebSignInToken(person: ShellPage, http: APIRequestContext): Promise<string> {
  return webSignInToken(person, http, clientIds.accountsWeb, `${urls.accountsMfe}/signin-oidc`, 'openid profile roles organization accounts.read accounts.write');
}

/** The person's own Customer Onboarding BFF sign-in token (an agent's view of the Customer Onboarding API). */
export function customerOnboardingWebSignInToken(person: ShellPage, http: APIRequestContext): Promise<string> {
  return webSignInToken(person, http, clientIds.customerOnboardingWeb, `${urls.customerOnboardingMfe}/signin-oidc`,
    'openid profile roles organization customer-onboarding.read customer-onboarding.write');
}

/** The person's own Customer KYC BFF sign-in token (a KYC officer's; exchanged for Documents Management read). */
export function customerKycWebSignInToken(person: ShellPage, http: APIRequestContext): Promise<string> {
  return webSignInToken(person, http, clientIds.customerKycWeb, `${urls.kycMfe}/api/auth/callback`, 'openid profile roles organization customer-kyc.read');
}

/** A BFF client's sign-in token for the signed-in person (PAR + Authorization Code + PKCE, prompt=none). */
async function webSignInToken(person: ShellPage, http: APIRequestContext, clientId: string, redirectUri: string, scope: string): Promise<string> {
  const verifier = randomBytes(32).toString('base64url');
  const state = randomBytes(16).toString('base64url');

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ PAR first, like the BFFs: the staff clients REQUIRE it (RFC 9126, FAPI 2.0), so the
  // │ request is pushed with a client assertion and the browser leg carries only client_id
  // │ and the one-time request_uri.
  // └──────────────────────────────────────────────────────────────────────────────────────
  const pushed = await http.post(`${urls.idp}/connect/par`, {
    form: {
      client_id: clientId, ...await clientAuthenticationOf(http, clientId), redirect_uri: redirectUri, response_type: 'code', scope,
      state, nonce: randomBytes(16).toString('base64url'), prompt: 'none',
      code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
    },
  });
  expect(pushed.status(), `the authorization request is pushed: ${await pushed.text()}`).toBe(201);
  const authorize = new URL(`${urls.idp}/connect/authorize`);
  authorize.searchParams.set('client_id', clientId);
  authorize.searchParams.set('request_uri', (await pushed.json() as { request_uri: string }).request_uri);

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ prompt=none: the person must already be signed in at the IDP (no password typed here);
  // │ the code is accepted only with this test's PKCE verifier and a client assertion - the
  // │ same proof the BFF itself gives.
  // └──────────────────────────────────────────────────────────────────────────────────────
  // The context's own request client carries the person's IDP cookies; the redirect is NOT
  // followed (a page.route handler never sees the redirected request), so the code is read from
  // the IDP's Location header and never reaches the BFF.
  const answer = await person.page.context().request.get(authorize.toString(), { maxRedirects: 0 });
  expect([302, 303], `the IDP redirects back with a code (got ${answer.status()})`).toContain(answer.status());
  const back = new URL(answer.headers()['location'] ?? '', urls.idp);
  expect(`${back.origin}${back.pathname}`, 'back to the registered redirect URI').toBe(redirectUri);
  expect(back.searchParams.get('error'), 'silent sign-in (is the person signed in at the IDP?)').toBeNull();
  expect(back.searchParams.get('state'), 'the same state').toBe(state);
  const code = back.searchParams.get('code');
  expect(code, 'an authorization code from the IDP (silent sign-in)').toBeTruthy();

  const response = await http.post(`${urls.idp}/connect/token`, {
    form: {
      grant_type: 'authorization_code', client_id: clientId, ...await clientAuthenticationOf(http, clientId),
      code: code!, redirect_uri: redirectUri, code_verifier: verifier,
    },
  });
  expect(response.status(), `the code is redeemed: ${await response.text()}`).toBe(200);
  return (await response.json() as { access_token: string }).access_token;
}