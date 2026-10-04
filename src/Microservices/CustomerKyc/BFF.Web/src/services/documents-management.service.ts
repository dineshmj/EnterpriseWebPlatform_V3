import {
  ForbiddenException,
  Inject,
  Injectable,
  NotFoundException,
  ServiceUnavailableException,
} from '@nestjs/common';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { DocumentsManagementM2mService } from './documents-management-m2m.service';

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
 * Reads KYC evidence from Documents Management with this BFF's M2M identity.
 *
 * Documents are resolved ONLY by business reference (customer number) and
 * document type. There is deliberately no fallback that searches other
 * documents by file name: a missing document is reported as missing.
 *
 * Every call states the signed-in user's branch (X-Actor-Branch), so DM applies
 * branch-scoped object-level authorization to the human behind the M2M call.
 */
@Injectable()
export class DocumentsManagementService {
  constructor(
    private readonly m2m: DocumentsManagementM2mService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  async getIdentityProof(customerNumber: string, actorBranch: string | undefined): Promise<SelectedKycDocument> {
    return this.selectDocument(
      await this.getDocuments(customerNumber, 'KYCProof', actorBranch),
      'Identity proof',
    );
  }

  async getTaxProof(customerNumber: string, actorBranch: string | undefined): Promise<SelectedKycDocument> {
    return this.selectDocument(
      await this.getDocuments(customerNumber, 'TaxProof', actorBranch),
      'Tax proof',
    );
  }

  async getContent(documentId: string, actorBranch: string | undefined): Promise<globalThis.Response> {
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(documentId)) {
      throw new NotFoundException('Invalid document ID.');
    }

    const response = await this.fetchWithRetry(
      `${this.options.documentsManagementApiBaseUrl}/v1/documents/${documentId}/content`,
      actorBranch,
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

  private async getDocuments(
    businessReference: string,
    documentType: string,
    actorBranch: string | undefined,
  ): Promise<DocumentListItem[]> {
    const params = new URLSearchParams({ businessReference, documentType });

    const response = await this.fetchWithRetry(
      `${this.options.documentsManagementApiBaseUrl}/v1/documents?${params.toString()}`,
      actorBranch,
    );

    if (response.status === 403) {
      throw new ForbiddenException('You are not permitted to view documents of this customer.');
    }

    if (!response.ok) {
      throw new ServiceUnavailableException(
        `Documents Management document-list request failed with HTTP ${response.status}.`,
      );
    }

    return (await response.json()) as DocumentListItem[];
  }

  private selectDocument(documents: DocumentListItem[], label: string): SelectedKycDocument {
    if (documents.length === 0) {
      throw new NotFoundException(`No ${label.toLowerCase()} document is associated with this customer.`);
    }

    // The most recently uploaded document of the type is the evidence under review.
    const latest = [...documents].sort(
      (a, b) => new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
    )[0];

    return {
      ...latest,
      selectionReason:
        documents.length === 1
          ? `The only ${label.toLowerCase()} document of this customer.`
          : `The most recent of ${documents.length} ${label.toLowerCase()} documents of this customer.`,
    };
  }

  private async fetchWithRetry(url: string, actorBranch: string | undefined): Promise<globalThis.Response> {
    if (!actorBranch) {
      // Fail closed: without the user's branch DM cannot authorize the read.
      throw new ForbiddenException('Your profile has no branch; documents cannot be shown.');
    }

    const accessToken = await this.m2m.getAccessToken();
    let lastError: unknown;

    // GET requests only: safe to retry on transient failures.
    for (let attempt = 1; attempt <= 3; attempt++) {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 5000);

      try {
        const response = await fetch(url, {
          headers: {
            Authorization: `Bearer ${accessToken}`,
            'X-Actor-Branch': actorBranch,
          },
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