using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.API.Bian;
using EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Queries.GetCustomer;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.Tests.Bian;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Bian;

// ┌─ :: ARCHITECTURE :: /v2 is BIAN only if BIAN's own file says so ───────────────────────────────
// │ Why : The /v2 contract claims BIAN PartyReferenceDataDirectory (release 14.0.0). A renamed
// │       property, a wrong nesting or a value BIAN does not list would break that claim silently.
// │ How : Every body /v2 sends or accepts is validated against BIAN's published OpenAPI file
// │       (doc/bian, pinned) - the entry of a person and of a business, their reference (with and
// │       without an address), and a contact change as a partner would send it.
// │ Ref : doc/bian/README.md | Customer Onboarding API README (/v2)
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Happy")]
public sealed class PartyReferenceDataDirectory_Contract_Happy_ValidAgainstBian
{
    private const string BianFile = "PartyReferenceDataDirectory.yaml";

    [TestCase(CustomerType.Individual, "Person")]
    [TestCase(CustomerType.Business, "Organisation")]
    public void Given_ACustomerOfEitherType_When_RetrievedThroughV2_Then_TheEntryIsValidAgainstBiansSchema(CustomerType type, string partyType)
    {
        // Given
        var customer = Customer() with { CustomerType = type };

        // When
        var entry = PartyReferenceDataDirectoryMapper.ToEntry(customer);

        // Then
        BianSchema.Validate(BianFile, "PartyReferenceDataDirectoryEntry", entry, BianJson.Options).Should().BeEmpty();
        entry.PartyType.Should().Be(partyType);
        entry.PartyReference!.PartyName!.Name.Should().Be("Camilla Parker", "BIAN has one name string");
        entry.PartyReference.PartyIdentification!.PartyIdentification!.IdentifierValue.Should().Be("CUS-000123");
    }

    [Test]
    public void Given_ACustomer_When_TheReferenceIsRetrieved_Then_ItIsValidAndCarriesContactsAndAddress()
    {
        // Given
        var customer = Customer();

        // When
        var reference = PartyReferenceDataDirectoryMapper.ToReference(customer);

        // Then
        BianSchema.Validate(BianFile, "Reference", reference, BianJson.Options).Should().BeEmpty();
        reference.eMailAddress.Should().Be(Contact("ElectronicAddress", "camilla.parker@example.com"));
        reference.CellPhoneNumber.Should().Be(Contact("PhoneNumber", "+61 412 345 678"));
        reference.ResidentialAddress.Should().Be(new BianPostalAddress
        {
            AddressType = "Residential",
            AddressLine = "1 George Street, Level 4",
            PostCode = new BianIdentifier { IdentifierValue = "2000" },
            TownName = new BianName { Name = "Sydney" },
            Country = new BianCountry { CountryCode = new BianCountryCode { Countrycode = "AU" } },
            AddressDescription = "NSW"
        }, "BIAN's CountrySubDivision is the kind of area, so the state's name travels in AddressDescription");
    }

    [Test]
    public void Given_ACustomerWithoutAResidentialAddress_When_TheReferenceIsRetrieved_Then_ItIsStillValid()
    {
        // Given
        var customer = Customer() with { ResidentialAddress = null };

        // When
        var reference = PartyReferenceDataDirectoryMapper.ToReference(customer);

        // Then
        BianSchema.Validate(BianFile, "Reference", reference, BianJson.Options).Should().BeEmpty();
        reference.ResidentialAddress.Should().BeNull();
    }

    [Test]
    public void Given_AContactChangeAsAPartnerWouldSendIt_When_CheckedAgainstBian_Then_ItIsValidAndMapsToTheNewContacts()
    {
        // Given
        var request = new Reference
        {
            eMailAddress = Contact("ElectronicAddress", "camilla@example.org"),
            CellPhoneNumber = Contact("PhoneNumber", "+61 400 000 001")
        };

        // When
        var errors = BianSchema.Validate(BianFile, "Reference", request, BianJson.Options);
        var (change, mappingErrors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        errors.Should().BeEmpty();
        mappingErrors.Should().BeEmpty();
        change.Should().Be(new BianContactChange("camilla@example.org", "+61 400 000 001"));
    }

    [Test]
    public void Given_ABodyWithAnAddressTypeBianDoesNotList_When_Validated_Then_TheCheckFindsIt()
    {
        // Given - the check itself must be able to fail, or a green test proves nothing (two $refs deep)
        var valid = PartyReferenceDataDirectoryMapper.ToReference(Customer());
        var reference = valid with { ResidentialAddress = valid.ResidentialAddress! with { AddressType = "Holiday" } };

        // When
        var errors = BianSchema.Validate(BianFile, "Reference", reference, BianJson.Options);

        // Then
        errors.Should().NotBeEmpty();
    }

    internal static BianContactPoint Contact(string type, string address) => new()
    {
        ContactPointType = type,
        LocationReference = new BianLocation { LocationAddress = address }
    };

    internal static CustomerDetailsDto Customer() => new(
        CustomerId: 42,
        CustomerNumber: "CUS-000123",
        FirstName: "Camilla",
        LastName: "Parker",
        Email: "camilla.parker@example.com",
        PhoneNumber: "+61 412 345 678",
        CustomerType: CustomerType.Individual,
        Status: "ACTIVE",
        BranchId: 1,
        Version: 3,
        ResidentialAddress: new CustomerAddressDto("1 George Street", "Level 4", "Sydney", "NSW", "2000", "AU"),
        CreatedAt: new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero));
}