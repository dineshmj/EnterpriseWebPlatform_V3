import {
  BadRequestException,
  ServiceUnavailableException,
  Controller,
  Get,
  Headers,
  Inject,
  Param,
  Post,
  Query,
  Req,
  Res,
  UnauthorizedException,
} from '@nestjs/common';
import { Request, Response } from 'express';
import { Readable } from 'node:stream';
import type { ReadableStream as NodeReadableStream } from 'node:stream/web';
import { safeEqual } from '../auth/csrf';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { KycApiService } from '../services/kyc-api.service';
import { DocumentsManagementService } from '../services/documents-management.service';

interface DecisionBody {
  decisionRemarks?: unknown;
}

interface CaseEvidence {
  applicationNumber: string;
  identityProofDocumentId: string;
  taxProofDocumentId: string;
}

@Controller('bff/api/kyc')
export class KycCasesController {
  constructor(
    private readonly api: KycApiService,
    private readonly documents: DocumentsManagementService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  @Get('cases')
  async cases(
    @Query('pageNumber') pageNumberRaw: string | undefined,
    @Query('pageSize') pageSizeRaw: string | undefined,
    @Query('status') status: string | undefined,
    @Query('stage') stage: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    this.requireSession(req);
    const pageNumber = Math.max(1, Math.min(1000, Number(pageNumberRaw ?? '1') || 1));
    const pageSize = Math.max(1, Math.min(100, Number(pageSizeRaw ?? '25') || 25));
    const response = await this.api.getCases(req, pageNumber, pageSize, status, stage);
    const body = await response.text();
    res.status(response.status).type(response.headers.get('content-type') ?? 'application/json').send(body);
  }

  @Get('cases/:caseId')
  async caseById(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const caseId = this.parseCaseId(caseIdRaw);
    const response = await this.api.getCase(req, caseId);
    const body = await response.text();
    res.status(response.status).type(response.headers.get('content-type') ?? 'application/json').send(body);
  }

  @Post('cases/:caseId/identity-verification/approve')
  async approveIdentity(
    @Param('caseId') caseIdRaw: string,
    @Headers('x-csrf-token') csrfToken: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    return this.decideStage(req, res, caseIdRaw, csrfToken, 'identity-verification', 'approve');
  }

  @Post('cases/:caseId/identity-verification/reject')
  async rejectIdentity(
    @Param('caseId') caseIdRaw: string,
    @Headers('x-csrf-token') csrfToken: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    return this.decideStage(req, res, caseIdRaw, csrfToken, 'identity-verification', 'reject');
  }

  @Post('cases/:caseId/document-verification/approve')
  async approveDocument(
    @Param('caseId') caseIdRaw: string,
    @Headers('x-csrf-token') csrfToken: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    return this.decideStage(req, res, caseIdRaw, csrfToken, 'document-verification', 'approve');
  }

  @Post('cases/:caseId/document-verification/reject')
  async rejectDocument(
    @Param('caseId') caseIdRaw: string,
    @Headers('x-csrf-token') csrfToken: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    return this.decideStage(req, res, caseIdRaw, csrfToken, 'document-verification', 'reject');
  }

  @Get('cases/:caseId/identity-proof')
  async identityProof(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const caseId = this.parseCaseId(caseIdRaw);
    const evidence = await this.getCaseEvidence(req, caseId);
    res.json(await this.documents.getEvidence(evidence.identityProofDocumentId, 'Identity proof', evidence.applicationNumber, req.session.user?.branch));
  }

  @Get('cases/:caseId/tax-proof')
  async taxProof(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const caseId = this.parseCaseId(caseIdRaw);
    const evidence = await this.getCaseEvidence(req, caseId);
    res.json(await this.documents.getEvidence(evidence.taxProofDocumentId, 'Tax proof', evidence.applicationNumber, req.session.user?.branch));
  }

  @Get('cases/:caseId/identity-proof/content')
  async identityProofContent(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const branch = req.session.user?.branch;
    const caseId = this.parseCaseId(caseIdRaw);
    const evidence = await this.getCaseEvidence(req, caseId);
    const document = await this.documents.getEvidence(evidence.identityProofDocumentId, 'Identity proof', evidence.applicationNumber, branch);
    const documentResponse = await this.documents.getContent(document.documentId, branch);
    return this.forwardDocumentContent(documentResponse, res, document.fileName);
  }

  @Get('cases/:caseId/tax-proof/content')
  async taxProofContent(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const branch = req.session.user?.branch;
    const caseId = this.parseCaseId(caseIdRaw);
    const evidence = await this.getCaseEvidence(req, caseId);
    const document = await this.documents.getEvidence(evidence.taxProofDocumentId, 'Tax proof', evidence.applicationNumber, branch);
    const documentResponse = await this.documents.getContent(document.documentId, branch);
    return this.forwardDocumentContent(documentResponse, res, document.fileName);
  }

  private async decideStage(
    req: Request,
    res: Response,
    caseIdRaw: string,
    csrfToken: string | undefined,
    stage: 'identity-verification' | 'document-verification',
    action: 'approve' | 'reject',
  ) {
    this.requireSession(req);
    this.requireCsrf(req, csrfToken);
    const caseId = this.parseCaseId(caseIdRaw);
    const remarks = await this.readDecisionRemarks(req);
    if (action === 'reject' && !remarks) {
      throw new BadRequestException('Decision remarks are required when rejecting a KYC verification stage.');
    }

    let response: globalThis.Response;
    try {
      response = await this.api.decideStage(req, caseId, stage, action, remarks);
    } catch {
      // Sent once, never retried: the answer was lost, not necessarily the decision.
      throw new ServiceUnavailableException('The KYC service did not answer in time. Reload the case to see whether your decision was applied.');
    }
    return this.forwardApiResponse(response, res);
  }

  private requireSession(req: Request): void {
    if (!req.session.user || !req.session.accessToken) throw new UnauthorizedException();
  }

  private requireCsrf(req: Request, suppliedToken?: string): void {
    if (!safeEqual(suppliedToken, req.session.csrfToken)) {
      throw new UnauthorizedException('Invalid CSRF token.');
    }
  }

  private parseCaseId(raw: string): number {
    const caseId = Number(raw);
    if (!Number.isSafeInteger(caseId) || caseId <= 0) throw new BadRequestException('caseId must be a positive integer.');
    return caseId;
  }

  private async readDecisionRemarks(req: Request): Promise<string> {
    const body = req.body as DecisionBody | undefined;
    const remarks = typeof body?.decisionRemarks === 'string' ? body.decisionRemarks.trim() : '';
    if (remarks.length > 4000) throw new BadRequestException('Decision remarks cannot exceed 4000 characters.');
    return remarks;
  }

  /** The evidence the case recorded at submission (the KYC API applies branch scope). */
  private async getCaseEvidence(req: Request, caseId: number): Promise<CaseEvidence> {
    const response = await this.api.getCase(req, caseId);
    if (!response.ok) throw new BadRequestException(`Unable to resolve KYC Case ${caseId}; KYC API returned HTTP ${response.status}.`);
    const body = await response.json() as Partial<CaseEvidence>;
    if (!body.identityProofDocumentId || !body.taxProofDocumentId || !body.applicationNumber) {
      throw new BadRequestException(`KYC Case ${caseId} does not name its evidence documents.`);
    }
    return body as CaseEvidence;
  }

  private async forwardApiResponse(response: globalThis.Response, res: Response) {
    const body = await response.text();
    res.status(response.status).type(response.headers.get('content-type') ?? 'application/json').send(body);
  }

  /**
   * Relays evidence to the officer's browser.
   *
   * The content type is the one Documents Management VERIFIED from the file
   * signature (never the uploader's claim). Only PDF is rendered inline (with
   * nosniff, framable only by this MFE); every other type is downloaded.
   * The body is streamed rather than buffered in memory.
   */
  private async forwardDocumentContent(response: globalThis.Response, res: Response, fileName: string) {
    const verifiedType = (response.headers.get('content-type') ?? '').split(';')[0].trim().toLowerCase();
    const safeName = fileName.replace(/[^\w.\- ]/g, '_');
    const inline = verifiedType === 'application/pdf';

    res.status(response.status);
    res.setHeader('Content-Type', inline ? 'application/pdf' : 'application/octet-stream');
    res.setHeader('Content-Disposition', `${inline ? 'inline' : 'attachment'}; filename="${safeName}"`);
    res.setHeader('X-Content-Type-Options', 'nosniff');
    // frame-ancestors is checked against EVERY ancestor frame: the PDF iframe sits
    // inside the KYC MFE ('self'), which itself sits inside the Shell. Both must be
    // allowed; any other site still cannot frame the evidence.
    res.setHeader('Content-Security-Policy', `frame-ancestors 'self' ${this.options.shellOrigin}`);
    res.setHeader('Cache-Control', 'no-store');

    const contentLength = response.headers.get('content-length');
    if (contentLength) res.setHeader('Content-Length', contentLength);

    if (!response.body) return res.end();
    Readable.fromWeb(response.body as unknown as NodeReadableStream).pipe(res);
  }
}