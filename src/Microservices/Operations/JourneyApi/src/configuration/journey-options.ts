/**
 * Configuration of the Operations Journey API - environment variables only (runnow.bat sets the
 * development values). Fail closed: a missing or misspelt value stops the start-up, and outside
 * Development the security settings must be explicitly safe (see assertSafeOutsideDevelopment).
 */
export type DeploymentEnvironment = 'Development' | 'Test' | 'Production';

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

  /** Step-up for a simulator change: a recent MFA sign-in (keep equal to the IDP's Mfa:Enabled); required outside Development. */
  mfaEnabled: boolean;
  mfaMaxAgeMinutes: number;

  tlsPfxPath: string;
  tlsPfxPassword: string;
}

export const JOURNEY_OPTIONS = 'OPERATIONS_JOURNEY_OPTIONS';

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

function minutes(name: string): number {
  const value = Number(required(name));
  if (!Number.isInteger(value) || value < 1 || value > 60) {
    throw new Error(`Configuration ${name} must be a whole number of minutes from 1 to 60; the Operations Journey API refuses to start.`);
  }
  return value;
}

/**
 * Outside Development the privileged actions keep their protections whatever the deployment
 * forgot: MFA step-up must be on (keep the IDP's Mfa:Enabled on too), and the simulators' switch
 * exists only in Development and Test.
 */
export function assertSafeOutsideDevelopment(options: JourneyOptions): void {
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
    environment: oneOf('OPERATIONS_JOURNEY_ENVIRONMENT', ['Development', 'Test', 'Production'] as const),
    port: Number(process.env.OPERATIONS_JOURNEY_PORT ?? '46382'),
    publicOrigin: required('OPERATIONS_JOURNEY_PUBLIC_ORIGIN'),
    authority: required('OPERATIONS_JOURNEY_IDP_AUTHORITY').replace(/\/$/, ''),
    audience: required('OPERATIONS_JOURNEY_AUDIENCE'),
    callerClientId: required('OPERATIONS_JOURNEY_CALLER_CLIENT_ID'),
    simulatorControlsEnabled: flag('OPERATIONS_SIMULATOR_CONTROLS_ENABLED'),
    simulatorAutoRevertMinutes: minutes('OPERATIONS_SIMULATOR_AUTO_REVERT_MINUTES'),
    mfaEnabled: flag('OPERATIONS_MFA_ENABLED'),
    mfaMaxAgeMinutes: minutes('OPERATIONS_MFA_MAX_AGE_MINUTES'),
    tlsPfxPath: required('OPERATIONS_JOURNEY_TLS_PFX_PATH'),
    tlsPfxPassword: required('OPERATIONS_JOURNEY_TLS_PFX_PASSWORD'),
  };
  assertSafeOutsideDevelopment(options);
  return options;
}