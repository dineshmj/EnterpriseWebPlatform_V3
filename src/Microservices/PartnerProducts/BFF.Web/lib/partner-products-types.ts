/** What a server action returns to the page: data, or a message the page can show as it is. */
export type ActionResult<T> =
  | { ok: true; data: T }
  | { ok: false; error: string; signedOut?: boolean };

export interface CustomerMatch {
  customerId: number;
  customerNumber: string;
  name: string;
  status: string;
}

export interface PayingAccount {
  bsb: string;
  accountNumber: string;
  product: string;
  available: number;
}

export interface GoldApplication {
  number: string;
  customerNumber: string;
  customerName: string;
  mobileEnding: string;
  formNumber: string;
  formSignedOn: string;
  formScanSha256: string;
  status: 'RECORDED' | 'SENT' | 'OPENED' | 'REFUSED';
  partnerAccountNumber: string | null;
  statusReason: string | null;
  recordedByLanId: string;
  recordedAt: string;
}

export interface GoldPurchase {
  number: string;
  customerNumber: string;
  partnerAccountNumber: string;
  amountAud: number;
  debtorBsb: string;
  debtorAccountNumber: string;
  slipNumber: string;
  slipSignedOn: string;
  status: 'RECORDED' | 'SENT' | 'ORDERED' | 'PAID' | 'ALLOCATED' | 'REFUSED' | 'CANCELLED';
  partnerOrderNumber: string | null;
  statusReason: string | null;
  recordedByLanId: string;
  recordedAt: string;
  pricePerGram: number | null;
  grams: number | null;
  quoteExpiresAt: string | null;
  paymentNumber: string | null;
  networkReference: string | null;
  allocatedAt: string | null;
}

/** "Send & pay": the purchase as it now stands, and whether the follow-up workflow runs (null when nothing is left to follow). */
export interface PurchaseProgress {
  purchase: GoldPurchase;
  followUp: 'STARTED' | 'ALREADY_RUNNING' | 'NOT_STARTED' | null;
}

/** The customer's gold as the Partner Bank keeps it (its system of record), read on every look. */
export interface PartnerHolding {
  number: string;
  holderName: string;
  holding: {
    grams: number;
    pricePerGram: number;
    valueAud: number;
    pricedAt: string;
    activity: { at: string; text: string; reference: string | null }[];
  };
}

/** The customer at the counter: EWP's record, their paying accounts and their Digital Gold. */
export interface CustomerAtCounter {
  customerId: number;
  customerNumber: string;
  name: string;
  status: string;
  kycVerified: boolean;
  /** Last three digits of the customer's mobile on record (null: not an Australian mobile). */
  mobileEnding: string | null;
  accountsAvailable: boolean;
  accounts: PayingAccount[];
  digitalGold: { customerNumber: string; application: GoldApplication | null; purchases: GoldPurchase[] };
  /** Null until the account is open, or when the Partner Bank cannot be reached. */
  holding: PartnerHolding | null;
}