using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A bad request never becomes a payment: amounts in whole cents within the limit, two
// │ different accounts, an Idempotency-Key and a known initiator - checked before any
// │ saga starts.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Boundary")]
public sealed class Payment_Initiate_Edge_InvalidDetails
{
    private static Payment Initiate(
        decimal amount = 140m, Guid? paymentRef = null, string initiatedBy = PaymentsTestData.Sophie,
        BankAccountRef? to = null, string payeeName = "Harbour Plumbing Pty Ltd", string? reference = null) =>
        Payment.Initiate(
            paymentRef ?? Guid.NewGuid(), "PAY-261015-TEST03", "CUST-100001", PaymentsTestData.Payer, payeeName,
            to ?? PaymentsTestData.Payee, amount, reference, PaymentsTestData.Sydney, initiatedBy,
            PaymentsTestData.ApprovalThreshold, PaymentsTestData.Now);

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(0.001)]
    [TestCase(1_000_000.01)]
    public void Given_AnAmountOutsideTheRules_When_ThePaymentIsInitiated_Then_ItIsRefused(decimal amount)
    {
        // Given / When
        var initiate = () => Initiate(amount);

        // Then
        initiate.Should().Throw<DomainRuleViolationException>().WithMessage("*in whole cents*");
    }

    [Test]
    public void Given_TheLargestAllowedAmount_When_ThePaymentIsInitiated_Then_ItIsAccepted()
    {
        // Given / When
        var payment = Initiate(PaymentRules.MaxAmount);

        // Then
        payment.Amount.Should().Be(1_000_000m);
    }

    [Test]
    public void Given_NoIdempotencyKey_When_ThePaymentIsInitiated_Then_ItIsRefused()
    {
        // Given / When
        var initiate = () => Initiate(paymentRef: Guid.Empty);

        // Then
        initiate.Should().Throw<DomainRuleViolationException>().WithMessage("*Idempotency-Key*");
    }

    [Test]
    public void Given_ThePayeeIsThePayingAccount_When_ThePaymentIsInitiated_Then_ItIsRefused()
    {
        // Given / When
        var initiate = () => Initiate(to: BankAccountRef.Create("062000", "72947086", "payee"));

        // Then
        initiate.Should().Throw<DomainRuleViolationException>().WithMessage("*must differ*");
    }

    [Test]
    [Category("Security")]
    public void Given_NoInitiator_When_ThePaymentIsInitiated_Then_ItIsRefused()
    {
        // Given / When
        var initiate = () => Initiate(initiatedBy: " ");

        // Then
        initiate.Should().Throw<DomainRuleViolationException>().WithMessage("*initiating staff member*");
    }

    [TestCase("<script>alert(1)</script>")]
    [TestCase("")]
    [Category("Security")]
    public void Given_APayeeNameWithMarkupOrNothing_When_ThePaymentIsInitiated_Then_ItIsRefused(string payeeName)
    {
        // Given / When
        var initiate = () => Initiate(payeeName: payeeName);

        // Then
        initiate.Should().Throw<DomainRuleViolationException>().WithMessage("*payee's account name*");
    }

    [Test]
    public void Given_AReferenceLongerThan35Characters_When_ThePaymentIsInitiated_Then_ItIsRefused()
    {
        // Given / When
        var initiate = () => Initiate(reference: new string('A', 36));

        // Then
        initiate.Should().Throw<DomainRuleViolationException>().WithMessage("*at most 35*");
    }
}