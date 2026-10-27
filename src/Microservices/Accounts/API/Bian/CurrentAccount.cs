using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.Accounts.Api.Bian;

// ┌─ :: ARCHITECTURE :: BIAN "Current Account" - the contract, not the model ──────────────────────
// │ Why : To show that the Accounts API can speak BIAN's standard service-domain contract (release
// │       14.0.0, service domain CurrentAccount) next to its own /v1 - without letting BIAN's very
// │       general shapes into the domain. EWP's own callers stay on /v1.
// │ How : These records carry BIAN's property names and nesting EXACTLY (checked against BIAN's
// │       published OpenAPI file in the tests), but only the properties EWP can fill: an account
// │       with its owner, BSB, balances and status, and a cash deposit at the counter. Which BIAN
// │       properties EWP requires is decided in CurrentAccountMapper, not here.
// │       Anti-corruption layer: the mapper translates to the existing queries and commands; the
// │       domain and the tables do not know BIAN exists. Payments has its own copy of these few
// │       BIAN shapes on purpose - each bounded context owns its published contract.
// │ Ref : doc/bian/README.md | BIAN semantic APIs release 14.0.0 (CurrentAccount.yaml) |
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

/// <summary>BIAN "CurrentAccountFacility" (response of Retrieve).</summary>
public sealed record CurrentAccountFacility
{
    public BianCurrentAccountAgreement? ProductInstanceReference { get; init; }
    public BianAccountIdentification? CurrentAccountNumber { get; init; }
    public BianBranch? BankBranchLocationReference { get; init; }
    public BianAccountCurrency? AccountCurrency { get; init; }
    public string? AssociationType { get; init; }
    public BianParty? AssociationReference { get; init; }
    public BianAccount? AccountDetails { get; init; }
    public string? AccountDateType { get; init; }
    public BianDatetime? AccountDate { get; init; }
    public BianAccountBalance? AccountBalance { get; init; }
    public string? AccountStatus { get; init; }
}

public sealed record BianCurrentAccountAgreement
{
    public BianIdentifier? ProductAgreementIdentification { get; init; }
    public string? AgreementDescription { get; init; }
}

public sealed record BianAccountIdentification
{
    public string? AccountIdentificationType { get; init; }
    public BianIdentifier? AccountIdentification { get; init; }
}

public sealed record BianBranch
{
    public BianIdentifier? BranchIdentification { get; init; }
}

public sealed record BianAccountCurrency
{
    public BianCurrencyCode? AccountCurrency { get; init; }
}

public sealed record BianParty
{
    public BianName? PartyName { get; init; }
    public BianPartyIdentification? PartyIdentification { get; init; }
}

public sealed record BianPartyIdentification
{
    public BianIdentifier? PartyIdentification { get; init; }
}

public sealed record BianName
{
    public string? Name { get; init; }
}

/// <summary>BIAN "Account" (the facility's AccountDetails, and a deposit's target account).</summary>
public sealed record BianAccount
{
    public BianAccountStatus? AccountStatus { get; init; }
    public BianAccountIdentification? AccountIdentification { get; init; }
    public BianAccountBalance? AccountBalance { get; init; }
}

public sealed record BianAccountStatus
{
    public BianStatus? AccountStatus { get; init; }
    public string? AccountStatusType { get; init; }
}

public sealed record BianAccountBalance
{
    public BianAmount? BalanceAmount { get; init; }
    public string? BalanceType { get; init; }
}

public sealed record BianStatus
{
    public string? StatusReason { get; init; }
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

public sealed record BianDate
{
    public string? DateContent { get; init; }
}

/// <summary>BIAN "Deposit" behaviour qualifier (request and response of Deposit/Initiate).</summary>
public sealed record Deposit
{
    public string? DepositType { get; init; }
    public BianDepositTransaction? DepositTransaction { get; init; }
    public string? DepositTransactionSourceReference { get; init; }
    public BianAmount? DepositTransactionAmount { get; init; }
    public BianDate? DepositTransactionDate { get; init; }
}

public sealed record BianDepositTransaction
{
    public BianAmount? DepositAmount { get; init; }
    public BianIdentifier? TransactionIdentification { get; init; }
    public BianAccount? FinancialTransactionTargetAccount { get; init; }
}