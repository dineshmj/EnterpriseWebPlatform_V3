import { readFileSync } from 'node:fs';

export const componentGroups = ['Front ends', 'Identity', 'Domain APIs', 'Journey APIs', 'Workers', 'Simulators'] as const;
export type ComponentGroup = (typeof componentGroups)[number];

/** A deployable part of EWP and its readiness probe. Probed server-side only: the screen never sees a URL. */
export interface PlatformComponent {
  name: string;
  group: ComponentGroup;
  readiness: string;
}

export const behaviours = ['Healthy', 'Slow', 'Failing', 'Down', 'Refusing', 'LosingAnswers'] as const;
export type Behaviour = (typeof behaviours)[number];

const common: readonly Behaviour[] = ['Healthy', 'Slow', 'Failing', 'Down', 'Refusing'];

/**
 * The simulators whose behaviour can be switched (PUT /admin/behaviour, loopback callers only),
 * each with the behaviours it knows. LosingAnswers (the payment goes out, every answer is lost)
 * is the Payment Network's only: the case its status enquiry - reconciliation - exists for.
 * Their addresses come from the platform map (below), like every other address.
 */
export const simulators = [
  { key: 'screening-provider', name: 'Screening Provider', affects: 'Compliance screening', behaviours: common },
  { key: 'core-banking', name: 'Core Banking', affects: 'Account opening', behaviours: common },
  { key: 'payment-network', name: 'Payment Network', affects: 'Sending payments', behaviours: [...common, 'LosingAnswers'] as readonly Behaviour[] },
] as const;

export type SimulatorKey = (typeof simulators)[number]['key'];

/** What this deployment consists of: every component's readiness probe, and each simulator's address. */
export interface PlatformMap {
  components: PlatformComponent[];
  simulatorBaseUrls: Record<SimulatorKey, string>;
}

// ┌─ :: ARCHITECTURE + SECURITY :: The platform map is deployment data ────────────────────────────
// │ Why : Which components exist, and where, differs per environment (laptop: *.dev.localhost and
// │       fixed ports; a cluster: service names). Compiled in, a deployed Platform Health would
// │       probe laptop addresses and report everything down.
// │ How : A JSON file named by OPERATIONS_JOURNEY_PLATFORM_FILE - development: the committed
// │       config/platform.development.json (runnow.bat); a cluster: a mounted ConfigMap. Read once
// │       at start-up and checked: known groups, unique names, absolute http(s) addresses without
// │       credentials, and every simulator's address while the simulator switch is on. A missing
// │       or malformed file stops the start-up - no silent empty dashboard.
// │       The file is an operator's, not a user's: the addresses are probed server-side only and
// │       never leave this API.
// │ Ref : doc/Containers.md (run-time contract) | Features 1.7.4 | OWASP A05
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function loadPlatformMap(variable: string, path: string, simulatorsRequired: boolean): PlatformMap {
  const fail = (problem: string): never => {
    throw new Error(`${variable} (${path}): ${problem}; the Operations Journey API refuses to start.`);
  };

  let raw: unknown;
  try {
    raw = JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    return fail(`cannot be read as JSON (${(error as Error).message})`);
  }
  const map = raw as { components?: unknown; simulators?: unknown };

  if (!Array.isArray(map.components) || map.components.length === 0) fail('"components" must be a non-empty list');
  const names = new Set<string>();
  const components = (map.components as unknown[]).map((entry, index) => {
    const c = entry as Partial<PlatformComponent>;
    if (typeof c.name !== 'string' || c.name.trim().length === 0) fail(`component ${index + 1} has no name`);
    if (names.has(c.name!)) fail(`component "${c.name}" is listed twice`);
    names.add(c.name!);
    if (!componentGroups.includes(c.group as ComponentGroup)) fail(`component "${c.name}" has an unknown group "${c.group}" (one of ${componentGroups.join(', ')})`);
    return { name: c.name!, group: c.group as ComponentGroup, readiness: address(c.readiness, `component "${c.name}"`, fail) };
  });

  const configured = (map.simulators ?? {}) as Record<string, unknown>;
  for (const key of Object.keys(configured)) {
    if (!simulators.some((s) => s.key === key)) fail(`unknown simulator "${key}" (one of ${simulators.map((s) => s.key).join(', ')})`);
  }
  const simulatorBaseUrls = {} as Record<SimulatorKey, string>;
  for (const simulator of simulators) {
    const value = configured[simulator.key];
    if (value === undefined) {
      if (simulatorsRequired) fail(`simulator "${simulator.key}" has no address, and the simulator switch is on`);
      continue;
    }
    simulatorBaseUrls[simulator.key] = address(value, `simulator "${simulator.key}"`, fail).replace(/\/$/, '');
  }

  return { components, simulatorBaseUrls };
}

/** An absolute http(s) address without credentials, query or fragment. */
function address(value: unknown, what: string, fail: (problem: string) => never): string {
  if (typeof value !== 'string') return fail(`${what} has no address`);
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return fail(`${what}: "${value}" is not an absolute address`);
  }
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash) {
    return fail(`${what}: "${value}" must be http(s), without credentials, query or fragment`);
  }
  return value;
}