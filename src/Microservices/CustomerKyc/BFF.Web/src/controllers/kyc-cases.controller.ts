import {
  BadRequestException,
  Controller,
  Get,
  Headers,
  Param,
  Post,
  Query,
  Req,
  Res,
  UnauthorizedException,
} from '@nestjs/common';
import { Request, Response } from 'express';
import { KycApiService } from '../services/kyc-api.service';
import { DocumentsManagementService } from '../services/documents-management.service';

interface DecisionBody {
  decisionRemarks?: unknown;
}

@Controller('bff/api/kyc')
export class KycCasesController {
  constructor(
    private readonly api: KycApiService,
    private readonly documents: DocumentsManagementService,
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
    const customerNumber = await this.getCustomerNumber(req, caseId);
    res.json(await this.documents.getIdentityProof(customerNumber));
  }

  @Get('cases/:caseId/tax-proof')
  async taxProof(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const caseId = this.parseCaseId(caseIdRaw);
    const customerNumber = await this.getCustomerNumber(req, caseId);
    res.json(await this.documents.getTaxProof(customerNumber));
  }

  @Get('cases/:caseId/identity-proof/content')
  async identityProofContent(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const caseId = this.parseCaseId(caseIdRaw);
    const customerNumber = await this.getCustomerNumber(req, caseId);
    const document = await this.documents.getIdentityProof(customerNumber);
    const documentResponse = await this.documents.getContent(document.documentId);
    return this.forwardDocumentContent(documentResponse, res, document.fileName, document.contentType);
  }

  @Get('cases/:caseId/tax-proof/content')
  async taxProofContent(@Param('caseId') caseIdRaw: string, @Req() req: Request, @Res() res: Response) {
    this.requireSession(req);
    const caseId = this.parseCaseId(caseIdRaw);
    const customerNumber = await this.getCustomerNumber(req, caseId);
    const document = await this.documents.getTaxProof(customerNumber);
    const documentResponse = await this.documents.getContent(document.documentId);
    return this.forwardDocumentContent(documentResponse, res, document.fileName, document.contentType);
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

    const response = await this.api.decideStage(req, caseId, stage, action, remarks);
    return this.forwardApiResponse(response, res);
  }

  private requireSession(req: Request): void {
    if (!req.session.user || !req.session.accessToken) throw new UnauthorizedException();
  }

  private requireCsrf(req: Request, suppliedToken?: string): void {
    if (!suppliedToken || !req.session.csrfToken || suppliedToken !== req.session.csrfToken) {
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

  private async getCustomerNumber(req: Request, caseId: number): Promise<string> {
    const response = await this.api.getCase(req, caseId);
    if (!response.ok) throw new BadRequestException(`Unable to resolve KYC Case ${caseId}; KYC API returned HTTP ${response.status}.`);
    const body = await response.json() as { customerNumber?: string };
    if (!body.customerNumber) throw new BadRequestException(`KYC Case ${caseId} did not contain a customer number.`);
    return body.customerNumber;
  }

  private async forwardApiResponse(response: globalThis.Response, res: Response) {
    const body = await response.text();
    res.status(response.status).type(response.headers.get('content-type') ?? 'application/json').send(body);
  }

  private async forwardDocumentContent(response: globalThis.Response, res: Response, fileName: string, contentType: string) {
    const buffer = Buffer.from(await response.arrayBuffer());
    res.status(response.status);
    res.type(contentType || 'application/pdf');
    res.setHeader('Content-Disposition', `inline; filename="${fileName.replace(/"/g, '')}"`);
    return res.send(buffer);
  }
}
