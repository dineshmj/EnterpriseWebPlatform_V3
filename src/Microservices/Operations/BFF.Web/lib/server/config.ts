// ┌─ :: SECURITY :: Fail-closed configuration and __Host- cookies ─────────────────────────────────
// │ Why : A web tier running without its client key or session key would be silently insecure.
// │ How : Every required setting is read at start-up and a missing one stops it; the session and
// │       sign-in cookies are __Host- (Secure, path /, no domain).
// │ Ref : Features 1.6.5, 2.3.3 | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
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
  get publicOrigin() { return required('OPERATIONS_WEB_PUBLIC_ORIGIN').replace(/\/$/, ''); },
  get shellOrigin() { return required('OPERATIONS_WEB_SHELL_ORIGIN').replace(/\/$/, ''); },
  get authority() { return required('OPERATIONS_WEB_IDP_AUTHORITY').replace(/\/$/, ''); },
  get clientId() { return required('OPERATIONS_WEB_CLIENT_ID'); },
  /** This BFF's private_key_jwt key (encrypted PKCS#8 PEM) and the file holding its password. */
  get clientKeyPath() { return required('OPERATIONS_WEB_CLIENT_KEY_PATH'); },
  get clientKeyPasswordPath() { return required('OPERATIONS_WEB_CLIENT_KEY_PASSWORD_PATH'); },
  /** 32 random bytes, base64: encrypts the tokens stored in the session table. */
  get sessionKey() { return Buffer.from(required('OPERATIONS_WEB_SESSION_KEY'), 'base64'); },
  get databaseUrl() { return required('OPERATIONS_WEB_DATABASE_URL'); },
  get journeyApiBaseUrl() { return required('OPERATIONS_JOURNEY_API_BASE_URL').replace(/\/$/, ''); },
  get callbackUrl() { return `${this.publicOrigin}/api/auth/callback`; },
};

/** Sliding session lifetime, the same as the other BFFs. */
export const SESSION_IDLE_MINUTES = 30;
export const SESSION_COOKIE = '__Host-Operations-Web-Session';
export const AUTH_STATE_COOKIE = '__Host-Operations-Web-Auth';