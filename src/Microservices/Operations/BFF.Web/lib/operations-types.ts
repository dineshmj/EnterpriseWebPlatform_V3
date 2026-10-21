/** The Operations Journey API's answers, as the screen uses them. */

export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy' | 'Unreachable';

export interface ComponentHealth {
  name: string;
  group: string;
  status: HealthStatus;
  responseMs: number | null;
  /** Its callers' route at the fault proxy, when it is not Open (the probe itself goes direct). */
  atProxy: RouteState | null;
}

export interface PlatformHealth {
  checkedAt: string;
  components: ComponentHealth[];
  /** The simulators' switch exists here (Development and test only). */
  simulatorControls: boolean;
  /** The fault proxy's routes exist here (Development, test and UAT only). */
  faultControls: boolean;
}

export const ROUTE_STATES = ['Open', 'Slow', 'Hanging', 'Cut'] as const;
export type RouteState = (typeof ROUTE_STATES)[number];

/** One API's route at the fault proxy: every server-side caller of that API goes through it. */
export interface FaultRoute {
  key: string;
  name: string;
  usedBy: string;
  state: RouteState | 'Unknown';
  states: RouteState[];
  revertsAt: string | null;
  changedBy: string | null;
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
  | { ok: false; error: string; signedOut?: boolean; stepUp?: boolean; notAvailable?: boolean };