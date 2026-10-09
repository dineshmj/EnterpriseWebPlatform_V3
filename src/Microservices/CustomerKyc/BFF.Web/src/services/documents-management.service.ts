import {
  ForbiddenException,
  Inject,
  Injectable,
  NotFoundException,
  ServiceUnavailableException,
} from '@nestjs/common';
import { Request } from 'express';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { DocumentsManagementTokenService } from './documents-management-token.service';

const DOCUMENT_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

interface DocumentListItem {
  documentId: string;
  fileName: string;
  contentType: string;
  size: number;
  createdAt: string;
  documentType?: string | null;
  businessReference?: string | null;
}

export interface SelectedKycDocument extends DocumentListItem {
  selectionReason: string;
}

/**
 * Reads KYC evidence from Documents Management for the signed-in officer: every call carries
 * a token exchanged for the officer (RFC 8693), from which Documents Management takes the
 * officer's branch (branch-scoped object-level authorization) and sees this BFF as the acting
 * client. No machine identity and no branch header asserted by the BFF.
 *
 * Evidence is resolved ONLY by the document IDs the KYC case recorded from the
 * application's submission. There is deliberately no fallback that searches the
 * customer's other documents: a missing document is reported as missing.
 */
@Injectable()
export class DocumentsManagementService {
  constructor(
    private readonly tokens: DocumentsManagementTokenService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  /**
   * The evidence document submitted with the KYC case's application, by its ID: the
   * officer reviews exactly what was submitted, never "the latest" of the customer.
   */
  async getEvidence(
    req: Request,
    documentId: string,
    label: string,
    applicationNumber: string,
  ): Promise<SelectedKycDocument> {
    if (!DOCUMENT_ID.test(documentId)) {
      throw new NotFoundException(`The case names no valid ${label.toLowerCase()} document.`);
    }

    const response = await this.fetchWithRetry(
      req,
      `${this.options.documentsManagementApiBaseUrl}/v1/documents/${documentId}`,
    );

    if (response.status === 404) {
      throw new NotFoundException(`The ${label.toLowerCase()} submitted with this application was not found.`);
    }

    if (!response.ok) {
      throw new ServiceUnavailableException(
        `Documents Management document request failed with HTTP ${response.status}.`,
      );
    }

    const document = (await response.json()) as DocumentListItem;
    return {
      ...document,
      selectionReason: `The ${label.toLowerCase()} submitted with application ${applicationNumber}.`,
    };
  }

  async getContent(req: Request, documentId: string): Promise<globalThis.Response> {
    if (!DOCUMENT_ID.test(documentId)) {
      throw new NotFoundException('Invalid document ID.');
    }

    const response = await this.fetchWithRetry(
      req,
      `${this.options.documentsManagementApiBaseUrl}/v1/documents/${documentId}/content`,
    );

    if (response.status === 404) {
      throw new NotFoundException('Document content was not found.');
    }

    if (!response.ok) {
      throw new ServiceUnavailableException(
        `Documents Management content request failed with HTTP ${response.status}.`,
      );
    }

    return response;
  }

  private async fetchWithRetry(req: Request, url: string): Promise<globalThis.Response> {
    if (!req.session.user?.branch) {
      // Stop early with a clear message: without a branch, Documents Management denies the read.
      throw new ForbiddenException('Your profile has no branch; documents cannot be shown.');
    }

    const accessToken = await this.tokens.getToken(req);
    let lastError: unknown;

    // GET requests only: safe to retry on transient failures.
    for (let attempt = 1; attempt <= 3; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 5000);

      try {
        const response = await fetch(url, {
          headers: { Authorization: `Bearer ${accessToken}` },
          signal: controller.signal,
        });

        if (![408, 429, 500, 502, 503, 504].includes(response.status) || attempt === 3) {
          return response;
        }
      } catch (error) {
        lastError = error;
        if (attempt === 3) throw error;
      } finally {
        clearTimeout(timer);
      }

      await new Promise(resolve => setTimeout(resolve, 150 * attempt));
    }

    throw lastError instanceof Error
      ? lastError
      : new ServiceUnavailableException('Documents Management request failed.');
  }
}