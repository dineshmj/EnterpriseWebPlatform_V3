using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The approval tier is decided once, when the payment is captured: AT the threshold no
// │ officer is needed, one cent above it one is (BSS-Requirements section 10).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Happy")]
public sealed class Payment_Initiate_Happy_ApprovalTier
{
    [TestCase(140.00, false)]
    [TestCase(1_000.00, false)]
    [TestCase(1_000.01, true)]
    [TestCase(25_000.00, true)]
    [Category("Boundary")]
    public void Given_AnAmount_When_ThePaymentIsInitiated_Then_ApprovalIsRequiredOnlyAboveTheThreshold(decimal amount, bool approvalRequired)
    {
        // Given / When
        var payment = PaymentsTestData.NewPayment(amount);

        // Then
        payment.ApprovalRequired.Should().Be(approvalRequired);
        payment.Status.Should().Be(PaymentStatus.Initiated);
        payment.Currency.Should().Be("AUD");
        payment.HasEnded.Should().BeFalse();
    }

    [Test]
    public void Given_ThePayeeNameWithExtraSpaces_When_ThePaymentIsInitiated_Then_TheNameIsNormalised()
    {
        // Given / When
        var payment = Payment.Initiate(
            Guid.NewGuid(), "PAY-261015-TEST02", " CUST-100001 ", PaymentsTestData.Payer, "  Harbour   Plumbing  ", PaymentsTestData.Payee,
            10m, "   ", PaymentsTestData.Sydney, PaymentsTestData.Sophie, PaymentsTestData.ApprovalThreshold, PaymentsTestData.Allowed, PaymentsTestData.Now);

        // Then
        payment.PayeeName.Should().Be("Harbour Plumbing");
        payment.CustomerNumber.Should().Be("CUST-100001");
        payment.Reference.Should().BeNull();
    }
}