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
  /** The user's branch code (ABAC). Checked to stop early; Documents Management reads it from its own token. */
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
    /** The IDP session ID (sid): back-channel logout ends every session of it. */
    idpSid?: string;
    /** Documents Management token for this officer (token exchange), kept until shortly before it expires. */
    documentsToken?: { accessToken: string; expiresAt: number };
  }
}