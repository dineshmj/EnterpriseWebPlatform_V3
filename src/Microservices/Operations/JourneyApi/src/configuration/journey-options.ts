import { loadPlatformMap, type PlatformMap } from '../platform/components';
import { listenerTls, type ListenerTls } from './listener-tls';

/**
 * Configuration of the Operations Journey API - environment variables only (runnow.bat sets the
 * development values). Fail closed: a missing or misspelt value stops the start-up, and outside
 * Development the security settings must be explicitly safe (see assertSafeOutsideDevelopment).
 */
export type DeploymentEnvironment = 'Development' | 'Test' | 'UAT' | 'Production';

export interface JourneyOptions {
  /** Where this instance runs; no default - an unset environment is never taken for Development. */
  environment: DeploymentEnvironment;
  port: number;
  publicOrigin: string;

  /** The IDP: issuer of the tokens this API accepts. */
  authority: string;
  /** This API's own resource name: the audience of the tokens it accepts. */
  audience: string;
  /** The only client allowed to call: the Operations web BFF (the token's "act" must name it). */
  callerClientId: string;

  /**
   * The simulators' behaviour switch. ON only in Development and Test: elsewhere the API refuses to
   * start with it on; when off, the switch is neither shown nor accepted.
   */
  simulatorControlsEnabled: boolean;
  /** A changed behaviour goes back to Healthy by itself after this many minutes. */
  simulatorAutoRevertMinutes: number;

  /**
   * The fault proxy's routes (cut / slow / hang an API for its server-side callers). ON only in
   * Development, Test and UAT: elsewhere the API refuses to start with it on (and production has
   * no proxy at all - its callers use direct addresses).
   */
  faultControlsEnabled: boolean;
  /** The fault proxy's admin API: plain http on this machine only (it has no authentication). */
  faultProxyAdminUrl: string;
  /** A cut, slow or hanging route opens again by itself after this many minutes. */
  faultAutoRevertMinutes: number;

  /** Step-up for a simulator change: a recent MFA sign-in (keep equal to the IDP's Mfa:Enabled); required outside Development. */
  mfaEnabled: boolean;
  mfaMaxAgeMinutes: number;

  /** This API's own client at the IDP: token exchange only, never client credentials. */
  clientId: string;
  clientSecret: string;
  /** The Domain APIs whose parked Outbox messages operations may see and resend. */
  accountsApiBaseUrl: string;
  complianceApiBaseUrl: string;
  partnerProductsApiBaseUrl: string;

  /** What Platform Health probes, and where the simulators are: deployment data (components.ts). */
  platform: PlatformMap;

  /** The listener's certificate; undefined = plain HTTP behind the platform's TLS (listener-tls.ts). */
  tls: ListenerTls | undefined;
}

export const JOURNEY_OPTIONS = 'OPERATIONS_JOURNEY_OPTIONS';

// ┌─ :: SECURITY :: Fail-closed configuration ─────────────────────────────────────────────────────
// │ Why : A Journey API started with a missing audience or caller would accept the wrong tokens.
// │ How : Every security setting is required (no defaults); switches must be exactly true or false.
// │ Ref : Features 1.6.5 | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim().length === 0) {
    throw new Error(`Configuration ${name} is not set; the Operations Journey API refuses to start without it.`);
  }
  return value.trim();
}

function oneOf<T extends string>(name: string, allowed: readonly T[]): T {
  const value = required(name);
  if (!(allowed as readonly string[]).includes(value)) {
    throw new Error(`Configuration ${name} must be one of ${allowed.join(', ')} (is "${value}"); the Operations Journey API refuses to start.`);
  }
  return value as T;
}

/** "true" or "false" exactly: a misspelt switch stops the start-up instead of silently meaning "off". */
function flag(name: string): boolean {
  return oneOf(name, ['true', 'false'] as const) === 'true';
}

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The fault proxy's admin API is unauthenticated: only a loopback address is accepted,
// │ so a setting can never point this API at a proxy on another machine.
// └──────────────────────────────────────────────────────────────────────────────────────
function loopbackUrl(name: string): string {
  const value = required(name).replace(/\/$/, '');
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`Configuration ${name} is not a URL; the Operations Journey API refuses to start.`);
  }
  if (url.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || url.pathname !== '/') {
    throw new Error(`Configuration ${name} must be http://127.0.0.1:<port> (this machine only); the Operations Journey API refuses to start.`);
  }
  return value;
}

