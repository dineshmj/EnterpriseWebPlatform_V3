import { listenerTls, type ListenerTls } from './listener-tls';
import { trustedProxies } from '../security/trusted-proxies';

// ┌─ :: SECURITY :: Fail-closed configuration ─────────────────────────────────────────────────────
// │ Why : A BFF running without its secret, session key or audience would be silently insecure.
// │ How : Every required setting is read once at start-up; a missing one stops the BFF. Addresses
// │       have no development defaults either: a container must not quietly call localhost.
// │ Ref : Features 1.6.5 | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
export interface KycBffOptions {
  port: number;
  authority: string;
  clientId: string;
  clientSecret: string;
  callbackUrl: string;
  postLogoutRedirectUri: string;
  apiBaseUrl: string;
  sessionSecret: string;
  /** AES-256-GCM key (32 bytes) that encrypts the sessions at rest; never in the database. */
  sessionKey: Buffer;
  /** EwpBffStateDb as ewp_kyc_bff (schema kyc_bff). */
  databaseUrl: string;
  /** The listener's certificate; undefined = plain HTTP behind the platform's TLS (listener-tls.ts). */
  tls: ListenerTls | undefined;
  /** Express "trust proxy": the ingress's addresses, one hop; undefined = forwarded headers ignored. */
  trustProxy: ((address: string, hop: number) => boolean) | undefined;
  staticRoot: string;
  shellOrigin: string;
  documentsManagementApiBaseUrl: string;
}

/**
 * Secrets have no defaults: the BFF refuses to start without them (fail closed)
 * rather than falling back to values that are published in source control.
 */
function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim() === '') {
    throw new Error(`Required configuration '${name}' is not set.`);
  }
  return value;
}

export function loadOptions(): KycBffOptions {
  return {
    port: Number(process.env.KYC_BFF_PORT ?? '33800'),
    authority: required('KYC_IDP_AUTHORITY'),
    clientId: required('KYC_BFF_CLIENT_ID'),
    clientSecret: required('KYC_BFF_CLIENT_SECRET'),
    callbackUrl: required('KYC_BFF_CALLBACK_URL'),
    postLogoutRedirectUri: required('KYC_BFF_POST_LOGOUT_REDIRECT_URI'),
    apiBaseUrl: required('KYC_API_BASE_URL'),
    sessionSecret: required('KYC_BFF_SESSION_SECRET'),
    sessionKey: Buffer.from(required('KYC_BFF_SESSION_KEY'), 'base64'),
    databaseUrl: required('KYC_BFF_DATABASE_URL'),
    tls: listenerTls('KYC_BFF', 'KYC BFF'),
    trustProxy: trustedProxies(process.env.KYC_BFF_TRUSTED_PROXIES),
    staticRoot: process.env.KYC_BFF_STATIC_ROOT ?? 'client-app/out',
    shellOrigin: required('KYC_BFF_SHELL_ORIGIN'),
    documentsManagementApiBaseUrl: required('KYC_DOCUMENTS_MANAGEMENT_API_BASE_URL'),
  };
}