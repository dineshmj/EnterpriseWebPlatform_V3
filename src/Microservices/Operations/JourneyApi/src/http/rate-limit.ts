import { HttpException, HttpStatus } from '@nestjs/common';
import type { NextFunction, Request, Response } from 'express';
import { problem } from './problem-details.filter';

/** At most `permits` requests per `windowSeconds`. */
export interface RateLimit {
  permits: number;
  windowSeconds: number;
}

/** The overall limit on every request to this API, before any token is checked. */
export const OVERALL_LIMIT: RateLimit = { permits: 600, windowSeconds: 60 };
/** Per person and calling client: reading (the dashboard polls every 10 seconds). */
export const READ_LIMIT: RateLimit = { permits: 120, windowSeconds: 60 };
/** Per person and calling client: privileged changes (the simulators' behaviour). */
export const CHANGE_LIMIT: RateLimit = { permits: 10, windowSeconds: 60 };

/** 429 with Retry-After (the Problem Details filter writes the header). */
export class TooManyRequestsException extends HttpException {
  constructor(readonly retryAfterSeconds: number) {
    super('Too many requests. Try again shortly.', HttpStatus.TOO_MANY_REQUESTS);
  }
}

/**
 * Fixed-window counters in memory, per key (OWASP API4: unrestricted resource consumption). Each
 * instance of the API counts on its own; the limits are per instance.
 */
export class RateLimiter {
  private readonly windows = new Map<string, { endsAt: number; count: number }>();

  /** Counts one request for `key`; throws TooManyRequestsException when its permits are used up. */
  take(key: string, limit: RateLimit, now = Date.now()): void {
    let window = this.windows.get(key);
    if (!window || now >= window.endsAt) {
      if (this.windows.size >= 10_000) this.prune(now);
      window = { endsAt: now + limit.windowSeconds * 1000, count: 0 };
      this.windows.set(key, window);
    }
    window.count++;
    if (window.count > limit.permits) {
      throw new TooManyRequestsException(Math.max(1, Math.ceil((window.endsAt - now) / 1000)));
    }
  }

  private prune(now: number): void {
    for (const [key, window] of this.windows) {
      if (now >= window.endsAt) this.windows.delete(key);
    }
  }
}

/** Express middleware for the overall limit: answers 429 itself, before routing and authentication. */
export function overallRateLimit(limiter: RateLimiter) {
  return (req: Request, res: Response, next: NextFunction) => {
    if (req.path.startsWith('/health')) return next();
    try {
      limiter.take('overall', OVERALL_LIMIT);
      next();
    } catch (error) {
      if (!(error instanceof TooManyRequestsException)) throw error;
      console.warn(`HTTP ${req.method} ${req.path} refused: overall rate limit reached.`);
      res.setHeader('Retry-After', String(error.retryAfterSeconds));
      problem(res, req, HttpStatus.TOO_MANY_REQUESTS, 'Too many requests. Try again shortly.');
    }
  };
}