using System.Text.Json;

using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.API.Bian;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

using static EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Bian.PartyReferenceDataDirectory_Contract_Happy_ValidAgainstBian;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Bian;

[TestFixture]
[Category("Boundary")]
public sealed class PartyReferenceDataDirectory_ToContactChange_Edge_OnlyContactsChange
{
    [Test]
    public void Given_NoBody_When_Mapped_Then_ItIsRefused()
    {
        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(null, Customer());

        // Then
        change.Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("A Reference body is required"));
    }

    [Test]
    public void Given_AnEmptyReference_When_Mapped_Then_ItSaysWhatCanChange()
    {
        // Given - BIAN marks every property optional; an update without contacts changes nothing
        var request = new Reference();

        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        change.Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("eMailAddress or CellPhoneNumber is required"));
    }

    [Test]
    public void Given_OnlyANewEmail_When_Mapped_Then_ThePhoneStaysAsItWas()
    {
        // Given
        var request = new Reference { eMailAddress = Contact("ElectronicAddress", "  camilla@example.org ") };

        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        errors.Should().BeEmpty();
        change.Should().Be(new BianContactChange("camilla@example.org", "+61 412 345 678"));
    }

    [Test]
    public void Given_TheRetrievedReferenceSentBackWithANewPhone_When_Mapped_Then_TheUnchangedPropertiesAreAccepted()
    {
        // Given - read, modify, write: name, address and dates come back exactly as retrieved
        var retrieved = PartyReferenceDataDirectoryMapper.ToReference(Customer());
        var request = retrieved with { CellPhoneNumber = Contact("PhoneNumber", "+61 400 000 001") };

        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        errors.Should().BeEmpty();
        change.Should().Be(new BianContactChange("camilla.parker@example.com", "+61 400 000 001"));
    }

    [Test]
    public void Given_ADifferentName_When_Mapped_Then_ItIsRefusedAndPointsToV1()
    {
        // Given - one BIAN name string cannot be split into first and last name without guessing
        var request = new Reference
        {
            PartyFullLegalName = new BianName { Name = "Camilla Rose Parker" },
            eMailAddress = Contact("ElectronicAddress", "camilla@example.org")
        };

        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        change.Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("PartyFullLegalName cannot be changed") && e.Contains("/v1/customers"));
    }

    [Test]
    public void Given_ADifferentAddress_When_Mapped_Then_ItIsRefused()
    {
        // Given
        var retrieved = PartyReferenceDataDirectoryMapper.ToReference(Customer());
        var request = retrieved with { ResidentialAddress = retrieved.ResidentialAddress! with { TownName = new BianName { Name = "Melbourne" } } };

        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        change.Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("ResidentialAddress cannot be changed"));
    }

    [TestCase("PhoneNumber", "camilla@example.org", "must be 'ElectronicAddress'")]
    [TestCase("ElectronicAddress", "   ", "LocationReference.LocationAddress is required")]
    [TestCase(null, null, "LocationReference.LocationAddress is required")]
    public void Given_AnEmailContactThatIsNotAnEmailAddress_When_Mapped_Then_ItIsRefused(string? type, string? address, string expected)
    {
        // Given
        var request = new Reference { eMailAddress = new BianContactPoint { ContactPointType = type, LocationReference = new BianLocation { LocationAddress = address } } };

        // When
        var (change, errors) = PartyReferenceDataDirectoryMapper.ToContactChange(request, Customer());

        // Then
        change.Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("eMailAddress") && e.Contains(expected));
    }

    [Test]
    public void Given_APropertyBianDefinesButEwpDoesNotKeep_When_TheBodyIsRead_Then_ItIsRefusedNotDropped()
    {
        // Given - a nationality would otherwise be accepted and silently lost
        const string body = """{ "Nationality": { "CountryCode": { "Countrycode": "AU" } }, "CellPhoneNumber": { "LocationReference": { "LocationAddress": "+61 400 000 001" } } }""";

        // When
        var read = () => JsonSerializer.Deserialize<Reference>(body, BianJson.Options);

        // Then
        read.Should().Throw<JsonException>().WithMessage("*Nationality*");
    }

    [TestCase(CustomerType.Individual, "Individual")]
    [TestCase(CustomerType.Business, null)]
    public void Given_ACustomerType_When_ShownInBian_Then_OnlyAnIndividualHasALegalStructure(CustomerType type, string? structure) =>
        PartyReferenceDataDirectoryMapper.ToReference(Customer() with { CustomerType = type }).PartyLegalStructure.Should().Be(structure);
}