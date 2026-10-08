/**
 * The light BFF's configuration - environment variables only (runnow.bat sets the development
 * values). A missing value stops the request that needs it: no secret has a default.
 */
function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim().length === 0) {
    throw new Error(`Configuration ${name} is not set (see runnow.bat).`);
  }
  return value.trim();
}

export const config = {
  get publicOrigin() { return required('AUDIT_WEB_PUBLIC_ORIGIN').replace(/\/$/, ''); },
  get shellOrigin() { return required('AUDIT_WEB_SHELL_ORIGIN').replace(/\/$/, ''); },
  get authority() { return required('AUDIT_WEB_IDP_AUTHORITY').replace(/\/$/, ''); },
  get clientId() { return required('AUDIT_WEB_CLIENT_ID'); },
  get clientSecret() { return required('AUDIT_WEB_CLIENT_SECRET'); },
  /** 32 random bytes, base64: encrypts the tokens stored in the session table. */
  get sessionKey() { return Buffer.from(required('AUDIT_WEB_SESSION_KEY'), 'base64'); },
  get databaseUrl() { return required('AUDIT_WEB_DATABASE_URL'); },
  get journeyApiBaseUrl() { return required('AUDIT_JOURNEY_API_BASE_URL').replace(/\/$/, ''); },
  get callbackUrl() { return `${this.publicOrigin}/api/auth/callback`; },
};

/** Sliding session lifetime, the same as the other BFFs. */
export const SESSION_IDLE_MINUTES = 30;
export const SESSION_COOKIE = '__Host-Audit-Web-Session';
export const AUTH_STATE_COOKIE = '__Host-Audit-Web-Auth';