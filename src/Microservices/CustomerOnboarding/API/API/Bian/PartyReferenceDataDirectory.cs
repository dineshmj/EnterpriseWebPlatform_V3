using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Bian;

// ┌─ :: ARCHITECTURE :: BIAN "Party Reference Data Directory" - the contract, not the model ───────
// │ Why : To show that the Customer Onboarding API can speak BIAN's standard service-domain
// │       contract (release 14.0.0, service domain PartyReferenceDataDirectory) next to its own
// │       /v1 - without letting BIAN's very general shapes into the domain. EWP's callers stay on /v1.
// │ How : These records carry BIAN's property names and nesting EXACTLY (checked against BIAN's
// │       published OpenAPI file in the tests), but only the properties EWP can fill: the
// │       directory entry (who the party is) and its "Reference" (legal name, e-mail, phone,
// │       residential address). Which properties EWP accepts is decided in the mapper, not here.
// │       Anti-corruption layer: the mapper translates to the existing query and command; the
// │       domain and the tables do not know BIAN exists. Payments and Accounts have their own
// │       copies of the few shared BIAN shapes on purpose - each bounded context owns its contract.
// │ Ref : doc/bian/README.md | BIAN semantic APIs release 14.0.0 (PartyReferenceDataDirectory.yaml)
// │       | Evans, DDD - Anti-Corruption Layer
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

/// <summary>BIAN "PartyReferenceDataDirectoryEntry" (response of Retrieve).</summary>
public sealed record PartyReferenceDataDirectoryEntry
{
    public BianParty? PartyReference { get; init; }
    public string? DirectoryEntryDateType { get; init; }
    public BianDatetime? DirectoryEntryDate { get; init; }
    public string? PartyType { get; init; }
}

/// <summary>
/// BIAN "Reference" behaviour qualifier (response of RetrieveReference; request and response of
/// UpdateReference). SEC: a property BIAN defines but EWP does not keep (nationality, date of
/// birth, political exposure, ...) is refused, not silently dropped - the caller would otherwise
/// believe it was stored.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Reference
{
    public BianName? PartyFullLegalName { get; init; }
    public string? PartyLegalStructure { get; init; }
    public BianPostalAddress? ResidentialAddress { get; init; }
    public BianContactPoint? eMailAddress { get; init; }
    public BianContactPoint? CellPhoneNumber { get; init; }
    public BianDatetime? CustomerSinceDate { get; init; }
}

public sealed record BianParty
{
    public BianName? PartyName { get; init; }
    public string? PartyType { get; init; }
    public BianPartyIdentification? PartyIdentification { get; init; }
    public string? PartyLegalStructureType { get; init; }
}

public sealed record BianPartyIdentification
{
    public BianIdentifier? PartyIdentification { get; init; }
}

public sealed record BianPostalAddress
{
    public string? AddressType { get; init; }
    public string? AddressLine { get; init; }
    public BianIdentifier? PostCode { get; init; }
    public BianName? TownName { get; init; }
    public BianCountry? Country { get; init; }
    public string? AddressDescription { get; init; }
}

public sealed record BianCountry
{
    public BianCountryCode? CountryCode { get; init; }
}

public sealed record BianCountryCode
{
    public string? Countrycode { get; init; }
}

/// <summary>BIAN "ContactPoint": the kind of contact, and the address itself in the location.</summary>
public sealed record BianContactPoint
{
    public string? ContactPointType { get; init; }
    public BianLocation? LocationReference { get; init; }
}

public sealed record BianLocation
{
    public string? LocationAddress { get; init; }
}

public sealed record BianName
{
    public string? Name { get; init; }
}

public sealed record BianIdentifier
{
    public string? IdentifierValue { get; init; }
}

public sealed record BianDatetime
{
    public string? DateTimeContent { get; init; }
}