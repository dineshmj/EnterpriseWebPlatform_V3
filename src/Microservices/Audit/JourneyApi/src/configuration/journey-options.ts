/**
 * Configuration of the Audit Journey API - environment variables only (runnow.bat sets the
 * development values). A missing value stops the start-up: no secret has a default.
 */
export interface JourneyOptions {
  port: number;
  publicOrigin: string;

  /** The IDP: issuer of the tokens this API accepts, and the token-exchange endpoint. */
  authority: string;
  /** This API's own resource name: the audience of the tokens it accepts. */
  audience: string;
  /** The only client allowed to call: the Audit web BFF (the token's "act" must name it). */
  callerClientId: string;

  /** This API's identity at the IDP. Its only grant is token exchange. */
  clientId: string;
  clientSecret: string;

  auditApiBaseUrl: string;
  paymentsApiBaseUrl: string;

  tlsPfxPath: string;
  tlsPfxPassword: string;
}

export const JOURNEY_OPTIONS = 'AUDIT_JOURNEY_OPTIONS';

function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim().length === 0) {
    throw new Error(`Configuration ${name} is not set; the Audit Journey API refuses to start without it.`);
  }
  return value.trim();
}

export function loadOptions(): JourneyOptions {
  return {
    port: Number(process.env.AUDIT_JOURNEY_PORT ?? '46379'),
    publicOrigin: required('AUDIT_JOURNEY_PUBLIC_ORIGIN'),
    authority: required('AUDIT_JOURNEY_IDP_AUTHORITY').replace(/\/$/, ''),
    audience: process.env.AUDIT_JOURNEY_AUDIENCE ?? 'audit-journey-api',
    callerClientId: process.env.AUDIT_JOURNEY_CALLER_CLIENT_ID ?? 'Audit.Microservice.Web.ClientID',
    clientId: process.env.AUDIT_JOURNEY_CLIENT_ID ?? 'Audit.JourneyApi.ClientID',
    clientSecret: required('AUDIT_JOURNEY_CLIENT_SECRET'),
    auditApiBaseUrl: required('AUDIT_API_BASE_URL').replace(/\/$/, ''),
    paymentsApiBaseUrl: required('PAYMENTS_API_BASE_URL').replace(/\/$/, ''),
    tlsPfxPath: required('AUDIT_JOURNEY_TLS_PFX_PATH'),
    tlsPfxPassword: required('AUDIT_JOURNEY_TLS_PFX_PASSWORD'),
  };
}