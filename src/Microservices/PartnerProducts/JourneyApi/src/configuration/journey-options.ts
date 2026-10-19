/**
 * Configuration of the Partner Products Journey API - environment variables only (runnow.bat sets
 * the development values). Fail closed: a missing or misspelt value stops the start-up; no secret
 * and no security setting (audience, permitted caller, own client) has a default.
 */
export type DeploymentEnvironment = 'Development' | 'Test' | 'Production';

export interface JourneyOptions {
  /** Where this instance runs; no default - an unset environment is never taken for Development. */
  environment: DeploymentEnvironment;
  port: number;
  publicOrigin: string;

  /** The IDP: issuer of the tokens this API accepts, and the token-exchange endpoint. */
  authority: string;
  /** This API's own resource name: the audience of the tokens it accepts. */
  audience: string;
  /** The only client allowed to call: the Partner Products web BFF (the token's "act" must name it). */
  callerClientId: string;

  /** This API's own client at the IDP: token exchange only, never client credentials. */
  clientId: string;
  clientSecret: string;

  /** The Domain APIs this API composes, always FOR the signed-in staff member. */
  partnerProductsApiBaseUrl: string;
  customerOnboardingApiBaseUrl: string;
  accountsApiBaseUrl: string;
  documentsApiBaseUrl: string;
  /** EWP Payments: the purchase's payment is started here, as the staff member (payment.initiate). */
  paymentsApiBaseUrl: string;

  /** Temporal: the purchase workflow's server (host:port), namespace and task queue (the Partner Products Worker polls it). */
  temporalAddress: string;
  temporalNamespace: string;
  temporalTaskQueue: string;

  tlsPfxPath: string;
  tlsPfxPassword: string;
}

export const JOURNEY_OPTIONS = 'PARTNER_PRODUCTS_JOURNEY_OPTIONS';

// ┌─ :: SECURITY :: Fail-closed configuration ─────────────────────────────────────────────────────
// │ Why : A Journey API started with a missing audience or caller would accept the wrong tokens.
// │ How : Every security setting is required (no defaults); the environment must be named exactly.
// │ Ref : Features 1.6.5 | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim().length === 0) {
    throw new Error(`Configuration ${name} is not set; the Partner Products Journey API refuses to start without it.`);
  }
  return value.trim();
}

function oneOf<T extends string>(name: string, allowed: readonly T[]): T {
  const value = required(name);
  if (!(allowed as readonly string[]).includes(value)) {
    throw new Error(`Configuration ${name} must be one of ${allowed.join(', ')} (is "${value}"); the Partner Products Journey API refuses to start.`);
  }
  return value as T;
}

/** An https base address, without a trailing slash: a plain-http downstream would carry bearer tokens in the clear. */
function httpsUrl(name: string): string {
  const value = required(name).replace(/\/$/, '');
  if (!value.startsWith('https://')) {
    throw new Error(`Configuration ${name} must be an https address; the Partner Products Journey API refuses to start.`);
  }
  return value;
}

export function loadOptions(): JourneyOptions {
  return {
    environment: oneOf('PARTNER_PRODUCTS_JOURNEY_ENVIRONMENT', ['Development', 'Test', 'Production'] as const),
    port: Number(process.env.PARTNER_PRODUCTS_JOURNEY_PORT ?? '46385'),
    publicOrigin: required('PARTNER_PRODUCTS_JOURNEY_PUBLIC_ORIGIN'),
    authority: httpsUrl('PARTNER_PRODUCTS_JOURNEY_IDP_AUTHORITY'),
    audience: required('PARTNER_PRODUCTS_JOURNEY_AUDIENCE'),
    callerClientId: required('PARTNER_PRODUCTS_JOURNEY_CALLER_CLIENT_ID'),
    clientId: required('PARTNER_PRODUCTS_JOURNEY_CLIENT_ID'),
    clientSecret: required('PARTNER_PRODUCTS_JOURNEY_CLIENT_SECRET'),
    partnerProductsApiBaseUrl: httpsUrl('PARTNER_PRODUCTS_API_BASE_URL'),
    customerOnboardingApiBaseUrl: httpsUrl('CUSTOMER_ONBOARDING_API_BASE_URL'),
    accountsApiBaseUrl: httpsUrl('ACCOUNTS_API_BASE_URL'),
    documentsApiBaseUrl: httpsUrl('DOCUMENTS_MANAGEMENT_API_BASE_URL'),
    paymentsApiBaseUrl: httpsUrl('PAYMENTS_API_BASE_URL'),
    temporalAddress: required('TEMPORAL_ADDRESS'),
    temporalNamespace: required('TEMPORAL_NAMESPACE'),
    temporalTaskQueue: required('PARTNER_PRODUCTS_TEMPORAL_TASK_QUEUE'),
    tlsPfxPath: required('PARTNER_PRODUCTS_JOURNEY_TLS_PFX_PATH'),
    tlsPfxPassword: required('PARTNER_PRODUCTS_JOURNEY_TLS_PFX_PASSWORD'),
  };
}