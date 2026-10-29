import { Client, generators, Issuer, TokenSet } from 'openid-client';
import { config } from './config';
import { SessionData } from './session-store';

let clientPromise: Promise<Client> | undefined;

// ┌─ :: SECURITY :: OIDC client and the first token exchange ──────────────────────────────────────
// │ Why : The browser must never hold a token, and the Journey API must know which person is asking
// │       through which app.
// │ How : Authorization Code + PKCE with state and nonce, pushed to the IDP first (PAR); tokens
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
      client_secret: config.clientSecret,
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
  // │ PAR (RFC 9126, FAPI 2.0): the request is pushed to the IDP with the client secret; the
  // │ browser is sent only client_id + a one-time request_uri (60 s) - nothing to read or change.
  // └────────────────────────────────────────────────────────────────────────────────────────────
  const pushed = await client.pushedAuthorizationRequest({
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
  const tokens = await client.refresh(session.refreshToken);
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
    grant_type: 'urn:ietf:params:oauth:grant-type:token-exchange',
    subject_token: personAccessToken,
    subject_token_type: 'urn:ietf:params:oauth:token-type:access_token',
    scope: 'audit-journey.read',
  });
  return tokens.access_token!;
}