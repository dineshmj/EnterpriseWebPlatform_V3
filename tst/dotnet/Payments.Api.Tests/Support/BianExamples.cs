using EnterpriseWebPlatform.Payments.Api.Bian;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Support;

/// <summary>BIAN PaymentOrderInitiation bodies as a partner would send them - shared by the /v2 fixtures.</summary>
internal static class BianExamples
{
    public static PaymentOrderInitiationTransaction FullRequest() => new()
    {
        PayerReference = new BianPayer { PayerIdentifier = "CUS-000123", PaymentInstructionInvolvementType = "PayerOrDebtor" },
        PayerBankReference = new BianPayerBank { PayerBankIdentifier = "062-000" },
        PayerProductInstanceReference = new BianProductAgreement { ProductAgreementIdentification = new BianIdentifier { IdentifierValue = "72947086" } },
        PayeeReference = new BianPayee { PayeeIdentifier = "Harbour Plumbing", PaymentInstructionInvolvementType = "PayeeOrCreditor" },
        PayeeBankReference = new BianPayeeBank { PayeeBankIdentifier = "083-004" },
        PayeeProductInstanceReference = new BianProductAgreement { ProductAgreementIdentification = new BianIdentifier { IdentifierValue = "12345678" } },
        Amount = new BianAmount { AmountValue = "1100.00", AmountCurrency = new BianCurrencyCode { Currencycode = "AUD" } },
        PaymentPurpose = "Invoice 7781"
    };
}