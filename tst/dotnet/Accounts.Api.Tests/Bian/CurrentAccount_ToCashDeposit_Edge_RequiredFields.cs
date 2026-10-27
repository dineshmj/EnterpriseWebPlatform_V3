using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Bian;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Bian;

[TestFixture]
[Category("Boundary")]
public sealed class CurrentAccount_ToCashDeposit_Edge_RequiredFields
{
    [Test]
    public void Given_AnEmptyDeposit_When_Mapped_Then_EachMissingPropertyIsNamedWithItsBianPath()
    {
        // Given - BIAN marks every property optional; a cash credit needs the customer and the amount
        var request = new Deposit();

        // When
        var (deposit, errors) = CurrentAccountMapper.ToCashDeposit(request);

        // Then
        deposit.Should().BeNull();
        errors.Should().HaveCount(2);
        errors.Should().Contain(e => e.StartsWith("DepositTransactionSourceReference"));
        errors.Should().Contain(e => e.StartsWith("DepositTransactionAmount.AmountValue"));
    }

    [TestCase("FixedTermDeposit")]
    [TestCase("CardDeposit")]
    public void Given_ADepositTypeOtherThanCash_When_Mapped_Then_ItIsRefused(string type)
    {
        // Given - a cheque, card or term deposit must never be booked as cash at the counter
        var request = CashDeposit() with { DepositType = type };

        // When
        var (deposit, errors) = CurrentAccountMapper.ToCashDeposit(request);

        // Then
        deposit.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("Only DepositType 'CashDeposit'"));
    }

    [TestCase("0")]
    [TestCase("-50.00")]
    [TestCase("1,000.00")]
    [TestCase("lots")]
    public void Given_AnAmountThatIsNotAPositivePlainDecimal_When_Mapped_Then_ItIsRefused(string amount)
    {
        // Given
        var request = CashDeposit() with { DepositTransactionAmount = new BianAmount { AmountValue = amount } };

        // When
        var (deposit, errors) = CurrentAccountMapper.ToCashDeposit(request);

        // Then
        deposit.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("must be a positive decimal number"));
    }

    [Test]
    public void Given_ACurrencyOtherThanAud_When_Mapped_Then_ItIsRefused()
    {
        // Given
        var request = CashDeposit() with { DepositTransactionAmount = new BianAmount { AmountValue = "250.00", AmountCurrency = new BianCurrencyCode { Currencycode = "NZD" } } };

        // When
        var (deposit, errors) = CurrentAccountMapper.ToCashDeposit(request);

        // Then
        deposit.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("Only AUD deposits are supported"));
    }

    [Test]
    public void Given_NoDepositType_When_Mapped_Then_ItIsTakenAsCash()
    {
        // Given - the type is optional in BIAN; this endpoint records cash only
        var request = CashDeposit() with { DepositType = null };

        // When
        var (deposit, errors) = CurrentAccountMapper.ToCashDeposit(request);

        // Then
        errors.Should().BeEmpty();
        deposit.Should().Be(new BianCashDeposit("CUS-000123", 250.00m));
    }

    [TestCase("ACTIVE", "Enabled")]
    [TestCase("FROZEN", "Disabled")]
    [TestCase("CLOSED", "Disabled")]
    public void Given_AnEwpAccountStatus_When_ShownInBian_Then_ItBecomesTheClosestBianStatusType(string ewp, string bian) =>
        CurrentAccountMapper.ToBianStatusType(ewp).Should().Be(bian);

    internal static Deposit CashDeposit() => new()
    {
        DepositType = "CashDeposit",
        DepositTransactionSourceReference = "CUS-000123",
        DepositTransactionAmount = new BianAmount { AmountValue = "250.00", AmountCurrency = new BianCurrencyCode { Currencycode = "AUD" } }
    };
}