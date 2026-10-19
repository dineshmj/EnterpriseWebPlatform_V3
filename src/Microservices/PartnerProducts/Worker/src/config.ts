/**
 * Configuration of the Partner Products Worker - environment variables only (runnow.bat sets the
 * development values). Fail closed: a missing value stops the start-up; no secret has a default.
 */
export interface WorkerOptions {
  /** Temporal: server (host:port), namespace and the task queue the Journey API starts the workflow on. */
  temporalAddress: string;
  temporalNamespace: string;
  taskQueue: string;

  /** The IDP, and this worker's own machine client (client credentials). */
  authority: string;
  clientId: string;
  clientSecret: string;

  /** The two APIs whose internal endpoints pin this worker's client ID. */
  partnerProductsApiBaseUrl: string;
  paymentsApiBaseUrl: string;

  /** The health probe (Operations -> Platform Health): https, the development certificate. */
  healthPort: number;
  tlsPfxPath: string;
  tlsPfxPassword: string;
}

function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim().length === 0) {
    throw new Error(`Configuration ${name} is not set; the Partner Products Worker refuses to start without it.`);
  }
  return value.trim();
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Every API and the IDP over https only: the worker sends its bearer tokens there.
// └──────────────────────────────────────────────────────────────────────────────────────
function httpsUrl(name: string): string {
  const value = required(name).replace(/\/$/, '');
  if (!value.startsWith('https://')) {
    throw new Error(`Configuration ${name} must be an https address; the Partner Products Worker refuses to start.`);
  }
  return value;
}

export function loadOptions(): WorkerOptions {
  return {
    temporalAddress: required('TEMPORAL_ADDRESS'),
    temporalNamespace: required('TEMPORAL_NAMESPACE'),
    taskQueue: required('PARTNER_PRODUCTS_TEMPORAL_TASK_QUEUE'),
    authority: httpsUrl('PARTNER_PRODUCTS_WORKER_IDP_AUTHORITY'),
    clientId: required('PARTNER_PRODUCTS_WORKER_CLIENT_ID'),
    clientSecret: required('PARTNER_PRODUCTS_WORKER_CLIENT_SECRET'),
    partnerProductsApiBaseUrl: httpsUrl('PARTNER_PRODUCTS_API_BASE_URL'),
    paymentsApiBaseUrl: httpsUrl('PAYMENTS_API_BASE_URL'),
    healthPort: Number(required('PARTNER_PRODUCTS_WORKER_HEALTH_PORT')),
    tlsPfxPath: required('PARTNER_PRODUCTS_WORKER_TLS_PFX_PATH'),
    tlsPfxPassword: required('PARTNER_PRODUCTS_WORKER_TLS_PFX_PASSWORD'),
  };
}