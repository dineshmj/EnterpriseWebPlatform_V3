import { createPrivateKey, createPublicKey, KeyObject, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { calculateJwkThumbprint, exportJWK, SignJWT } from 'jose';
import { Client, generators, Issuer, TokenSet } from 'openid-client';
import { config } from './config';
import { SessionData } from './session-store';

let clientPromise: Promise<Client> | undefined;

// ┌─ :: SECURITY :: OIDC client and the first token exchange ──────────────────────────────────────
// │ Why : The browser must never hold a token, and the Journey API must know which person is asking
// │       through which app.
// │ How : Authorization Code + PKCE with state and nonce, pushed to the IDP first (PAR); the BFF
// │       proves itself with private_key_jwt (no client secret); tokens
// │       stay in the server-side session and are refreshed there; each call exchanges the
// │       person's token (RFC 8693) for a Journey API token naming this BFF as "act".
// │ Ref : Features 2.1.1, 2.1.2 | RFC 7636 | RFC 9126 (PAR) | RFC 8693 |
// │       IETF OAuth for Browser-Based Apps
// └────────────────────────────────────────────────────────────────────────────────────────────────
/** The IDP client of this light BFF (discovered once; retried after a failure). */
export function oidcClient(): Promise<Client> {
  clientPromise ??= Issuer.discover(config.authority)
    .then((issuer) => new issuer.Client({
      client_id: config.clientId,
      // No client authentication by openid-client: every call adds clientAuthentication() below.
      token_endpoint_auth_method: 'none',
      redirect_uris: [config.callbackUrl],
      response_types: ['code'],
    }))
    .catch((error) => {
      clientPromise = undefined;
      throw error;
    });
  return clientPromise;
}

/** What the sign-in round trip must remember; kept in an encrypted, short-lived cookie. */
export interface PendingSignIn {
  state: string;
  nonce: string;
  codeVerifier: string;
  returnUrl: string;
}

/**
 * The authorization request: code + PKCE. Sign-in asks only for identity and refresh: the
 * token this BFF holds is good for nothing but being exchanged (it carries no API scope).
 * prompt=none in the Shell's frame: the IDP's SSO session signs the person in silently.
 */
export async function authorizationRequest(returnUrl: string, silent: boolean): Promise<{ url: string; pending: PendingSignIn }> {
  const client = await oidcClient();
  const pending: PendingSignIn = {
    state: generators.state(),
    nonce: generators.nonce(),
    codeVerifier: generators.codeVerifier(),
    returnUrl,
  };
  // ┌─ SEC ──────────────────────────────────────────────────────────────────────────────────────
  // │ PAR (RFC 9126, FAPI 2.0): the request is pushed to the IDP with a client assertion; the
  // │ browser is sent only client_id + a one-time request_uri (60 s) - nothing to read or change.
  // └────────────────────────────────────────────────────────────────────────────────────────────
  const pushed = await client.pushedAuthorizationRequest({
    ...(await clientAuthentication(client)),
    scope: 'openid profile roles organization offline_access',
    response_mode: 'query',
    state: pending.state,
    nonce: pending.nonce,
    code_challenge: generators.codeChallenge(pending.codeVerifier),
    code_challenge_method: 'S256',
    ...(silent ? { prompt: 'none' } : {}),
  });
  const url = new URL(String(client.issuer.metadata.authorization_endpoint));
  url.searchParams.set('client_id', config.clientId);
  url.searchParams.set('request_uri', pushed.request_uri);
  return { url: url.href, pending };
}

export function toSession(tokens: TokenSet): SessionData {
  const claims = tokens.claims();
  return {
    sub: String(claims.sub),
    sid: typeof claims.sid === 'string' ? claims.sid : undefined,
    lanId: typeof claims.lan_id === 'string' ? claims.lan_id : undefined,
    name: typeof claims.name === 'string' ? claims.name : undefined,
    idToken: tokens.id_token!,
    accessToken: tokens.access_token!,
    accessTokenExpiresAt: (tokens.expires_at ?? Math.floor(Date.now() / 1000) + 300) * 1000,
    refreshToken: tokens.refresh_token,
  };
}

/** The person's own access token, refreshed a minute before it expires. */
export async function freshAccessToken(session: SessionData): Promise<{ token: string; refreshed?: SessionData }> {
  if (session.accessTokenExpiresAt > Date.now() + 60_000 || !session.refreshToken) {
    return { token: session.accessToken };
  }
  const client = await oidcClient();
  const tokens = await client.refresh(session.refreshToken, { exchangeBody: await clientAuthentication(client) });
  const refreshed: SessionData = {
    ...session,
    accessToken: tokens.access_token!,
    accessTokenExpiresAt: (tokens.expires_at ?? Math.floor(Date.now() / 1000) + 300) * 1000,
    refreshToken: tokens.refresh_token ?? session.refreshToken,
  };
  return { token: refreshed.accessToken, refreshed };
}

/**
 * Hop 1 of the customer's pattern: OAuth 2.0 Token Exchange (RFC 8693). The person's token is
 * swapped for a short-lived one aimed at the Audit Journey API - the person stays the subject,
 * and this BFF is named as the acting client ("act"). The Journey API accepts nothing else.
 */
export async function exchangeForJourneyApi(personAccessToken: string): Promise<string> {
  const client = await oidcClient();
  const tokens = await client.grant({
    ...(await clientAuthentication(client)),
    grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
    subject_token: personAccessToken,
    subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
    scope: 'audit-journey.read',
  });
  return tokens.access_token!;
}

let signingKeyPromise: Promise<{ key: KeyObject; kid: string }> | undefined;

// ┌─ :: SECURITY :: private_key_jwt: this BFF's client assertions ─────────────────────────────────
// │ Why : A shared client secret travels with every token request and the IDP knows it too;
// │       FAPI 2.0 asks a confidential client to prove itself with an asymmetric key instead.
// │ How : The EC P-256 private key (encrypted PKCS#8 PEM, its password in a second file) is read
// │       once. Every call to the IDP gets a new assertion: iss = sub = the client ID, aud = the
// │       IDP's issuer only, typ client-authentication+jwt (openid-client 5 cannot set it - so
// │       this BFF signs it and openid-client sends no client authentication of its own), 60 s, a
// │       fresh jti (the IDP takes each one once), ES256, kid = the RFC 7638 thumbprint
// │       New-EwpClientKeys.mjs gave the public key.
// │ Ref : Features 2.1.1 | FAPI 2.0 Security Profile §5.3.1 | RFC 7523, 7638 |
// │       draft-ietf-oauth-rfc7523bis | Threat-Model R-10
// └────────────────────────────────────────────────────────────────────────────────────────────────
/** The client authentication for ONE call to the IDP (form parameters). */
export async function clientAuthentication(client: Client): Promise<{ client_assertion_type: string; client_assertion: string }> {
  signingKeyPromise ??= loadSigningKey().catch((error) => {
    signingKeyPromise = undefined;
    throw error;
  });
  const { key, kid } = await signingKeyPromise;
  const now = Math.floor(Date.now() / 1000);
  const assertion = await new SignJWT({})
    .setProtectedHeader({ alg: 'ES256', typ: 'client-authentication+jwt', kid })
    .setIssuer(config.clientId)
    .setSubject(config.clientId)
    .setAudience(String(client.issuer.metadata.issuer))
    .setIssuedAt(now)
    .setNotBefore(now)
    .setExpirationTime(now + 60)
    .setJti(randomUUID())
    .sign(key);
  return { client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer', client_assertion: assertion };
}

async function loadSigningKey(): Promise<{ key: KeyObject; kid: string }> {
  const key = createPrivateKey({
    key: readFileSync(config.clientKeyPath),
    format: 'pem',
    passphrase: readFileSync(config.clientKeyPasswordPath, 'utf-8').trim(),
  });
  if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails?.namedCurve !== 'prime256v1') {
    throw new Error('The client key must be an EC P-256 key (ES256).');
  }
  return { key, kid: await calculateJwkThumbprint(await exportJWK(createPublicKey(key))) };
}