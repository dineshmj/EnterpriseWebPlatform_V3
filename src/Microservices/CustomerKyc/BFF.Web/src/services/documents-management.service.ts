import { Inject, Injectable, NotFoundException, ServiceUnavailableException } from '@nestjs/common';
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

@Injectable()
export class DocumentsManagementService {
  constructor(
    private readonly m2m: DocumentsManagementM2mService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  async getIdentityProof(customerNumber: string): Promise<SelectedKycDocument> {
    const documents = await this.getDocuments(customerNumber, 'KYCProof');
    if (documents.length > 0) {
      return this.selectDocument(
        documents,
        /driver|license|licence|identity|kyc/i,
        'Identity proof',
      );
    }

    return this.selectLegacyDocument(
      await this.getAllDocuments(),
      /driver|license|licence|identity|kyc/i,
      'Identity proof',
    );
  }

  async getTaxProof(customerNumber: string): Promise<SelectedKycDocument> {
    const documents = await this.getDocuments(customerNumber, 'TaxProof');
    if (documents.length > 0) {
      return this.selectDocument(
        documents,
        /tax|form[\s_-]*16|itr|income/i,
        'Tax proof',
      );
    }

    return this.selectLegacyDocument(
      await this.getAllDocuments(),
      /tax|form[\s_-]*16|itr|income/i,
      'Tax proof',
    );
  }

  async getContent(documentId: string): Promise<globalThis.Response> {
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(documentId)) {
      throw new NotFoundException('Invalid document ID.');
    }

    const token = await this.m2m.getAccessToken();
    const response = await this.fetchWithRetry(
      `${this.options.documentsManagementApiBaseUrl}/v1/documents/${documentId}/content`,
      token,
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
  ): Promise<DocumentListItem[]> {
    const token = await this.m2m.getAccessToken();
    const params = new URLSearchParams({
      businessReference,
      documentType,
    });

    const response = await this.fetchWithRetry(
      `${this.options.documentsManagementApiBaseUrl}/v1/documents?${params.toString()}`,
      token,
    );

    if (!response.ok) {
      throw new ServiceUnavailableException(
        `Documents Management document-list request failed with HTTP ${response.status}.`,
      );
    }

    return (await response.json()) as DocumentListItem[];
  }

  private async getAllDocuments(): Promise<DocumentListItem[]> {
    const token = await this.m2m.getAccessToken();

    const response = await this.fetchWithRetry(
      `${this.options.documentsManagementApiBaseUrl}/v1/documents`,
      token,
    );

    if (!response.ok) {
      throw new ServiceUnavailableException(
        `Documents Management document-list request failed with HTTP ${response.status}.`,
      );
    }

    return (await response.json()) as DocumentListItem[];
  }

  private selectLegacyDocument(
    documents: DocumentListItem[],
    filenamePattern: RegExp,
    label: string,
  ): SelectedKycDocument {
    const matches = documents.filter(document => filenamePattern.test(document.fileName));

    if (matches.length === 0) {
      throw new NotFoundException(
        `No ${label.toLowerCase()} document could be resolved from the existing Documents Management records.`,
      );
    }

    if (matches.length > 1) {
      throw new ServiceUnavailableException(
        `Multiple legacy ${label.toLowerCase()} documents match the filename-based fallback. Existing documents need business-reference metadata before this KYC case can be displayed safely.`,
      );
    }

    return {
      ...matches[0],
      selectionReason:
        'Legacy fallback: this document predates business-reference metadata and was uniquely identified by filename.',
    };
  }

  private selectDocument(
    documents: DocumentListItem[],
    filenamePattern: RegExp,
    label: string,
  ): SelectedKycDocument {
    if (documents.length === 0) {
      throw new NotFoundException(
        `No ${label.toLowerCase()} document is associated with this customer.`,
      );
    }

    const ordered = [...documents].sort(
      (a, b) =>
        new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime(),
    );

    const match = ordered.find(document => filenamePattern.test(document.fileName));
    const selected = match ?? ordered[0];

    return {
      ...selected,
      selectionReason: match
        ? `Selected by filename within the ${label} document type.`
        : `Selected as the only/first document returned for the customer and document type.`,
    };
  }

  private async fetchWithRetry(url: string, accessToken: string): Promise<globalThis.Response> {
    let lastError: unknown;

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
