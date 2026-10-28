import {
  BadRequestException,
  ConflictException,
  ForbiddenException,
  Injectable,
  Logger,
  NotFoundException,
  ServiceUnavailableException,
} from '@nestjs/common';
import { TokenExchangeService } from './token-exchange.service';

/** A file the staff member uploaded (the scan of a signed form), held in memory for one request. */
export interface UploadedScan {
  originalname: string;
  mimetype: string;
  size: number;
  buffer: Buffer;
}

/** How long a call that goes on to the Partner Bank may take (the Partner Products API's token + call, 15 s each). */
const PARTNER_BANK_TIMEOUT_MS = 40_000;
/** A READ that goes on to the Partner Bank (the holding): longer than a plain call, but inside the web BFF's 30 s for reads. */
const PARTNER_BANK_READ_TIMEOUT_MS = 25_000;

/** What Documents Management answers for an upload: the document and the SHA-256 of its content. */
export interface StoredDocument {
  documentId: string;
  contentHash: string;
}

// ┌─ :: ARCHITECTURE + SECURITY :: Domain API calls, for the person, answers translated ───────────
// │ Why : The Journey API must never act as itself, and a domain API's raw error would leak its
// │       internals to the screen.
// │ How : For each call the person's token is exchanged (RFC 8693) for exactly that API's scope;
// │       the domain's own refusals (400 / 409, written for the counter) pass through; 401 / 403
// │       become a plain 403, 404 a 404, anything else a 503 "try again" - with timeouts.
// │ Ref : Features 2.1.2, 2.4.1 | RFC 8693 | CWE-209 | OWASP API5
// └────────────────────────────────────────────────────────────────────────────────────────────────
@Injectable()
export class DomainApiClient {
  private readonly logger = new Logger(DomainApiClient.name);

  constructor(private readonly exchange: TokenExchangeService) {}

  async get<T>(subjectToken: string, scope: string, url: string, what: string): Promise<T> {
    return this.call<T>(subjectToken, scope, 'GET', url, what);
  }

  async post<T>(subjectToken: string, scope: string, url: string, body: unknown, what: string, headers: Record<string, string> = {}): Promise<T> {
    return this.call<T>(subjectToken, scope, 'POST', url, what, JSON.stringify(body), 'application/json', headers);
  }

  // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
  // │ A call the Partner Products API forwards to the Partner Bank (token + instruction,
  // │ up to 15 s each): waited for longer than a plain Domain API call, so the Partner
  // │ Products API's own answer ("not reachable - it stays SENT, send again") reaches the
  // │ counter instead of this API's generic time-out.
  // └──────────────────────────────────────────────────────────────────────────────────────
  // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
  // │ A read the Partner Products API answers FROM the Partner Bank (token + GET, each up
  // │ to 15 s there): a slow first answer after a restart must not be cut off at 15 s and
  // │ shown as "not reachable" - but the whole screen load must still end within the web
  // │ BFF's 30 s for reads, so 25 s.
  // └──────────────────────────────────────────────────────────────────────────────────────
  async getViaPartnerBank<T>(subjectToken: string, scope: string, url: string, what: string): Promise<T> {
    return this.call<T>(subjectToken, scope, 'GET', url, what, undefined, undefined, {}, PARTNER_BANK_READ_TIMEOUT_MS);
  }

  async postViaPartnerBank<T>(subjectToken: string, scope: string, url: string, what: string): Promise<T> {
    return this.call<T>(subjectToken, scope, 'POST', url, what, '{}', 'application/json', {}, PARTNER_BANK_TIMEOUT_MS);
  }

  /**
   * Stores a scan in Documents Management FOR the person (their branch): the type and business
   * reference go in DM's headers; DM checks the content against the declared type itself.
   */
  async upload(subjectToken: string, url: string, scan: UploadedScan, documentType: string, businessReference: string): Promise<StoredDocument> {
    const form = new FormData();
    form.append('File', new Blob([new Uint8Array(scan.buffer)], { type: scan.mimetype }), scan.originalname);
    return this.call<StoredDocument>(subjectToken, 'documents-management.write', 'POST', url, 'Documents Management', form, undefined, {
      'X-Document-Type': documentType,
      'X-Business-Reference': businessReference,
    });
  }

  /** Best effort: removes a scan that ended up attached to nothing (the record was refused after the upload). */
  async removeDocument(subjectToken: string, url: string): Promise<void> {
    try {
      await this.call(subjectToken, 'documents-management.write', 'DELETE', url, 'Documents Management');
    } catch (error) {
      this.logger.warn(`An unused scan could not be removed (${url}): ${(error as Error).message}`);
    }
  }

  private async call<T>(
    subjectToken: string, scope: string, method: 'GET' | 'POST' | 'DELETE', url: string, what: string,
    body?: string | FormData, contentType?: string, extraHeaders: Record<string, string> = {}, timeoutMs = 15_000,
  ): Promise<T> {
    // ┌─ SECURITY ─────────────────────────────────────────────────────────────────────────────
    // │ The Journey API must never act as itself, and a domain API's raw error would leak its
    // │ internals to the screen. For each call the person's token is exchanged (RFC 8693) for
    // │ exactly that API's scope; the domain's own refusals (400 / 409, written for the counter)
    // │ pass through; 401 / 403 become a plain 403, 404 a 404, anything else a 503 "try again"
    // │ - with timeouts.
    // └──────────────────────────────────────────────────────────────────────────────────────
    const token = await this.exchange.exchange(subjectToken, scope);
    
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), timeoutMs);
    let response: Response;

    try {
      // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
      // │ A domain API never redirects: a redirect is refused (an error, so "unavailable"),
      // │ never followed - the exchanged token goes to the configured API and nowhere else.
      // └──────────────────────────────────────────────────────────────────────────────────────
      response = await fetch(url, {
        method,
        headers: { Authorization: `Bearer ${token}`, Accept: 'application/json', ...(contentType ? { 'Content-Type': contentType } : {}), ...extraHeaders },
        body,
        signal: controller.signal,
        redirect: 'error',
      });
    } catch (error) {
      this.logger.warn(`${method} ${url} failed: ${(error as Error).message}`);
      if (timeoutMs === PARTNER_BANK_TIMEOUT_MS) {
        throw new ServiceUnavailableException(`${what} did not answer in time. Nothing is lost: what was sent stays SENT - send it again shortly (it is done once only).`);
      }
      throw new ServiceUnavailableException(`${what} is unavailable. Try again shortly.`);
    } finally {
      clearTimeout(timer);
    }

    if (response.status === 204) return undefined as T;
    const payload = (await response.json().catch(() => ({}))) as { detail?: string; errors?: Record<string, string[]> };
    if (response.ok) return payload as T;

    const detail = payload.detail ?? Object.values(payload.errors ?? {}).flat()[0];

    switch (response.status) {
      // 400, and 422 from Documents Management (e.g. a file whose content is not what its type says): the reason passes.
      case 400:
      case 422: throw new BadRequestException(detail ?? 'The request was not valid.');
      case 401:
      case 403: throw new ForbiddenException('You are not allowed to do this.');
      case 404: throw new NotFoundException('Not found.');
      case 409: throw new ConflictException(detail ?? 'This has already been done.');
      // The Partner Products API's own 503 says what to do (e.g. "Form VII stays SENT: send it again").
      case 503: throw new ServiceUnavailableException(detail ?? `${what} could not answer. Try again shortly.`);
      default:
        this.logger.warn(`${method} ${url} answered HTTP ${response.status}.`);
        throw new ServiceUnavailableException(`${what} could not answer. Try again shortly.`);
    }
  }
}