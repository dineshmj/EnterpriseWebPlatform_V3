import { randomUUID } from 'node:crypto';
import type { APIRequestContext } from '@playwright/test';
import { expect, test } from '../../../support/fixtures';
import { fundedCustomer } from '../../../support/journeys/payments';
import { resetAllSimulators } from '../../../support/simulators';
import { urls } from '../../../support/services';
import { accountsWebSignInToken, paymentsWebSignInToken } from '../../../support/tokens';

/**
 * ┌─ :: ARCHITECTURE :: The Accounts API speaks BIAN - shown live, next to its own /v1 ───────────
 * │ Why : /v2 claims BIAN's CurrentAccount contract (release 14.0.0). The unit tests check every
 * │       body against BIAN's published file; this shows the running API doing it - and that /v2
 * │       is a second contract on the SAME business path, not a copy.
 * │ How : Jack (account officer) retrieves a customer's account through BIAN and /v1 shows the
 * │       same; Sophie (the counter) deposits cash through BIAN's Deposit/Initiate; the same
 * │       Idempotency-Key through /v2 and through /v1 finds that deposit (one path, one credit);
 * │       Jack may not deposit (the counter's client only: 403); the balance moved by the
 * │       deposit, once. Also the demo script for doc/bian/README.md.
 * │ Ref : doc/bian/README.md | Accounts API README (/v2)
 * └────────────────────────────────────────────────────────────────────────────────────────────────
 */
interface V1Account { accountId: number; accountNumber: string; bsb: string; customerNumber: string; holderName: string; balance: number; available: number }
interface Balance { BalanceAmount: { AmountValue: string }; BalanceType: string }
interface Facility {
  CurrentAccountNumber: { AccountIdentification: { IdentifierValue: string } };
  BankBranchLocationReference: { BranchIdentification: { IdentifierValue: string } };
  AssociationReference: { PartyName: { Name: string }; PartyIdentification: { PartyIdentification: { IdentifierValue: string } } };
  AccountBalance: Balance;
  AccountDetails: { AccountBalance: Balance };
  AccountStatus: string;
}
interface DepositAnswer { DepositTransaction: { TransactionIdentification: { IdentifierValue: string }; FinancialTransactionTargetAccount: { AccountBalance: Balance } } }

const v2 = `${urls.accountsApi}/v2/CurrentAccount`;
const bearer = (token: string) => ({ Authorization: `Bearer ${token}` });

async function retrieve(http: APIRequestContext, token: string, accountId: number): Promise<Facility> {
  const response = await http.get(`${v2}/${accountId}/Retrieve`, { headers: bearer(token) });
  expect(response.status(), await response.text()).toBe(200);
  return await response.json() as Facility;
}

test.describe('BIAN - Current Account (/v2)', () => {
  test.describe.configure({ timeout: 6 * 60_000 });
  test.afterAll(async () => { await resetAllSimulators(); });

  test('ShouldShowAndCreditAnAccountThroughBiansContract_OnTheSameBusinessPathAsV1', async ({ signedInAs, request }) => {
    // Arrange: a funded customer; Jack's (account officer) and Sophie's (counter) tokens; the account.
    const customer = await fundedCustomer(signedInAs, 'BIAN current account');
    const jackToken = await accountsWebSignInToken(await signedInAs('jack.accounts'), request);
    const sophieToken = await paymentsWebSignInToken(await signedInAs('sophie.cs'), request);
    const list = await request.get(`${urls.accountsApi}/v1/accounts?pageSize=100`, { headers: bearer(jackToken) });
    expect(list.status()).toBe(200);
    const account = (await list.json() as { items: V1Account[] }).items.find(a => a.customerNumber === customer);
    expect(account, `${customer}'s account in Jack's branch`).toBeTruthy();

    // Act + Assert 1: BIAN Retrieve shows what /v1 shows - number, BSB, owner, both balances.
    const before = await retrieve(request, jackToken, account!.accountId);
    expect(before.AccountStatus).toBe('Enabled');
    expect(before.CurrentAccountNumber.AccountIdentification.IdentifierValue).toBe(account!.accountNumber);
    expect(before.BankBranchLocationReference.BranchIdentification.IdentifierValue).toBe(account!.bsb);
    expect(before.AssociationReference.PartyIdentification.PartyIdentification.IdentifierValue).toBe(customer);
    expect(before.AssociationReference.PartyName.Name).toBe(account!.holderName);
    expect(before.AccountBalance).toMatchObject({ BalanceType: 'CurrentBalance', BalanceAmount: { AmountValue: account!.balance.toFixed(2) } });
    expect(before.AccountDetails.AccountBalance).toMatchObject({ BalanceType: 'AvailableBalance', BalanceAmount: { AmountValue: account!.available.toFixed(2) } });

    // Act 2: Sophie deposits AUD 250 in cash through BIAN's Deposit/Initiate.
    const key = randomUUID();
    const deposit = {
      DepositType: 'CashDeposit',
      DepositTransactionSourceReference: customer,
      DepositTransactionAmount: { AmountValue: '250.00', AmountCurrency: { Currencycode: 'AUD' } },
    };
    const recorded = await request.post(`${v2}/${account!.accountId}/Deposit/Initiate`, { data: deposit, headers: { ...bearer(sophieToken), 'Idempotency-Key': key } });
    expect(recorded.status(), await recorded.text()).toBe(201);
    const first = await recorded.json() as DepositAnswer;
    const depositNumber = first.DepositTransaction.TransactionIdentification.IdentifierValue;
    const after = (account!.balance + 250).toFixed(2);
    expect(first.DepositTransaction.FinancialTransactionTargetAccount.AccountBalance.BalanceAmount.AmountValue).toBe(after);

    // Assert 2: the same key - through /v2 and through /v1 - finds THIS deposit; nothing is credited twice.
    const again = await request.post(`${v2}/${account!.accountId}/Deposit/Initiate`, { data: deposit, headers: { ...bearer(sophieToken), 'Idempotency-Key': key } });
    expect(again.status(), await again.text()).toBe(200);
    expect((await again.json() as DepositAnswer).DepositTransaction.TransactionIdentification.IdentifierValue).toBe(depositNumber);
    const viaV1 = await request.post(`${urls.accountsApi}/v1/accounts/cash-deposits`, {
      data: { customerNumber: customer, bsb: account!.bsb, accountNumber: account!.accountNumber, amount: 250 },
      headers: { ...bearer(sophieToken), 'Idempotency-Key': key },
    });
    expect(viaV1.status(), await viaV1.text()).toBe(200);
    expect((await viaV1.json() as { depositNumber: string }).depositNumber).toBe(depositNumber);

    // Assert 3: BIAN's door keeps /v1's rule - only the counter (the Payments BFF's client) credits cash.
    const byJack = await request.post(`${v2}/${account!.accountId}/Deposit/Initiate`, { data: deposit, headers: { ...bearer(jackToken), 'Idempotency-Key': randomUUID() } });
    expect(byJack.status(), 'an account officer recording cash through /v2').toBe(403);

    // Assert 4: the balance moved by the deposit, once.
    expect((await retrieve(request, jackToken, account!.accountId)).AccountBalance.BalanceAmount.AmountValue).toBe(after);
  });
});