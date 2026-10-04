import 'express-session';

export interface OidcSession {
  state: string;
  nonce: string;
  codeVerifier: string;
  returnUrl: string;
}

export interface KycUserSession {
  subject: string;
  name?: string;
  roles: string[];
  /** The user's branch code (ABAC). Sent to Documents Management as the acting branch. */
  branch?: string;
}

declare module 'express-session' {
  interface SessionData {
    oidc?: OidcSession;
    user?: KycUserSession;
    accessToken?: string;
    refreshToken?: string;
    idToken?: string;
    accessTokenExpiresAt?: number;
    csrfToken?: string;
  }
}