/** The Operations Journey API's answers, as the screen uses them. */

export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unreachable';

export interface ComponentHealth {
  name: string;
  group: string;
  status: HealthStatus;
  responseMs: number | null;
}

export interface PlatformHealth {
  checkedAt: string;
  components: ComponentHealth[];
  /** The simulators' switch exists here (Development and test only). */
  simulatorControls: boolean;
}

export const BEHAVIOURS = ['Healthy', 'Slow', 'Failing', 'Down', 'Refusing', 'LosingAnswers'] as const;
export type Behaviour = (typeof BEHAVIOURS)[number];

export interface SimulatorState {
  key: string;
  name: string;
  affects: string;
  behaviour: Behaviour | 'Unknown';
  /** The behaviours this simulator knows (LosingAnswers: the Payment Network only). */
  behaviours: Behaviour[];
  revertsAt: string | null;
  changedBy: string | null;
}

/** A message the Outbox relay of Accounts, Compliance or Partner Products parked - no payload. */
export interface ParkedMessage {
  messageId: string;
  eventType: string;
  aggregateType: string;
  aggregateId: string;
  occurredAt: string;
  attemptCount: number;
  lastAttemptAt: string | null;
  lastError: string | null;
  heldBack: number;
  redriveCount: number;
  lastRedrivenAt: string | null;
  lastRedrivenBy: string | null;
  lastRedriveReason: string | null;
}

export interface ContextParkedMessages {
  context: 'accounts' | 'compliance' | 'partner-products';
  available: boolean;
  messages: ParkedMessage[];
}

export type ActionResult<T> =
  | { ok: true; data: T }
  | { ok: false; error: string; signedOut?: boolean; stepUp?: boolean };