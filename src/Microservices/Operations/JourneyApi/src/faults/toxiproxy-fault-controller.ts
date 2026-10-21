import { Inject, Injectable, ServiceUnavailableException } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { FaultController, RouteState } from './fault-controller';

/** The toxics this API adds; the names mark them as ours, so nothing else on a route is touched. */
const SLOW = 'ewp-slow';
const HANGING = 'ewp-hanging';
/** How much a Slow route adds to every answer: more than a person waits happily, less than the callers' timeouts. */
const SLOW_MS = 5000;

interface ToxiproxyProxy {
  name: string;
  enabled: boolean;
  toxics?: { name: string }[];
}

/**
 * The fault controller for Development: Toxiproxy's admin API on this machine
 * (ps\toxiproxy\toxiproxy.json holds the routes). Cut = the proxy is disabled (connections are
 * refused); Slow = a latency toxic on the answers; Hanging = a timeout toxic that holds the
 * connection and never answers.
 */
@Injectable()
export class ToxiproxyFaultController implements FaultController {
  constructor(@Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions) {}

  async states(): Promise<Map<string, RouteState>> {
    const proxies = await this.call<Record<string, ToxiproxyProxy>>('GET', '/proxies');
    const states = new Map<string, RouteState>();
    for (const proxy of Object.values(proxies)) {
      const toxics = new Set((proxy.toxics ?? []).map((t) => t.name));
      states.set(proxy.name, !proxy.enabled ? 'Cut' : toxics.has(HANGING) ? 'Hanging' : toxics.has(SLOW) ? 'Slow' : 'Open');
    }
    return states;
  }

  async set(route: string, state: RouteState): Promise<void> {
    const proxy = await this.call<ToxiproxyProxy>('GET', `/proxies/${encodeURIComponent(route)}`);
    const toxics = new Set((proxy.toxics ?? []).map((t) => t.name));

    // Only our own toxics are removed or added; the order keeps a route from flapping open.
    for (const name of [SLOW, HANGING]) {
      const wanted = (name === SLOW && state === 'Slow') || (name === HANGING && state === 'Hanging');
      if (toxics.has(name) && !wanted) await this.call('DELETE', `/proxies/${encodeURIComponent(route)}/toxics/${name}`);
      if (!toxics.has(name) && wanted) {
        await this.call('POST', `/proxies/${encodeURIComponent(route)}/toxics`, name === SLOW
          ? { name, type: 'latency', stream: 'downstream', toxicity: 1, attributes: { latency: SLOW_MS, jitter: 0 } }
          : { name, type: 'timeout', stream: 'downstream', toxicity: 1, attributes: { timeout: 0 } });
      }
    }
    const enabled = state !== 'Cut';
    if (proxy.enabled !== enabled) await this.call('POST', `/proxies/${encodeURIComponent(route)}`, { enabled });
  }

  async reopenAll(): Promise<void> {
    // Toxiproxy's own reset: every route enabled, every toxic removed.
    await this.call('POST', '/reset');
  }

  private async call<T = unknown>(method: 'GET' | 'POST' | 'DELETE', path: string, body?: unknown): Promise<T> {
    let response: Response;
    try {
      response = await fetch(`${this.options.faultProxyAdminUrl}${path}`, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
        signal: AbortSignal.timeout(5000),
        redirect: 'error',
      });
    } catch {
      throw new ServiceUnavailableException('The fault proxy does not answer. Is it running (.\\ps\\toxiproxy\\Start-Toxiproxy.ps1)?');
    }
    if (!response.ok) {
      throw new ServiceUnavailableException(`The fault proxy refused the change (HTTP ${response.status}).`);
    }
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }
}