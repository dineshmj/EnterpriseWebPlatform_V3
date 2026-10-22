import { BadRequestException, ConflictException, ForbiddenException, Inject, Injectable } from '@nestjs/common';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { DomainApiClient, UploadedScan } from '../services/domain-api.client';
import { PurchaseWorkflowStarter } from '../services/purchase-workflow.starter';

/** The customer as EWP's Customer Onboarding API knows it (GET /v1/customers/{id}). */
interface CustomerDetails {
  customerId: number;
  customerNumber: string;
  firstName: string;
  lastName: string;
  phoneNumber: string;
  status: string;
}

interface CustomerListItem {
  customerId: number;
  customerNumber: string;
  firstName: string;
  lastName: string;
  status: string;
}

/** A customer's account as the Accounts API offers it for a payment (GET /v1/accounts/for-payment). */
interface PaymentAccount {
  customerNumber: string;
  holderName: string;
  bsb: string;
  accountNumber: string;
  product: string;
  currency: string;
  available: number;
}

/** What the Partner Products Domain API records for a customer. */
export interface CustomerDigitalGold {
  customerNumber: string;
  application: { number: string; status: string; partnerAccountNumber: string | null } | null;
  purchases: unknown[];
}

export interface FormViiFields {
  formNumber: string;
  formSignedOn: string;
  dateOfBirth: string;
  identityChecked: boolean;
  signatureChecked: boolean;
}

export interface TransferSlipFields {
  slipNumber: string;
  slipSignedOn: string;
  amount: number;
  debtorAccountNumber: string;
  mobileOnSlip: string;
  signatureChecked: boolean;
}

const CO_READ = 'customer-onboarding.read';
const ACCOUNTS_READ = 'accounts.read';
const PP_READ = 'partner-products.read';
const PP_WRITE = 'partner-products.write';
const PAYMENTS_WRITE = 'payments.write';

/** A purchase as the Partner Products Domain API shows it (the fields this journey reads). */
export interface PurchaseView {
  number: string;
  status: string;
  paymentNumber: string | null;
}

/** Exactly what EWP Payments is asked to pay for an ordered purchase (from the Partner Products API). */
interface PurchasePayment {
  idempotencyKey: string;
  customerNumber: string;
  fromBsb: string;
  fromAccountNumber: string;
  payeeName: string;
  toBsb: string;
  toAccountNumber: string;
  amount: number;
  reference: string;
}

/**
 * The counter's Digital Gold journeys (EWP as the Partner Bank's distributor). Each one is a single
 * call for the screen, which this API turns into the Domain API calls it needs - always FOR the
 * signed-in staff member (token exchange), so every Domain API still decides by their own
 * permissions and branch.
 */
