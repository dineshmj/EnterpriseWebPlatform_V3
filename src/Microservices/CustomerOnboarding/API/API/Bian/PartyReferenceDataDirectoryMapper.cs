using System.Globalization;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Bian;

/// <summary>The contact details a BIAN UpdateReference leaves the customer with (what was not sent stays as it was).</summary>
public sealed record BianContactChange(string Email, string PhoneNumber);

/// <summary>
/// Translates between BIAN's PartyReferenceDataDirectory contract and EWP's customer. Pure: no
/// I/O, so every mapping rule is unit-tested (tst/dotnet/CustomerOnboarding.Api.Tests/Bian).
/// </summary>
public static class PartyReferenceDataDirectoryMapper
{
    /// <summary>EWP keeps one reference record per customer; BIAN addresses it by an id in the path.</summary>
    public const string ReferenceId = "primary";

    private const string ElectronicAddress = "ElectronicAddress";
    private const string PhoneNumber = "PhoneNumber";

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ Where EWP's customer sits in BIAN's directory entry (doc/bian/README.md, "Customers"):
    // │   the name -> PartyName (BIAN has ONE name string: "first last"); Individual / Business
    // │   -> PartyType Person / Organisation; the customer number -> the party's identification;
    // │   registered at -> DirectoryEntryDateType "OpenDate". EWP's lifecycle status (PROSPECT ...
    // │   CLOSED) has no place here - in BIAN it belongs to PartyLifecycleManagement.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static PartyReferenceDataDirectoryEntry ToEntry(CustomerDetailsDto customer) => new()
    {
        PartyReference = new BianParty
        {
            PartyName = new BianName { Name = FullName(customer) },
            PartyType = ToPartyType(customer.CustomerType),
            PartyIdentification = new BianPartyIdentification { PartyIdentification = new BianIdentifier { IdentifierValue = customer.CustomerNumber } },
            PartyLegalStructureType = ToLegalStructure(customer.CustomerType)
        },
        DirectoryEntryDateType = "OpenDate",
        DirectoryEntryDate = Since(customer),
        PartyType = ToPartyType(customer.CustomerType)
    };

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ BIAN's Reference: e-mail and phone are ContactPoints (the kind of contact, the address in
    // │ LocationReference.LocationAddress). BIAN names the phone "CellPhoneNumber"; EWP keeps one
    // │ phone number, mobile or not. In the address, BIAN's CountrySubDivision is the KIND of area
    // │ (an enumeration: State, Province, ...), not its name - so the state's name travels in
    // │ AddressDescription; address lines 1 and 2 are BIAN's one free-format AddressLine.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static Reference ToReference(CustomerDetailsDto customer) => new()
    {
        PartyFullLegalName = new BianName { Name = FullName(customer) },
        PartyLegalStructure = ToLegalStructure(customer.CustomerType),
        ResidentialAddress = customer.ResidentialAddress is { } address
            ? new BianPostalAddress
            {
                AddressType = "Residential",
                AddressLine = string.IsNullOrWhiteSpace(address.AddressLine2) ? address.AddressLine1 : $"{address.AddressLine1}, {address.AddressLine2}",
                PostCode = new BianIdentifier { IdentifierValue = address.PostalCode },
                TownName = new BianName { Name = address.City },
                Country = new BianCountry { CountryCode = new BianCountryCode { Countrycode = address.CountryCode } },
                AddressDescription = address.State
            }
            : null,
        eMailAddress = new BianContactPoint { ContactPointType = ElectronicAddress, LocationReference = new BianLocation { LocationAddress = customer.Email } },
        CellPhoneNumber = new BianContactPoint { ContactPointType = PhoneNumber, LocationReference = new BianLocation { LocationAddress = customer.PhoneNumber } },
        CustomerSinceDate = Since(customer)
    };

    /// <summary>
    /// BIAN UpdateReference, as EWP's contact-details change. SEC: only the e-mail and the phone
    /// can change through BIAN. The name, the legal structure, the address and the customer-since
    /// date may be sent back unchanged (read, modify, write) but never changed: BIAN's single name
    /// string cannot be split into first and last name without guessing, and /v1 changes no
    /// address either. A changed one is refused and named - never ignored.
    /// </summary>
    public static (BianContactChange? Change, IReadOnlyList<string> Errors) ToContactChange(Reference? request, CustomerDetailsDto current)
    {
        if (request is null)
            return (null, ["A Reference body is required."]);

        var errors = new List<string>();
        var email = ContactAddress(request.eMailAddress, nameof(Reference.eMailAddress), ElectronicAddress, errors);
        var phone = ContactAddress(request.CellPhoneNumber, nameof(Reference.CellPhoneNumber), PhoneNumber, errors);
        if (request.eMailAddress is null && request.CellPhoneNumber is null)
            errors.Add("eMailAddress or CellPhoneNumber is required: the contact details are what changes through BIAN.");

        var unchanged = ToReference(current);
        if (request.PartyFullLegalName is { } name && name != unchanged.PartyFullLegalName)
            errors.Add("PartyFullLegalName cannot be changed here: BIAN has one name string, EWP keeps a first and a last name. Use PUT /v1/customers/{id}.");
        if (request.PartyLegalStructure is { } structure && structure != unchanged.PartyLegalStructure)
            errors.Add("PartyLegalStructure cannot be changed.");
        if (request.ResidentialAddress is { } address && address != unchanged.ResidentialAddress)
            errors.Add("ResidentialAddress cannot be changed.");
        if (request.CustomerSinceDate is { } since && since != unchanged.CustomerSinceDate)
            errors.Add("CustomerSinceDate cannot be changed.");

        return errors.Count > 0
            ? (null, errors)
            : (new BianContactChange(email ?? current.Email, phone ?? current.PhoneNumber), errors);
    }

    public static string ToPartyType(CustomerType type) => type switch
    {
        CustomerType.Individual => "Person",
        _ => "Organisation"
    };

    // BIAN's legal structures for a business (partnership, private company, ...) are not known to
    // EWP, which only records "Business" - so a business customer has none rather than a guess.
    private static string? ToLegalStructure(CustomerType type) =>
        type == CustomerType.Individual ? "Individual" : null;

    private static string? ContactAddress(BianContactPoint? contact, string property, string type, List<string> errors)
    {
        if (contact is null)
            return null;

        if (contact.ContactPointType is { } sent && !string.Equals(sent, type, StringComparison.Ordinal))
            errors.Add($"{property}.ContactPointType must be '{type}' ('{sent}').");

        var value = contact.LocationReference?.LocationAddress?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            errors.Add($"{property}.LocationReference.LocationAddress is required.");
            return null;
        }

        return value;
    }

    private static string FullName(CustomerDetailsDto customer) => $"{customer.FirstName} {customer.LastName}";

    private static BianDatetime Since(CustomerDetailsDto customer) => new()
    {
        DateTimeContent = customer.CreatedAt.ToString("o", CultureInfo.InvariantCulture)
    };
}