function minutes(name: string): number {
  const value = Number(required(name));
  if (!Number.isInteger(value) || value < 1 || value > 60) {
    throw new Error(`Configuration ${name} must be a whole number of minutes from 1 to 60; the Operations Journey API refuses to start.`);
  }
  return value;
}

/**
 * Outside Development the privileged actions keep their protections whatever the deployment
 * forgot: MFA step-up must be on (keep the IDP's Mfa:Enabled on too), the simulators' switch
 * exists only in Development and Test, and the fault controls only in Development, Test and UAT.
 */
export function assertSafeOutsideDevelopment(options: JourneyOptions): void {
  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ Outside Development, MFA must be on and the simulator switch off (Test may keep it);
  // │ the fault controls exist only in Development, Test and UAT - otherwise refuse to start.
  // └──────────────────────────────────────────────────────────────────────────────────────
  if (options.faultControlsEnabled && !['Development', 'Test', 'UAT'].includes(options.environment)) {
    throw new Error(`OPERATIONS_FAULT_CONTROLS_ENABLED must be false in ${options.environment}; the Operations Journey API refuses to start.`);
  }
  if (options.environment === 'Development') return;
  if (!options.mfaEnabled) {
    throw new Error(`OPERATIONS_MFA_ENABLED must be true in ${options.environment}; the Operations Journey API refuses to start.`);
  }
  if (options.simulatorControlsEnabled && options.environment !== 'Test') {
    throw new Error(`OPERATIONS_SIMULATOR_CONTROLS_ENABLED must be false in ${options.environment}; the Operations Journey API refuses to start.`);
  }
}

export function loadOptions(): JourneyOptions {
  const options: JourneyOptions = {
    environment: oneOf('OPERATIONS_JOURNEY_ENVIRONMENT', ['Development', 'Test', 'UAT', 'Production'] as const),
    port: Number(process.env.OPERATIONS_JOURNEY_PORT ?? '46382'),
    publicOrigin: required('OPERATIONS_JOURNEY_PUBLIC_ORIGIN'),
    authority: required('OPERATIONS_JOURNEY_IDP_AUTHORITY').replace(/\/$/, ''),
    audience: required('OPERATIONS_JOURNEY_AUDIENCE'),
    callerClientId: required('OPERATIONS_JOURNEY_CALLER_CLIENT_ID'),
    simulatorControlsEnabled: flag('OPERATIONS_SIMULATOR_CONTROLS_ENABLED'),
    simulatorAutoRevertMinutes: minutes('OPERATIONS_SIMULATOR_AUTO_REVERT_MINUTES'),
    faultControlsEnabled: flag('OPERATIONS_FAULT_CONTROLS_ENABLED'),
    // Read only when the controls are on: an environment without the proxy needs no address.
    faultProxyAdminUrl: '',
    faultAutoRevertMinutes: 15,
    mfaEnabled: flag('OPERATIONS_MFA_ENABLED'),
    mfaMaxAgeMinutes: minutes('OPERATIONS_MFA_MAX_AGE_MINUTES'),
    clientId: required('OPERATIONS_JOURNEY_CLIENT_ID'),
    clientSecret: required('OPERATIONS_JOURNEY_CLIENT_SECRET'),
    accountsApiBaseUrl: required('OPERATIONS_JOURNEY_ACCOUNTS_API').replace(/\/$/, ''),
    complianceApiBaseUrl: required('OPERATIONS_JOURNEY_COMPLIANCE_API').replace(/\/$/, ''),
    partnerProductsApiBaseUrl: required('OPERATIONS_JOURNEY_PARTNER_PRODUCTS_API').replace(/\/$/, ''),
    // The simulators' addresses are required only while their switch is on.
    platform: loadPlatformMap('OPERATIONS_JOURNEY_PLATFORM_FILE', required('OPERATIONS_JOURNEY_PLATFORM_FILE'),
      flag('OPERATIONS_SIMULATOR_CONTROLS_ENABLED')),
    tls: listenerTls('OPERATIONS_JOURNEY', 'Operations Journey API'),
  };
  if (options.faultControlsEnabled) {
    options.faultProxyAdminUrl = loopbackUrl('OPERATIONS_FAULT_PROXY_ADMIN_URL');
    options.faultAutoRevertMinutes = minutes('OPERATIONS_FAULT_AUTO_REVERT_MINUTES');
  }
  assertSafeOutsideDevelopment(options);
  return options;
}