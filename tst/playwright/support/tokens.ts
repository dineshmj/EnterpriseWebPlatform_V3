import { createHash, randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type APIRequestContext, type APIResponse } from '@playwright/test';
import type { ShellPage } from './pages/ShellPage';
import { urls } from './services';

/**
 * Real tokens from the IDP, for the API-level security tests: what a stolen client secret or a
 * compromised service could obtain - and what the IDP and the APIs must still refuse.
 *
 * The DEVELOPMENT-ONLY client secrets are read from the IDP's appsettings.Development.json at run
 * time - never copied into the tests. Outside Development those secrets do not exist in files.
 */
export const clientIds = {
  partnerProductsWeb: 'PartnerProducts.Microservice.Web.ClientID',
  partnerProductsJourneyApi: 'PartnerProducts.JourneyApi.ClientID',
  partnerProductsWorker: 'PartnerProducts.Worker.M2M.ClientID',
  auditWeb: 'Audit.Microservice.Web.ClientID',
  paymentsWeb: 'Payments.Microservice.BFF.ClientID',
  accountsWeb: 'Accounts.Microservice.BFF.ClientID',
  customerOnboardingWeb: 'CustomerOnboarding.Microservice.BFF.ClientID',
  paymentsSagaReplySubscriber: 'Payments.SagaReplySubscriber.To.PaymentsApi.M2M.ClientID',
  kycCaseOpeningSubscriber: 'CustomerKyc.CaseOpeningSubscriber.To.CustomerKycApi.M2M.ClientID',
} as const;

const idpSettings = resolve(__dirname, '../../../src/IDP/appsettings.Development.json');

/** The client's development secret, as the IDP registered it. */
export function devSecretOf(clientId: string): string {
  const secrets = (JSON.parse(readFileSync(idpSettings, 'utf-8')) as { ClientSecrets?: Record<string, string> }).ClientSecrets;
  const secret = secrets?.[clientId];
  expect(secret, `the development secret of ${clientId} (src/IDP/appsettings.Development.json)`).toBeTruthy();
  return secret!;
}

/** A machine's own token (client credentials, no person). */
export async function clientCredentialsToken(http: APIRequestContext, clientId: string, scope: string): Promise<string> {
  const response = await http.post(`${urls.idp}/connect/token`, {
    form: { grant_type: 'client_credentials', client_id: clientId, client_secret: devSecretOf(clientId), scope },
  });
  expect(response.status(), `a client-credentials token for ${clientId} (${scope})`).toBe(200);
  return (await response.json() as { access_token: string }).access_token;
}

/** RFC 8693 token exchange by `clientId` - the raw answer, so a test can check a refusal. */
export function exchange(http: APIRequestContext, clientId: string, subjectToken: string, scope: string): Promise<APIResponse> {
  return http.post(`${urls.idp}/connect/token`, {
    form: {
      grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
      client_id: clientId,
      client_secret: devSecretOf(clientId),
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

/** A BFF client's sign-in token for the signed-in person (Authorization Code + PKCE, prompt=none). */
async function webSignInToken(person: ShellPage, http: APIRequestContext, clientId: string, redirectUri: string, scope: string): Promise<string> {
  const verifier = randomBytes(32).toString('base64url');
  const state = randomBytes(16).toString('base64url');
  const authorize = new URL(`${urls.idp}/connect/authorize`);
  for (const [name, value] of Object.entries({
    client_id: clientId, redirect_uri: redirectUri, response_type: 'code', scope,
    state, nonce: randomBytes(16).toString('base64url'), prompt: 'none',
    code_challenge: createHash('sha256').update(verifier).digest('base64url'), code_challenge_method: 'S256',
  })) authorize.searchParams.set(name, value);

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ prompt=none: the person must already be signed in at the IDP (no password typed here);
  // │ the code is accepted only with this test's PKCE verifier and the client's secret - the
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
      grant_type: 'authorization_code', client_id: clientId, client_secret: devSecretOf(clientId),
      code: code!, redirect_uri: redirectUri, code_verifier: verifier,
    },
  });
  expect(response.status(), `the code is redeemed: ${await response.text()}`).toBe(200);
  return (await response.json() as { access_token: string }).access_token;
}