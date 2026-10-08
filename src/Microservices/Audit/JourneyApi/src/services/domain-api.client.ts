import {
  ForbiddenException,
  HttpException,
  Injectable,
  Logger,
  NotFoundException,
  ServiceUnavailableException,
} from '@nestjs/common';
import { TokenExchangeService } from './token-exchange.service';

/**
 * Calls a Domain API for the person: exchanges their token for one aimed at that API, then GETs.
 * Reads only (GET), so one retry on a timeout or a 502/503/504 is safe. The answer is mapped to
 * what the caller should see - a downstream 401/403 becomes 403, 404 stays 404, anything else
 * unavailable is 503 - and the downstream's own body is never passed through.
 */
@Injectable()
export class DomainApiClient {
  private readonly logger = new Logger(DomainApiClient.name);

  constructor(private readonly exchange: TokenExchangeService) {}

  async get<T>(subjectToken: string, scope: string, url: string): Promise<T> {
    const token = await this.exchange.exchange(subjectToken, scope);

    for (let attempt = 1; ; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 10_000);
      try {
        const response = await fetch(url, {
          headers: { Authorization: `Bearer ${token}`, Accept: 'application/json' },
          signal: controller.signal,
        });

        if (response.ok) return (await response.json()) as T;
        if (response.status === 401 || response.status === 403) throw new ForbiddenException('You are not allowed to see this.');
        if (response.status === 404) throw new NotFoundException('Not found.');
        if (attempt < 2 && [502, 503, 504].includes(response.status)) continue;

        this.logger.warn(`GET ${url} answered HTTP ${response.status}.`);
        throw new ServiceUnavailableException('A service behind this one is unavailable. Try again shortly.');
      } catch (error) {
        if (error instanceof HttpException) throw error;
        if (attempt < 2) continue;
        this.logger.warn(`GET ${url} failed: ${(error as Error).message}`);
        throw new ServiceUnavailableException('A service behind this one is unavailable. Try again shortly.');
      } finally {
        clearTimeout(timer);
      }
    }
  }
}