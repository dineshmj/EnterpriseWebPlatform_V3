import { request as playwrightRequest, test } from '@playwright/test';
import { DigitalGoldMfe } from '../pages/mfes';
import type { ShellPage } from '../pages/ShellPage';
import type { PersonaName } from '../personas';
import { fundedCustomer } from './payments';

/**
 * The steps of Digital Gold at the counter (EWP as the Partner Bank's distributor), each as
 * Sophie does them. The Partner Bank is a separate organisation with its own deployment (the PC
 * on the home network, or the laptop): EWP reaches it over mutual TLS.
 */
type SignIn = (username: PersonaName) => Promise<ShellPage>;

/** The Partner Bank's web site (its public port: no client certificate needed to see that it answers). */
export const partnerBankWeb = 'https://partner-products.ewp.home.arpa:47402';

/** The mobile every generated customer has on record (data.ts) - a transfer slip must carry it. */
export const mobileOnRecord = '+61 412 345 678';

// ┌─ :: ARCHITECTURE :: Skip, do not fail, without the Partner Bank ──────────────────────────────
// │ Why : The Partner Bank is another organisation's system (Start-PartnerBank.ps1 -Lan on the
// │       PC); when it is not running, the Digital Gold journeys cannot say anything about EWP.
// │ How : One look at its public web port (5 s) before the file's tests; unreachable -> every
// │       test of the file is skipped with the reason, the rest of the suite runs on.
// │ Ref : tst/README.md "Journeys, resilience and security - what they need" | Smoke ST-12
// └──────────────────────────────────────────────────────────────────────────────────────────────
export async function partnerBankReachable(): Promise<boolean> {
  const api = await playwrightRequest.newContext({ ignoreHTTPSErrors: true });
  try {
    const response = await api.get(`${partnerBankWeb}/`, { timeout: 5_000, maxRedirects: 0 });
    return response.status() < 500;
  } catch {
    return false;
  } finally {
    await api.dispose();
  }
}

/** A number as printed on a paper form: a prefix and the run's time, unique per run. */
export function formNumber(prefix: 'F7' | 'TS'): string {
  return `${prefix}-${Date.now().toString(36).toUpperCase()}`;
}

export interface GoldAccount {
  customerNumber: string;
  partnerAccountNumber: string;
}

const goldAccounts = new Map<string, Promise<GoldAccount>>();

// ┌─ :: ARCHITECTURE :: A new customer per run ───────────────────────────────────────────────────
// │ Why : The Partner Bank opens ONE Digital Gold account per customer and keeps it after an
// │       EWP database re-create; a fixed demo customer would be refused on the second run.
// │ How : The customer is onboarded end to end (then AUD 5,000 deposited in cash) once per spec file;
// │       Form VII is recorded and sent once; the file's tests reuse the open account.
// │ Ref : support/journeys/payments.ts fundedCustomer | PartnerProducts-Requirements.md
// └──────────────────────────────────────────────────────────────────────────────────────────────
export function goldAccount(signedInAs: SignIn, key: string): Promise<GoldAccount> {
  let account = goldAccounts.get(key);
  if (!account) {
    account = (async () => {
      const customerNumber = await fundedCustomer(signedInAs, key);
      return test.step(`Sophie records Form VII for ${customerNumber} and sends it: the Partner Bank opens the account`, async () => {
        const digitalGold = new DigitalGoldMfe(await signedInAs('sophie.cs'));
        await digitalGold.open();
        await digitalGold.selectCustomer(customerNumber);
        await digitalGold.recordFormVii(formNumber('F7'), '1990-05-17');
        const partnerAccountNumber = await digitalGold.sendFormVii();
        return { customerNumber, partnerAccountNumber };
      });
    })();
    // A failed opening is not cached: the next test tries again.
    account.catch(() => goldAccounts.delete(key));
    goldAccounts.set(key, account);
  }
  return account;
}

/** Sophie records a transfer slip for `amount` and presses "Send & pay"; returns the slip number. */
export async function buyGold(sophie: ShellPage, customerNumber: string, amount: string): Promise<{ digitalGold: DigitalGoldMfe; slipNumber: string }> {
  return test.step(`Sophie buys AUD ${amount} of gold for ${customerNumber} (transfer slip, Send & pay)`, async () => {
    const digitalGold = new DigitalGoldMfe(sophie);
    await digitalGold.open();
    await digitalGold.selectCustomer(customerNumber);
    const slipNumber = formNumber('TS');
    await digitalGold.recordTransferSlip(slipNumber, amount, mobileOnRecord);
    await digitalGold.sendAndPay(slipNumber);
    return { digitalGold, slipNumber };
  });
}