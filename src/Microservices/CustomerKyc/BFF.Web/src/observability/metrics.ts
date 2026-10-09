import type { NextFunction, Request, Response } from 'express';
import { Counter, Gauge, Histogram, Registry, collectDefaultMetrics } from 'prom-client';

/**
 * Prometheus metrics at GET /metrics, with the names the .NET components export
 * (OpenTelemetry → Prometheus), so one Grafana panel covers every BFF:
 *   http_server_request_duration_seconds{http_request_method, http_route, http_response_status_code}
 *   http_client_request_duration_seconds{server_address, http_request_method, http_response_status_code}
 *   ewp_circuit_breaker_state{target}       0 closed, 1 half-open, 2 open
 *   ewp_token_exchanges_total{outcome}      token exchanges for Documents Management
 *   ewp_health_status{check}                2 healthy, 0 unhealthy (as /health/ready sees it)
 * plus the Node.js process and runtime (CPU, memory, event-loop lag, GC).
 * Labels never carry a user, an ID or a query string (bounded cardinality, no personal data).
 */
export const registry = new Registry();
registry.setDefaultLabels({ service: 'customer-kyc-bff' });
collectDefaultMetrics({ register: registry });

const DURATION_BUCKETS = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

const serverDuration = new Histogram({
  name: 'http_server_request_duration_seconds',
  help: 'Duration of HTTP requests handled by this BFF.',
  labelNames: ['http_request_method', 'http_route', 'http_response_status_code'],
  buckets: DURATION_BUCKETS,
  registers: [registry],
});

export const clientDuration = new Histogram({
  name: 'http_client_request_duration_seconds',
  help: 'Duration of HTTP calls made by this BFF (KYC API, Documents Management, IDP).',
  labelNames: ['server_address', 'http_request_method', 'http_response_status_code'],
  buckets: DURATION_BUCKETS,
  registers: [registry],
});

export const circuitBreakerState = new Gauge({
  name: 'ewp_circuit_breaker_state',
  help: 'Circuit breaker per downstream service: 0 closed, 1 half-open, 2 open.',
  labelNames: ['target'],
  registers: [registry],
});

export const tokenExchanges = new Counter({
  name: 'ewp_token_exchanges_total',
  help: 'Token exchanges (RFC 8693) for Documents Management, by outcome.',
  labelNames: ['outcome'],
  registers: [registry],
});

export const healthStatus = new Gauge({
  name: 'ewp_health_status',
  help: 'Health check status: 2 healthy, 1 degraded, 0 unhealthy.',
  labelNames: ['check'],
  registers: [registry],
});

/** Times every request; the route is the pattern (e.g. /bff/api/kyc/cases/:caseId), never the URL. */
export function requestMetrics(req: Request, res: Response, next: NextFunction): void {
  const end = serverDuration.startTimer();
  res.on('finish', () => {
    const route = req.route?.path
      ? `${req.baseUrl ?? ''}${String(req.route.path)}`
      : res.statusCode === 404 ? 'unmatched' : 'static';
    end({ http_request_method: req.method, http_route: route, http_response_status_code: String(res.statusCode) });
  });
  next();
}

export async function metricsEndpoint(_req: Request, res: Response): Promise<void> {
  res.setHeader('Content-Type', registry.contentType);
  res.setHeader('Cache-Control', 'no-store');
  res.end(await registry.metrics());
}