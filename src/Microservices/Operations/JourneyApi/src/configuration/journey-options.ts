/**
 * Configuration of the Operations Journey API - environment variables only (runnow.bat sets the
 * development values). A missing value stops the start-up.
 */
export interface JourneyOptions {
  port: number;
  publicOrigin: string;

  /** The IDP: issuer of the tokens this API accepts. */
  authority: string;
  /** This API's own resource name: the audience of the tokens it accepts. */
  audience: string;
  /** The only client allowed to call: the Operations web BFF (the token's "act" must name it). */
  callerClientId: string;

  /**
   * The simulators' behaviour switch. ON only in Development and test environments: elsewhere
   * the switch is neither shown nor accepted (fail closed - unset means off).
   */
  simulatorControlsEnabled: boolean;
  /** A changed behaviour goes back to Healthy by itself after this many minutes. */
  simulatorAutoRevertMinutes: number;

  /** Step-up for a simulator change while MFA is on: a recent MFA sign-in (keep equal to the IDP's Mfa:Enabled). */
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

function minutes(name: string, fallback: number): number {
  const value = Number(process.env[name] ?? fallback);
  return Number.isInteger(value) && value >= 1 && value <= 60 ? value : fallback;
}

export function loadOptions(): JourneyOptions {
  return {
    port: Number(process.env.OPERATIONS_JOURNEY_PORT ?? '46382'),
    publicOrigin: required('OPERATIONS_JOURNEY_PUBLIC_ORIGIN'),
    authority: required('OPERATIONS_JOURNEY_IDP_AUTHORITY').replace(/\/$/, ''),
    audience: process.env.OPERATIONS_JOURNEY_AUDIENCE ?? 'operations-journey-api',
    callerClientId: process.env.OPERATIONS_JOURNEY_CALLER_CLIENT_ID ?? 'Operations.Microservice.Web.ClientID',
    simulatorControlsEnabled: process.env.OPERATIONS_SIMULATOR_CONTROLS_ENABLED === 'true',
    simulatorAutoRevertMinutes: minutes('OPERATIONS_SIMULATOR_AUTO_REVERT_MINUTES', 15),
    mfaEnabled: process.env.OPERATIONS_MFA_ENABLED === 'true',
    mfaMaxAgeMinutes: minutes('OPERATIONS_MFA_MAX_AGE_MINUTES', 15),
    tlsPfxPath: required('OPERATIONS_JOURNEY_TLS_PFX_PATH'),
    tlsPfxPassword: required('OPERATIONS_JOURNEY_TLS_PFX_PASSWORD'),
  };
}