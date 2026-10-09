import type { LoggerService } from '@nestjs/common';
import type { NextFunction, Request, Response } from 'express';
import { currentTrace } from './trace-context';

type Level = 'Verbose' | 'Debug' | 'Information' | 'Warning' | 'Error' | 'Fatal';

const SHORT: Record<Level, string> = {
  Verbose: 'VRB', Debug: 'DBG', Information: 'INF', Warning: 'WRN', Error: 'ERR', Fatal: 'FTL',
};

/**
 * Structured logs like the .NET components (Common.Observability, Serilog):
 *  - JSON, one object per line, in Serilog's compact format (@t, @l, @m, @tr) with the service
 *    and environment - ready for a log shipper (Observe, Loki). Chosen by KYC_BFF_LOG_FORMAT=Json,
 *    and the default when NODE_ENV=production;
 *  - otherwise readable text: "[10:27:12 INF] message  <context>  trace=…".
 * Every line carries the request's trace ID. Messages never contain tokens, bodies or names.
 */
export class EwpLogger implements LoggerService {
  private readonly json: boolean;

  constructor(private readonly service: string) {
    const format = (process.env.KYC_BFF_LOG_FORMAT ?? '').toLowerCase();
    this.json = format === 'json' || (format !== 'text' && process.env.NODE_ENV === 'production');
  }

  log(message: unknown, context?: string): void { this.write('Information', message, context); }
  warn(message: unknown, context?: string): void { this.write('Warning', message, context); }
  debug(message: unknown, context?: string): void { this.write('Debug', message, context); }
  verbose(message: unknown, context?: string): void { this.write('Verbose', message, context); }
  fatal(message: unknown, context?: string): void { this.write('Fatal', message, context); }

  error(message: unknown, stackOrContext?: string, context?: string): void {
    // Nest passes (message, stack, context) or (message, context).
    const ctx = context ?? (stackOrContext && !stackOrContext.includes('\n') ? stackOrContext : undefined);
    const stack = stackOrContext && stackOrContext.includes('\n') ? stackOrContext : undefined;
    this.write('Error', message, ctx, stack);
  }

  /** A log line with extra structured properties (e.g. the request line). */
  write(level: Level, message: unknown, context?: string, exception?: string, properties: Record<string, unknown> = {}): void {
    const text = message instanceof Error ? message.message : typeof message === 'string' ? message : JSON.stringify(message);
    const trace = currentTrace();
    const now = new Date();
    const stream = level === 'Error' || level === 'Fatal' ? process.stderr : process.stdout;

    if (this.json) {
      stream.write(JSON.stringify({
        '@t': now.toISOString(),
        ...(level === 'Information' ? {} : { '@l': level }),
        '@m': text,
        ...(exception ? { '@x': exception } : {}),
        ...(trace ? { '@tr': trace.traceId, '@sp': trace.spanId } : {}),
        service: this.service,
        environment: process.env.NODE_ENV ?? 'Development',
        ...(context ? { SourceContext: context } : {}),
        ...properties,
      }) + '\n');
      return;
    }

    const time = now.toTimeString().slice(0, 8);
    stream.write(`[${time} ${SHORT[level]}] ${text}${context ? `  <${context}>` : ''}${trace ? `  trace=${trace.traceId}` : ''}\n`
      + (exception ? `${exception}\n` : ''));
  }
}

/**
 * One line per request, like UseEwpRequestLogging: method, path WITHOUT the query string,
 * status, duration and the caller's subject ID (never a name or a token). 5xx are errors;
 * health probes and metric scrapes are not logged.
 */
export function requestLogging(logger: EwpLogger) {
  return (req: Request, res: Response, next: NextFunction): void => {
    const started = process.hrtime.bigint();
    const path = req.originalUrl.split('?')[0];
    res.on('finish', () => {
      if (path.startsWith('/health') || path === '/metrics') return;
      const elapsed = Number(process.hrtime.bigint() - started) / 1e6;
      const subject = req.session?.user?.subject;
      logger.write(
        res.statusCode >= 500 ? 'Error' : 'Information',
        `HTTP ${req.method} ${path} responded ${res.statusCode} in ${elapsed.toFixed(0)} ms`,
        'RequestLogging',
        undefined,
        { RequestMethod: req.method, RequestPath: path, StatusCode: res.statusCode, Elapsed: elapsed, ...(subject ? { subject } : {}) },
      );
    });
    next();
  };
}