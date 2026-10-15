using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

[TestFixture]
[Category("Boundary")]
public sealed class BankAccountRef_Create_Edge_Formats
{
    [TestCase("062-000", "72947086")]
    [TestCase("062000", "72947086")]
    [TestCase("062 000", " 7294 7086 ")]
    public void Given_ABsbWrittenAnyCommonWay_When_TheAccountIsCreated_Then_ItIsStoredInOneForm(string bsb, string accountNumber)
    {
        // Given / When
        var account = BankAccountRef.Create(bsb, accountNumber, "paying");

        // Then
        account.Should().Be(BankAccountRef.Create("062-000", "72947086", "paying"));
        account.ToString().Should().Be("062-000 72947086");
    }

    [TestCase("06200", "72947086", "*BSB must be 6 digits*")]
    [TestCase("062-00A", "72947086", "*BSB must be 6 digits*")]
    [TestCase("062-000", "1234", "*account number must be 5 to 9 digits*")]
    [TestCase("062-000", "1234567890", "*account number must be 5 to 9 digits*")]
    [TestCase(null, "72947086", "*BSB must be 6 digits*")]
    public void Given_AMalformedBsbOrAccountNumber_When_TheAccountIsCreated_Then_ItIsRefused(string? bsb, string accountNumber, string message)
    {
        // Given / When
        var create = () => BankAccountRef.Create(bsb, accountNumber, "payee");

        // Then
        create.Should().Throw<DomainRuleViolationException>().WithMessage(message);
    }
}