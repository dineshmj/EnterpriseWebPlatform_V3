using EnterpriseWebPlatform.Accounts.Api.Bian;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Support;

/// <summary>BIAN CurrentAccount bodies as a partner would send them - shared by the /v2 fixtures.</summary>
internal static class BianExamples
{
    public static Deposit CashDeposit() => new()
    {
        DepositType = "CashDeposit",
        DepositTransactionSourceReference = "CUS-000123",
        DepositTransactionAmount = new BianAmount { AmountValue = "250.00", AmountCurrency = new BianCurrencyCode { Currencycode = "AUD" } }
    };
}