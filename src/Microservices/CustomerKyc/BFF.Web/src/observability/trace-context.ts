import { AsyncLocalStorage } from 'node:async_hooks';
import { randomBytes } from 'node:crypto';
import type { NextFunction, Request, Response } from 'express';

/** The W3C trace context of the request being handled. */
export interface TraceContext {
  traceId: string;
  spanId: string;
}

const storage = new AsyncLocalStorage<TraceContext>();
const TRACEPARENT = /^00-([0-9a-f]{32})-([0-9a-f]{16})-[0-9a-f]{2}$/;

/**
 * W3C trace context (the same as the .NET components): every request continues the caller's
 * trace (a valid `traceparent` header) or starts one, and every log line and outgoing call of
 * that request carries it. So an officer's decision is ONE trace from this BFF through the KYC
 * API and Kafka, not a new trace at the KYC API.
 *
 * Only the context is propagated: this BFF does not export spans of its own (no OpenTelemetry
 * SDK here), so in Jaeger / Tempo the KYC API's span is the first one shown for the trace.
 */
export function traceMiddleware(req: Request, _res: Response, next: NextFunction): void {
  const incoming = TRACEPARENT.exec(String(req.headers.traceparent ?? ''));
  const traceId = incoming && !/^0+$/.test(incoming[1]) ? incoming[1] : randomBytes(16).toString('hex');
  storage.run({ traceId, spanId: randomBytes(8).toString('hex') }, next);
}

export function currentTrace(): TraceContext | undefined {
  return storage.getStore();
}

/** The `traceparent` header for an outgoing call: same trace, a new span ID for the call. */
export function traceHeaders(): Record<string, string> {
  const trace = storage.getStore();
  return trace ? { traceparent: `00-${trace.traceId}-${randomBytes(8).toString('hex')}-01` } : {};
}