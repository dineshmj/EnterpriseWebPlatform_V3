/**
 * What a route between two parts of the platform does now: passes everything on (Open), adds a
 * delay to every answer (Slow), accepts the connection but never answers (Hanging), or refuses
 * the connection (Cut - the API looks down to every caller).
 */
export const routeStates = ['Open', 'Slow', 'Hanging', 'Cut'] as const;
export type RouteState = (typeof routeStates)[number];

// ┌─ :: ARCHITECTURE :: The fault controller is a port, Toxiproxy one adapter ─────────────────────
// │ Why : On a laptop one Toxiproxy process carries the server-to-server routes; a test cluster
// │       would inject the same faults in its service mesh (Istio fault injection, Linkerd), with
// │       no proxy process at all. The page and the rules around it must not change with that.
// │ How : The Journey API depends on this interface only. ToxiproxyFaultController implements it
// │       for Development; a mesh implementation would read and change the mesh's routing rules.
// │ Ref : Hexagonal architecture (ports and adapters) | Operations Journey API README
// └────────────────────────────────────────────────────────────────────────────────────────────────
export interface FaultController {
  /** Every route the proxy knows and what it does now (a route missing here is not carried). */
  states(): Promise<Map<string, RouteState>>;
  /** Makes one route do this. */
  set(route: string, state: RouteState): Promise<void>;
  /** Opens every route again (start-up, so a restart never leaves an API cut). */
  reopenAll(): Promise<void>;
}

export const FAULT_CONTROLLER = 'OPERATIONS_FAULT_CONTROLLER';