@Injectable()
export class DigitalGoldService {
  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    private readonly api: DomainApiClient,
    private readonly workflow: PurchaseWorkflowStarter,
  ) {}

  /** Customers of the staff member's branch whose name or number matches (Customer Onboarding decides the scope). */
  async searchCustomers(token: string, search: string) {
    const params = new URLSearchParams({ search, pageNumber: '1', pageSize: '10' });
    const page = await this.api.get<{ items: CustomerListItem[] }>(token, CO_READ, `${this.options.customerOnboardingApiBaseUrl}/v1/customers?${params}`, 'Customer Onboarding');
    return page.items.map((c) => ({
      customerId: c.customerId,
      customerNumber: c.customerNumber,
      name: `${c.firstName} ${c.lastName}`,
      status: c.status,
    }));
  }

  // ┌─ RESIL ──────────────────────────────────────────────────────────────────────────────
  // │ One screen, three contexts, in parallel: the customer (Customer Onboarding), their
  // │ paying accounts (Accounts) and their Digital Gold (Partner Products). If Accounts
  // │ cannot answer, the customer is still shown and the screen says why the accounts are
  // │ missing - a slip simply cannot be recorded then.
  // └──────────────────────────────────────────────────────────────────────────────────────
  async customer(token: string, customerId: number) {
    const customer = await this.customerDetails(token, customerId);
    const [accounts, digitalGold] = await Promise.all([
      this.accountsOf(token, customer.customerNumber).then((list) => ({ available: true, list })).catch(() => ({ available: false, list: [] as PaymentAccount[] })),
      this.api.get<CustomerDigitalGold>(token, PP_READ,
        `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/customers/${encodeURIComponent(customer.customerNumber)}/digital-gold`, 'Partner Products'),
    ]);
    const mobile = australianMobile(customer.phoneNumber);
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The Partner Bank is the system of record of the gold: once the account is open, its
    // │ holding is read from there (through the Partner Products API's adapter) on every look.
    // │ If the Partner Bank cannot answer, the rest of the screen is still shown.
    // └──────────────────────────────────────────────────────────────────────────────────────
    const application = digitalGold.application;
    const holding = application?.status === 'OPENED'
      ? await this.api.getViaPartnerBank<unknown>(token, PP_READ,
        `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-account-applications/${encodeURIComponent(application.number)}/holding`, 'The Partner Bank')
        .catch(() => null)
      : null;
    return {
      customerId: customer.customerId,
      customerNumber: customer.customerNumber,
      name: `${customer.firstName} ${customer.lastName}`,
      status: customer.status,
      kycVerified: customer.status === 'ACTIVE',
      mobileEnding: mobile ? mobile.slice(-3) : null,
      accountsAvailable: accounts.available,
      accounts: accounts.list.map((a) => ({ bsb: a.bsb, accountNumber: a.accountNumber, product: a.product, available: a.available })),
      digitalGold,
      holding,
    };
  }

  /** Sends a recorded (or sent, unanswered) Form VII to the Partner Bank; the Partner Products API holds the keys and records the answer. */
  async sendFormVii(token: string, applicationNumber: string) {
    return this.api.postViaPartnerBank(token, PP_WRITE,
      `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-account-applications/${encodeURIComponent(applicationNumber)}/send`, 'The Partner Bank');
  }

  // ┌─ ARCH + SEC ─────────────────────────────────────────────────────────────────────────
  // │ "Send and pay" for a recorded slip, each step safe to repeat ("Continue" runs it all
  // │ again and skips what is done): (1) the Partner Products API sends the signed purchase
  // │ instruction -> ORDERED; (2) the payment is started AT EWP PAYMENTS AS THE STAFF MEMBER
  // │ (their payment.initiate and branch; the slip's fixed Idempotency-Key; payee, amount and
  // │ reference from the Partner Products API, never from the screen) - the normal payment
  // │ saga, approval tier and four eyes apply; (3) the Temporal workflow takes over the wait.
  // └──────────────────────────────────────────────────────────────────────────────────────
  async buy(token: string, purchaseNumber: string) {
    const base = `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-purchases/${encodeURIComponent(purchaseNumber)}`;
    let purchase = await this.api.postViaPartnerBank<PurchaseView>(token, PP_WRITE, `${base}/send`, 'The Partner Bank');

    if (purchase.status === 'ORDERED' && !purchase.paymentNumber) {
      const payment = await this.api.get<PurchasePayment>(token, PP_WRITE, `${base}/payment-instruction`, 'Partner Products');
      let started: { paymentNumber: string };
      try {
        started = await this.api.post<{ paymentNumber: string }>(token, PAYMENTS_WRITE, `${this.options.paymentsApiBaseUrl}/v1/payments`, {
          customerNumber: payment.customerNumber,
          fromBsb: payment.fromBsb,
          fromAccountNumber: payment.fromAccountNumber,
          payeeName: payment.payeeName,
          toBsb: payment.toBsb,
          toAccountNumber: payment.toAccountNumber,
          amount: payment.amount,
          reference: payment.reference,
        }, 'Payments', { 'Idempotency-Key': payment.idempotencyKey });
      } catch (error) {
        if (error instanceof ForbiddenException) throw new ForbiddenException('The Partner Bank placed the order, but you may not start payments: a colleague who may continues it.');
        throw error;
      }
      purchase = await this.api.post<PurchaseView>(token, PP_WRITE, `${base}/payment`, { paymentNumber: started.paymentNumber }, 'Partner Products');
    }

    const followUp = purchase.status === 'ORDERED' || purchase.status === 'PAID' ? await this.workflow.ensureStarted(purchaseNumber) : null;
    return { purchase, followUp };
  }

  /** A one-time address of the Partner Bank's own read-only page for the account (the Partner Products API pushes the signed request). */
  async partnerView(token: string, applicationNumber: string) {
    return this.api.postViaPartnerBank<{ url: string; expiresInSeconds: number }>(token, PP_READ,
      `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-account-applications/${encodeURIComponent(applicationNumber)}/partner-view`, 'The Partner Bank');
  }

  async applications(token: string) {
    return this.api.get<unknown[]>(token, PP_READ, `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-account-applications`, 'Partner Products');
  }

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ Form VII: the customer's facts are READ AGAIN here from Customer Onboarding (for the
  // │ staff member, within their branch) - never taken from the screen. Cheap checks first,
  // │ then the scan goes to Documents Management, then the Domain API records it with DM's
  // │ SHA-256. If the Domain API refuses, the scan just stored is removed again.
  // └──────────────────────────────────────────────────────────────────────────────────────
  async recordFormVii(token: string, customerId: number, fields: FormViiFields, scan: UploadedScan) {
    const customer = await this.customerDetails(token, customerId);
    if (customer.status !== 'ACTIVE') {
      throw new BadRequestException('Only a customer whose onboarding (KYC) is complete can apply - the Partner Bank relies on EWP\'s identification.');
    }
    if (!australianMobile(customer.phoneNumber)) {
      throw new BadRequestException('The customer\'s phone number in EWP\'s records is not an Australian mobile: correct the customer\'s record first.');
    }
    const existing = await this.digitalGoldOf(token, customer.customerNumber);
    if (existing.application && existing.application.status !== 'REFUSED') {
      throw new ConflictException('The customer already has a Digital Gold application or account: a second Form VII is not needed.');
    }

    const stored = await this.api.upload(token, `${this.options.documentsApiBaseUrl}/v1/documents`, scan, 'PARTNER_FORM_VII', customer.customerNumber);
    try {
      return await this.api.post(token, PP_WRITE, `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-account-applications`, {
        customerNumber: customer.customerNumber,
        customerName: `${customer.firstName} ${customer.lastName}`,
        kycVerified: customer.status === 'ACTIVE',
        phoneOnRecord: customer.phoneNumber,
        dateOfBirth: fields.dateOfBirth,
        identityChecked: fields.identityChecked,
        formNumber: fields.formNumber,
        formSignedOn: fields.formSignedOn,
        signatureChecked: fields.signatureChecked,
        formDocumentId: stored.documentId,
        formScanSha256: stored.contentHash,
      }, 'Partner Products');
    } catch (error) {
      await this.api.removeDocument(token, `${this.options.documentsApiBaseUrl}/v1/documents/${encodeURIComponent(stored.documentId)}`);
      throw error;
    }
  }

  // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
  // │ A transfer slip: the paying account must be one of THIS customer's accounts (found
  // │ for the staff member by the Accounts API) with enough available - the payment saga
  // │ re-checks under the account's lock later; the phone number on the slip must be the
  // │ customer's mobile on record (the Domain API and the Partner Bank check it again).
  // └──────────────────────────────────────────────────────────────────────────────────────
  async recordTransferSlip(token: string, customerId: number, fields: TransferSlipFields, scan: UploadedScan) {
    const customer = await this.customerDetails(token, customerId);
    const existing = await this.digitalGoldOf(token, customer.customerNumber);
    if (!existing.application || existing.application.status !== 'OPENED') {
      throw new ConflictException('The customer\'s Digital Gold account is not open yet: a transfer slip needs an open account.');
    }
    if (australianMobile(fields.mobileOnSlip) !== australianMobile(customer.phoneNumber)) {
      throw new BadRequestException('The phone number on the slip is not the customer\'s mobile on record.');
    }

    let accounts: PaymentAccount[];
    try {
      accounts = await this.accountsOf(token, customer.customerNumber);
    } catch (error) {
      if (error instanceof ForbiddenException) throw new ForbiddenException('You are not allowed to choose a paying account for this customer.');
      throw error;
    }
    const debtor = accounts.find((a) => a.accountNumber === fields.debtorAccountNumber);
    if (!debtor) throw new BadRequestException('The paying account is not one of the customer\'s active accounts.');
    if (debtor.available < fields.amount) throw new BadRequestException('The paying account does not have enough available for this amount.');

    const stored = await this.api.upload(token, `${this.options.documentsApiBaseUrl}/v1/documents`, scan, 'PARTNER_TRANSFER_SLIP', customer.customerNumber);
    try {
      return await this.api.post(token, PP_WRITE, `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/gold-purchases`, {
        customerNumber: customer.customerNumber,
        amount: fields.amount,
        debtorBsb: debtor.bsb,
        debtorAccountNumber: debtor.accountNumber,
        slipNumber: fields.slipNumber,
        slipSignedOn: fields.slipSignedOn,
        mobileOnSlip: fields.mobileOnSlip,
        signatureChecked: fields.signatureChecked,
        slipDocumentId: stored.documentId,
        slipScanSha256: stored.contentHash,
      }, 'Partner Products');
    } catch (error) {
      await this.api.removeDocument(token, `${this.options.documentsApiBaseUrl}/v1/documents/${encodeURIComponent(stored.documentId)}`);
      throw error;
    }
  }

  private customerDetails(token: string, customerId: number): Promise<CustomerDetails> {
    return this.api.get<CustomerDetails>(token, CO_READ, `${this.options.customerOnboardingApiBaseUrl}/v1/customers/${customerId}`, 'Customer Onboarding');
  }

  /** The customer's Form VII and slips from the Partner Products API (the staff member's branch only). */
  digitalGoldOf(token: string, customerNumber: string): Promise<CustomerDigitalGold> {
    return this.api.get<CustomerDigitalGold>(token, PP_READ,
      `${this.options.partnerProductsApiBaseUrl}/v1/partner-products/customers/${encodeURIComponent(customerNumber)}/digital-gold`, 'Partner Products');
  }

  /** The customer's ACTIVE accounts in the staff member's branch (exact customer number only). */
  private async accountsOf(token: string, customerNumber: string): Promise<PaymentAccount[]> {
    const params = new URLSearchParams({ search: customerNumber });
    const list = await this.api.get<PaymentAccount[]>(token, ACCOUNTS_READ, `${this.options.accountsApiBaseUrl}/v1/accounts/for-payment?${params}`, 'Accounts');
    return list.filter((a) => a.customerNumber === customerNumber);
  }
}

/** +614 and 8 digits, or null - the same rule as the Domain API's DigitalGoldRules.AustralianMobile. */
export function australianMobile(phone: string | null | undefined): string | null {
  if (!phone) return null;
  let digits = phone.replace(/\D/g, '');
  if (digits.startsWith('04') && digits.length === 10) digits = `61${digits.slice(1)}`;
  return digits.startsWith('614') && digits.length === 11 ? `+${digits}` : null;
}