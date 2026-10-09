import { ServiceUnavailableException } from '@nestjs/common';
import { circuitBreakerState, clientDuration } from '../observability/metrics';
import { traceHeaders } from '../observability/trace-context';

type State = 'closed' | 'open' | 'half-open';

/**
 * A circuit breaker per downstream service, with the platform's settings (the .NET BFFs'
 * standard resilience handler): when at least half of the calls in the last 30 seconds failed
 * (and there were at least 5), the circuit opens for 30 seconds. While open, calls are refused
 * at once - a struggling service gets room to recover, and the officer sees "temporarily
 * unavailable" instead of a hanging page. Then one trial call is let through (half-open): its
 * success closes the circuit, its failure opens it again.
 *
 * A failure is a network error, a timeout, or 408 / 429 / 5xx. A 4xx answer (401, 403, 404,
 * 409, 422) is the service working correctly and never trips the breaker.
 */
export class CircuitBreaker {
  private state: State = 'closed';
  private openedAt = 0;
  private trialInFlight = false;
  private outcomes: { at: number; failed: boolean }[] = [];

  constructor(
    readonly target: string,
    private readonly failureRatio = 0.5,
    private readonly minimumThroughput = 5,
    private readonly samplingMs = 30_000,
    private readonly breakMs = 30_000,
  ) {
    circuitBreakerState.set({ target }, 0);
  }

  /** Throws when the circuit is open (or a half-open trial is already running). */
  acquire(): void {
    if (this.state === 'open') {
      if (Date.now() - this.openedAt < this.breakMs) throw this.refused();
      this.setState('half-open');
    }
    if (this.state === 'half-open') {
      if (this.trialInFlight) throw this.refused();
      this.trialInFlight = true;
    }
  }

  record(failed: boolean): void {
    const now = Date.now();
    if (this.state === 'half-open') {
      this.trialInFlight = false;
      this.outcomes = [];
      if (failed) this.open(now); else this.setState('closed');
      return;
    }

    this.outcomes.push({ at: now, failed });
    this.outcomes = this.outcomes.filter(o => now - o.at <= this.samplingMs);
    const failures = this.outcomes.filter(o => o.failed).length;
    if (this.outcomes.length >= this.minimumThroughput && failures / this.outcomes.length >= this.failureRatio) {
      this.open(now);
    }
  }

  private open(now: number): void {
    this.openedAt = now;
    this.outcomes = [];
    this.setState('open');
  }

  private setState(state: State): void {
    this.state = state;
    circuitBreakerState.set({ target: this.target }, state === 'closed' ? 0 : state === 'half-open' ? 1 : 2);
  }

  private refused(): ServiceUnavailableException {
    return new ServiceUnavailableException(`The ${this.target} service is temporarily unavailable. Please try again shortly.`);
  }
}

const TRANSIENT = [408, 429, 500, 502, 503, 504];

export interface ResilientFetchOptions {
  timeoutMs: number;
  /** Total attempts; GETs only - a write is never retried (see KycApiService.postOnce). */
  attempts?: number;
}

/**
 * One outgoing call: through the target's circuit breaker, with a timeout per attempt, the
 * request's trace context (traceparent) and a duration metric. Only idempotent requests may
 * ask for more than one attempt.
 */
export async function resilientFetch(
  breaker: CircuitBreaker,
  url: string,
  init: RequestInit,
  options: ResilientFetchOptions,
): Promise<globalThis.Response> {
  const method = (init.method ?? 'GET').toUpperCase();
  const attempts = method === 'GET' ? Math.max(1, options.attempts ?? 1) : 1;
  const host = new URL(url).host;
  let lastError: unknown;

  for (let attempt = 1; attempt <= attempts; attempt++) {
    breaker.acquire();
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), options.timeoutMs);
    const end = clientDuration.startTimer({ server_address: host, http_request_method: method });

    try {
      const response = await fetch(url, {
        ...init,
        headers: { ...(init.headers as Record<string, string> | undefined), ...traceHeaders() },
        signal: controller.signal,
      });
      end({ http_response_status_code: String(response.status) });
      const failed = TRANSIENT.includes(response.status);
      breaker.record(failed);
      if (!failed || attempt === attempts) return response;
    } catch (error) {
      end({ http_response_status_code: 'error' });
      breaker.record(true);
      lastError = error;
      if (attempt === attempts) throw error;
    } finally {
      clearTimeout(timer);
    }

    await new Promise(resolve => setTimeout(resolve, 150 * attempt));
  }

  throw lastError instanceof Error ? lastError : new Error(`Request to ${host} failed.`);
}