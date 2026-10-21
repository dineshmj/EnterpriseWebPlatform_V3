import { BadRequestException, Body, Controller, Get, Param, ParseIntPipe, Post, Query, Req, UploadedFile, UseGuards, UseInterceptors } from '@nestjs/common';
import { FileInterceptor } from '@nestjs/platform-express';
import { DelegatedRequest, DelegatedTokenGuard, ORDER, Requires, VIEW } from '../auth/delegated-token.guard';
import type { UploadedScan } from '../services/domain-api.client';
import { DigitalGoldService } from './digital-gold.service';

const MAX_SCAN_BYTES = 5 * 1024 * 1024;
const SCAN_TYPES = ['application/pdf', 'image/png', 'image/jpeg'];
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const DOCUMENT_NUMBER = /^[A-Za-z0-9][A-Za-z0-9-]{3,31}$/;
const CUSTOMER_NUMBER = /^CUST-\d{1,12}$/;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The scan is held in memory for this one request only (never written to this server's
// │ disk), at most 5 MB, one file, PDF / PNG / JPEG - Documents Management then checks the
// │ content against the type itself (magic bytes).
// └──────────────────────────────────────────────────────────────────────────────────────
const scanUpload = FileInterceptor('scan', { limits: { fileSize: MAX_SCAN_BYTES, files: 1, fields: 10 } });

/** The counter's Digital Gold: find the customer, see their Digital Gold, record Form VII or a transfer slip. */
@Controller('v1/journeys/partner-products')
@UseGuards(DelegatedTokenGuard)
export class DigitalGoldController {
  constructor(private readonly digitalGold: DigitalGoldService) {}

  @Get('customers')
  @Requires(VIEW)
  search(@Req() request: DelegatedRequest, @Query('search') search?: string) {
    const term = (search ?? '').trim();
    if (term.length < 2 || term.length > 100) throw new BadRequestException('Search with 2 to 100 characters of the customer\'s name or number.');
    return this.digitalGold.searchCustomers(request.delegation!.subjectToken, term);
  }

  @Get('customers/:customerId')
  @Requires(VIEW)
  customer(@Req() request: DelegatedRequest, @Param('customerId', ParseIntPipe) customerId: number) {
    return this.digitalGold.customer(request.delegation!.subjectToken, customerId);
  }

  // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
  // │ The light read the screen polls while a purchase waits for its payment: Partner
  // │ Products only (it limits the answer to the staff member's branch), nothing else.
  // └──────────────────────────────────────────────────────────────────────────────────────
  @Get('customers/:customerNumber/digital-gold')
  @Requires(VIEW)
  digitalGoldOf(@Req() request: DelegatedRequest, @Param('customerNumber') customerNumber: string) {
    if (!CUSTOMER_NUMBER.test(customerNumber)) throw new BadRequestException('An EWP customer number (CUST-...) is required.');
    return this.digitalGold.digitalGoldOf(request.delegation!.subjectToken, customerNumber);
  }

  @Get('applications')
  @Requires(VIEW)
  applications(@Req() request: DelegatedRequest) {
    return this.digitalGold.applications(request.delegation!.subjectToken);
  }

  @Post('applications/:number/send')
  @Requires(ORDER)
  sendFormVii(@Req() request: DelegatedRequest, @Param('number') number: string) {
    // Only an EWP application number goes into the path - never free input.
    if (!/^DGA-\d{6}-[A-Z0-9]{6}$/.test(number)) throw new BadRequestException('Unknown application.');
    return this.digitalGold.sendFormVii(request.delegation!.subjectToken, number);
  }

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ If the path is not an EWP application / purchase number (DGA-… / DGP-…), refuse it:
  // │ only EWP's own references reach the Domain API - amounts, accounts and payees never
  // │ come from the screen.
  // └──────────────────────────────────────────────────────────────────────────────────────
  /** "View at the Partner Bank": a one-time address of the Partner Bank's read-only page for the open account. */
  @Post('applications/:number/partner-view')
  @Requires(VIEW)
  partnerView(@Req() request: DelegatedRequest, @Param('number') number: string) {
    if (!/^DGA-\d{6}-[A-Z0-9]{6}$/.test(number)) throw new BadRequestException('Unknown application.');
    return this.digitalGold.partnerView(request.delegation!.subjectToken, number);
  }

  /** Send a recorded slip to the Partner Bank and pay for it - or continue where it stopped. */
  @Post('purchases/:number/buy')
  @Requires(ORDER)
  buy(@Req() request: DelegatedRequest, @Param('number') number: string) {
    if (!/^DGP-\d{6}-[A-Z0-9]{6}$/.test(number)) throw new BadRequestException('Unknown purchase.');
    return this.digitalGold.buy(request.delegation!.subjectToken, number);
  }

  @Post('customers/:customerId/form-vii')
  @Requires(ORDER)
  @UseInterceptors(scanUpload)
  recordFormVii(
    @Req() request: DelegatedRequest,
    @Param('customerId', ParseIntPipe) customerId: number,
    @Body() body: Record<string, unknown>,
    @UploadedFile() scan: UploadedScan | undefined,
  ) {
    return this.digitalGold.recordFormVii(request.delegation!.subjectToken, customerId, {
      formNumber: documentNumber(body.formNumber, 'Form VII'),
      formSignedOn: date(body.formSignedOn, 'The date Form VII was signed'),
      dateOfBirth: date(body.dateOfBirth, 'The date of birth'),
      identityChecked: body.identityChecked === 'true',
      signatureChecked: body.signatureChecked === 'true',
    }, checkedScan(scan));
  }

  @Post('customers/:customerId/transfer-slips')
  @Requires(ORDER)
  @UseInterceptors(scanUpload)
  recordTransferSlip(
    @Req() request: DelegatedRequest,
    @Param('customerId', ParseIntPipe) customerId: number,
    @Body() body: Record<string, unknown>,
    @UploadedFile() scan: UploadedScan | undefined,
  ) {
    const amount = typeof body.amount === 'string' && /^\d{1,5}(\.\d{1,2})?$/.test(body.amount) ? Number(body.amount) : NaN;
    if (!Number.isFinite(amount) || amount <= 0) throw new BadRequestException('The amount must be in AUD, more than 0, with at most two decimals.');
    if (typeof body.debtorAccountNumber !== 'string' || !/^\d{5,9}$/.test(body.debtorAccountNumber)) throw new BadRequestException('Choose the paying account.');
    if (typeof body.mobileOnSlip !== 'string' || body.mobileOnSlip.length > 30) throw new BadRequestException('Enter the phone number written on the slip.');
    return this.digitalGold.recordTransferSlip(request.delegation!.subjectToken, customerId, {
      slipNumber: documentNumber(body.slipNumber, 'transfer slip'),
      slipSignedOn: date(body.slipSignedOn, 'The date the slip was signed'),
      amount,
      debtorAccountNumber: body.debtorAccountNumber,
      mobileOnSlip: body.mobileOnSlip,
      signatureChecked: body.signatureChecked === 'true',
    }, checkedScan(scan));
  }
}

function documentNumber(value: unknown, what: string): string {
  if (typeof value !== 'string' || !DOCUMENT_NUMBER.test(value.trim())) throw new BadRequestException(`Enter the ${what} number as printed on it (4-32 letters, digits or hyphens).`);
  return value.trim().toUpperCase();
}

function date(value: unknown, what: string): string {
  if (typeof value !== 'string' || !DATE.test(value) || Number.isNaN(Date.parse(value))) throw new BadRequestException(`${what} is required (yyyy-mm-dd).`);
  return value;
}

function checkedScan(scan: UploadedScan | undefined): UploadedScan {
  if (!scan || scan.size === 0) throw new BadRequestException('Attach the scan of the signed document.');
  if (!SCAN_TYPES.includes(scan.mimetype)) throw new BadRequestException('The scan must be a PDF, PNG or JPEG file.');
  return scan;
}