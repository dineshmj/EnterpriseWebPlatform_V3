import { STATUS_CODES } from 'node:http';
import { ArgumentsHost, Catch, ExceptionFilter, HttpException, HttpStatus, Logger } from '@nestjs/common';
import type { Request, Response } from 'express';

/**
 * Every error as RFC 9457 Problem Details (application/problem+json), like the .NET APIs:
 * type, title, status, detail, instance - plus "code" when the caller must react to the reason
 * (e.g. mfa_required). Unexpected errors are logged here and answered with a generic detail:
 * no stack trace, message or internal name reaches the caller (CWE-209).
 */
@Catch()
export class ProblemDetailsFilter implements ExceptionFilter {
  private readonly logger = new Logger('ProblemDetails');

  catch(exception: unknown, host: ArgumentsHost): void {
    const http = host.switchToHttp();
    const request = http.getRequest<Request>();
    const response = http.getResponse<Response>();

    if (!(exception instanceof HttpException)) {
      this.logger.error(`HTTP ${request.method} ${request.path} failed`, exception instanceof Error ? exception.stack : String(exception));
      problem(response, request, HttpStatus.INTERNAL_SERVER_ERROR, 'An unexpected error occurred.');
      return;
    }

    const body = exception.getResponse();
    let detail = exception.message;
    let code: string | undefined;
    if (typeof body === 'string') {
      detail = body;
    } else if (body && typeof body === 'object') {
      const { message, code: reason } = body as { message?: unknown; code?: unknown };
      if (typeof message === 'string') detail = message;
      else if (Array.isArray(message)) detail = message.filter((m) => typeof m === 'string').join(' ');
      if (typeof reason === 'string') code = reason;
    }

    const retryAfter = (exception as { retryAfterSeconds?: unknown }).retryAfterSeconds;
    if (typeof retryAfter === 'number') response.setHeader('Retry-After', String(retryAfter));

    problem(response, request, exception.getStatus(), detail, code);
  }
}

/** Writes one Problem Details response. */
export function problem(response: Response, request: Request, status: number, detail: string, code?: string): void {
  response
    .status(status)
    .type('application/problem+json')
    .send(JSON.stringify({
      type: 'about:blank',
      title: STATUS_CODES[status] ?? 'Error',
      status,
      detail,
      instance: request.path,
      ...(code ? { code } : {}),
    }));
}