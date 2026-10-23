import { existsSync } from 'node:fs';
import { listenerTls, type ListenerTls } from './listener-tls';

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

  /**
   * Kafka (TLS, then SASL/SCRAM-SHA-512 as the worker's own user): the listener for
   * payments.payment.events. Read rights on that topic and the group partner-products.worker only
   * (Setup-KafkaSecurity.ps1). The CA file verifies the broker's certificate; undefined = the
   * operating system's trusted roots.
   */
  kafkaBootstrapServers: string;
  kafkaUsername: string;
  kafkaPassword: string;
  kafkaSslCaLocation: string | undefined;

  /** The Partner Bank's settlement account (the payee of every purchase): only its payments are looked up. */
  settlementBsb: string;
  settlementAccountNumber: string;

  /** The health probe (Operations -> Platform Health): https with the development certificate, or plain HTTP behind the platform's TLS (listener-tls.ts). */
  healthPort: number;
  tls: ListenerTls | undefined;
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

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ If a Kafka CA file is configured it must exist: a typo must stop the start-up, not
// │ fall back quietly to a different set of trusted roots.
// └──────────────────────────────────────────────────────────────────────────────────────
function optionalFile(name: string): string | undefined {
  const value = process.env[name]?.trim();
  if (!value) return undefined;
  if (!existsSync(value)) {
    throw new Error(`Configuration ${name} names ${value}, which does not exist; the Partner Products Worker refuses to start.`);
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
    kafkaBootstrapServers: required('KAFKA_BOOTSTRAP_SERVERS'),
    kafkaUsername: required('PARTNER_PRODUCTS_WORKER_KAFKA_USERNAME'),
    kafkaPassword: required('PARTNER_PRODUCTS_WORKER_KAFKA_PASSWORD'),
    kafkaSslCaLocation: optionalFile('KAFKA_SSL_CA_LOCATION'),
    settlementBsb: required('PARTNER_PRODUCTS_SETTLEMENT_BSB'),
    settlementAccountNumber: required('PARTNER_PRODUCTS_SETTLEMENT_ACCOUNT_NUMBER'),
    healthPort: Number(required('PARTNER_PRODUCTS_WORKER_HEALTH_PORT')),
    tls: listenerTls('PARTNER_PRODUCTS_WORKER', 'Partner Products Worker'),
  };
}