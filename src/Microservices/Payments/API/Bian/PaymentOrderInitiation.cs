using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.Payments.Api.Bian;

// ┌─ :: ARCHITECTURE :: BIAN "Payment Order Initiation" - the contract, not the model ─────────────
// │ Why : To show that the Payments API can speak BIAN's standard service-domain contract (release
// │       14.0.0, service domain PaymentOrderInitiation) next to its own /v1 - without letting
// │       BIAN's very general shapes into the domain. EWP's own callers stay on /v1.
// │ How : These records carry BIAN's property names and nesting EXACTLY (they are checked against
// │       BIAN's published OpenAPI file in the tests), but only the properties EWP can fill: a
// │       domestic BSB transfer from a customer's account. BIAN marks every property optional;
// │       which ones EWP requires is decided in PaymentOrderInitiationMapper, not here.
// │       Anti-corruption layer: the mapper translates to the existing commands and queries; the
// │       domain, the saga and the tables do not know BIAN exists.
// │ Ref : doc/bian/README.md | BIAN semantic APIs release 14.0.0 (PaymentOrderInitiation.yaml) |
// │       Evans, DDD - Anti-Corruption Layer
// └────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>BIAN's JSON: its PascalCase property names as they are, and no empty properties.</summary>
public static class BianJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>BIAN "PaymentOrderInitiationTransaction" (request of Initiate, response of Initiate and Retrieve).</summary>
public sealed record PaymentOrderInitiationTransaction
{
    public string? PaymentTransactionType { get; init; }
    public BianPaymentTransaction? PaymentTransaction { get; init; }
    public BianPayer? PayerReference { get; init; }
    public BianPayerBank? PayerBankReference { get; init; }
    public BianProductAgreement? PayerProductInstanceReference { get; init; }
    public BianPayee? PayeeReference { get; init; }
    public BianPayeeBank? PayeeBankReference { get; init; }
    public BianProductAgreement? PayeeProductInstanceReference { get; init; }
    public BianAmount? Amount { get; init; }
    public BianCurrencyCode? Currency { get; init; }
    public string? DateType { get; init; }
    public BianDatetime? Date { get; init; }
    public string? PaymentMechanism { get; init; }
    public string? PaymentPurpose { get; init; }
}

public sealed record BianPaymentTransaction
{
    public BianPaymentIdentification? PaymentTransactionIdentification { get; init; }
    public BianPaymentTransactionStatus? PaymentTransactionStatus { get; init; }
    public BianAmount? PaymentTransactionAmount { get; init; }
}

public sealed record BianPaymentIdentification
{
    public BianIdentifier? PaymentIdentification { get; init; }
    public string? PaymentIdentificationType { get; init; }
}

public sealed record BianPaymentTransactionStatus
{
    public BianStatus? PaymentTransactionStatus { get; init; }
    public string? PaymentTransactionStatusType { get; init; }
}

public sealed record BianPayer
{
    public string? PayerIdentifier { get; init; }
    public string? PaymentInstructionInvolvementType { get; init; }
}

public sealed record BianPayerBank
{
    public string? PayerBankIdentifier { get; init; }
    public string? PaymentInstructionInvolvementType { get; init; }
}

public sealed record BianPayee
{
    public string? PayeeIdentifier { get; init; }
    public string? PaymentInstructionInvolvementType { get; init; }
}

public sealed record BianPayeeBank
{
    public string? PayeeBankIdentifier { get; init; }
    public string? PaymentInstructionInvolvementType { get; init; }
}

public sealed record BianProductAgreement
{
    public BianIdentifier? ProductAgreementIdentification { get; init; }
}

public sealed record BianIdentifier
{
    public string? IdentifierValue { get; init; }
}

public sealed record BianAmount
{
    public string? AmountValue { get; init; }
    public BianCurrencyCode? AmountCurrency { get; init; }
}

public sealed record BianCurrencyCode
{
    public string? Currencycode { get; init; }
}

public sealed record BianDatetime
{
    public string? DateTimeContent { get; init; }
}

public sealed record BianStatus
{
    public string? StatusReason { get; init; }
    public BianDatetime? StatusDateTime { get; init; }
}

public sealed record BianTask
{
    public string? Task { get; init; }
}

/// <summary>BIAN "OrderInitiation" behaviour qualifier (request and response of its Exchange): a decision on the order.</summary>
public sealed record OrderInitiation
{
    public BianIdentifier? PaymentOrderProcedureInstanceReference { get; init; }
    public BianStatus? PaymentOrderProcedureInstanceStatus { get; init; }
    public BianTask? OrderInitiationTaskResult { get; init; }
}