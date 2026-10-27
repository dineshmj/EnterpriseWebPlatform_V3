using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Bian;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Bian;

[TestFixture]
[Category("Boundary")]
public sealed class PaymentOrderInitiation_ToPaymentOrder_Edge_RequiredFields
{
    [Test]
    public void Given_AFullBianPaymentOrder_When_Mapped_Then_EveryEwpFieldComesFromItsBianProperty()
    {
        // Given
        var request = FullRequest();

        // When
        var (order, errors) = PaymentOrderInitiationMapper.ToPaymentOrder(request);

        // Then
        errors.Should().BeEmpty();
        order.Should().Be(new BianPaymentOrder("CUS-000123", "062-000", "72947086", "Harbour Plumbing", "083-004", "12345678", 1100.00m, "Invoice 7781"));
    }

    [Test]
    public void Given_AnEmptyOrder_When_Mapped_Then_EachMissingPropertyIsNamedWithItsBianPath()
    {
        // Given - BIAN marks every property optional; EWP still needs a whole payment
        var request = new PaymentOrderInitiationTransaction();

        // When
        var (order, errors) = PaymentOrderInitiationMapper.ToPaymentOrder(request);

        // Then
        order.Should().BeNull();
        errors.Should().HaveCount(7);
        errors.Should().Contain(e => e.StartsWith("PayerReference.PayerIdentifier"));
        errors.Should().Contain(e => e.StartsWith("PayeeProductInstanceReference.ProductAgreementIdentification.IdentifierValue"));
        errors.Should().Contain(e => e.StartsWith("Amount.AmountValue"));
    }

    [Test]
    public void Given_NoBody_When_Mapped_Then_ItIsRefused()
    {
        // Given / When
        var (order, errors) = PaymentOrderInitiationMapper.ToPaymentOrder(null);

        // Then
        order.Should().BeNull();
        errors.Should().ContainSingle();
    }

    [TestCase("USD", null)]
    [TestCase(null, "EUR")]
    public void Given_ACurrencyOtherThanAud_When_Mapped_Then_ItIsRefused(string? amountCurrency, string? currency)
    {
        // Given
        var request = FullRequest() with
        {
            Amount = new BianAmount { AmountValue = "10.00", AmountCurrency = amountCurrency is null ? null : new BianCurrencyCode { Currencycode = amountCurrency } },
            Currency = currency is null ? null : new BianCurrencyCode { Currencycode = currency }
        };

        // When
        var (order, errors) = PaymentOrderInitiationMapper.ToPaymentOrder(request);

        // Then
        order.Should().BeNull();
        errors.Should().ContainSingle(e => e.Contains("Only AUD payments are supported"));
    }

    [TestCase("1,100.00")]
    [TestCase("1100,00")]
    [TestCase("-5")]
    [TestCase("ten dollars")]
    public void Given_AnAmountThatIsNotAPlainDecimal_When_Mapped_Then_ItIsRefused(string amount)
    {
        // Given - no thousands separators, no comma decimals, no sign: one unambiguous form
        var request = FullRequest() with { Amount = new BianAmount { AmountValue = amount } };

        // When
        var (order, errors) = PaymentOrderInitiationMapper.ToPaymentOrder(request);

        // Then
        order.Should().BeNull();
        errors.Should().ContainSingle(e => e.StartsWith("Amount.AmountValue must be a decimal number"));
    }

    [TestCase("Approve", true)]
    [TestCase("reject", false)]
    [TestCase(" APPROVE ", true)]
    public void Given_ATaskResult_When_MappedToADecision_Then_ItApprovesOrRejectsWithTheRemarks(string task, bool approve)
    {
        // Given
        var request = new OrderInitiation
        {
            OrderInitiationTaskResult = new BianTask { Task = task },
            PaymentOrderProcedureInstanceStatus = new BianStatus { StatusReason = "Checked with the customer" }
        };

        // When
        var (decision, error) = PaymentOrderInitiationMapper.ToDecision(request);

        // Then
        error.Should().BeNull();
        decision.Should().Be(new BianOrderDecision(approve, "Checked with the customer"));
    }

    [TestCase("Approved")]
    [TestCase("")]
    [TestCase(null)]
    public void Given_AnyOtherTask_When_MappedToADecision_Then_NothingIsDecided(string? task)
    {
        // Given
        var request = new OrderInitiation { OrderInitiationTaskResult = new BianTask { Task = task } };

        // When
        var (decision, error) = PaymentOrderInitiationMapper.ToDecision(request);

        // Then
        decision.Should().BeNull();
        error.Should().Contain("'Approve' or 'Reject'");
    }

    [TestCase("PENDING_APPROVAL", "Pending")]
    [TestCase("COMPLETED", "Acceptedsettlementcompleted")]
    [TestCase("REJECTED", "Rejected")]
    [TestCase("FAILED", "Rejected")]
    [TestCase("SENDING_TO_NETWORK", "Acceptedsettlementinprocess")]
    public void Given_AnEwpStatus_When_ShownInBian_Then_ItBecomesTheClosestBianStatusType(string ewp, string bian) =>
        PaymentOrderInitiationMapper.ToBianStatusType(ewp).Should().Be(bian);

    internal static PaymentOrderInitiationTransaction FullRequest() => new()
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