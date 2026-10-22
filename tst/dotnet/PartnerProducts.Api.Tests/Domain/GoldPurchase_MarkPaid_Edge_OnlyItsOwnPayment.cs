using AwesomeAssertions;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ PAID is what lets the Worker send the payment advice, and the advice is what makes the
// │ Partner Bank allocate gold. So a purchase becomes PAID only with ITS OWN completed
// │ payment: its Idempotency-Key, the slip's exact amount, the configured settlement account.
// │ Any other payment - another key, a cent less, another payee - is refused. The same
// │ payment reported twice changes nothing (the Worker may be told twice).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class GoldPurchase_MarkPaid_Edge_OnlyItsOwnPayment
{
    [Test]
    [Category("Happy")]
    public void Given_AnOrderedPurchase_When_ItsOwnPaymentCompletes_Then_ItIsPaid()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();
        purchase.ClearEvents();

        // When
        DigitalGoldTestData.PayExactly(purchase);

        // Then
        purchase.Status.Should().Be(GoldPurchase.Paid);
        purchase.NetworkReference.Should().Be(DigitalGoldTestData.NetworkReference);
        purchase.PaidAt.Should().Be(DigitalGoldTestData.Now);
        purchase.Events.Should().ContainSingle().Which.EventType.Should().Be(DigitalGoldEventTypes.PurchasePaid);
    }

    [Test]
    [Category("Security")]
    public void Given_AnOrderedPurchase_When_APaymentWithAnotherKeyCompletes_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();
        var settlement = DigitalGoldTestData.Settlement;

        // When
        var act = () => purchase.MarkPaid(Guid.NewGuid(), DigitalGoldTestData.NetworkReference, purchase.AmountAud,
            settlement.Bsb, settlement.AccountNumber, settlement, DigitalGoldTestData.Now);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
        purchase.Status.Should().Be(GoldPurchase.Ordered);
    }

    [TestCase(19.99)]
    [TestCase(20.01)]
    [TestCase(200)]
    [Category("Security")]
    public void Given_AnOrderedPurchase_When_ThePaidAmountDiffers_Then_ItIsRefused(decimal paidAmount)
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase(20m);
        var settlement = DigitalGoldTestData.Settlement;

        // When
        var act = () => purchase.MarkPaid(purchase.PaymentRef, DigitalGoldTestData.NetworkReference, paidAmount,
            settlement.Bsb, settlement.AccountNumber, settlement, DigitalGoldTestData.Now);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
        purchase.Status.Should().Be(GoldPurchase.Ordered);
    }

    [TestCase("182-512", "961234568")]
    [TestCase("062-000", "961234567")]
    [Category("Security")]
    public void Given_AnOrderedPurchase_When_ThePaymentWentToAnotherPayee_Then_ItIsRefused(string payeeBsb, string payeeAccount)
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();

        // When
        var act = () => purchase.MarkPaid(purchase.PaymentRef, DigitalGoldTestData.NetworkReference, purchase.AmountAud,
            payeeBsb, payeeAccount, DigitalGoldTestData.Settlement, DigitalGoldTestData.Now);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
        purchase.Status.Should().Be(GoldPurchase.Ordered);
    }

    [TestCase("")]
    [TestCase("   ")]
    [Category("Boundary")]
    public void Given_AnOrderedPurchase_When_TheNetworkReferenceIsMissing_Then_ItIsRefused(string networkReference)
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();

        // When
        var act = () => DigitalGoldTestData.PayExactly(purchase, networkReference);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
        purchase.Status.Should().Be(GoldPurchase.Ordered);
    }

    [Test]
    [Category("Resilience")]
    public void Given_APaidPurchase_When_TheSamePaymentIsReportedAgain_Then_NothingChanges()
    {
        // Given
        var purchase = DigitalGoldTestData.PaidPurchase();
        var version = purchase.Version;
        purchase.ClearEvents();

        // When
        DigitalGoldTestData.PayExactly(purchase);

        // Then
        purchase.Status.Should().Be(GoldPurchase.Paid);
        purchase.Version.Should().Be(version);
        purchase.Events.Should().BeEmpty();
    }

    [Test]
    [Category("Security")]
    public void Given_APaidPurchase_When_ASecondNetworkReferenceIsReported_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.PaidPurchase();

        // When
        var act = () => DigitalGoldTestData.PayExactly(purchase, "NPP-20261022-999999");

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
        purchase.NetworkReference.Should().Be(DigitalGoldTestData.NetworkReference);
    }

    [Test]
    [Category("Security")]
    public void Given_ARecordedPurchaseTheBankNeverOrdered_When_APaymentIsReported_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.RecordedPurchase();

        // When
        var act = () => DigitalGoldTestData.PayExactly(purchase);

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
        purchase.Status.Should().Be(GoldPurchase.Recorded);
    }

    [Test]
    [Category("Compensation")]
    public void Given_ACancelledPurchase_When_ALatePaymentIsReported_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();
        purchase.MarkCancelled("No payment within 30 minutes.", DigitalGoldTestData.Now);

        // When
        var act = () => DigitalGoldTestData.PayExactly(purchase);

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
        purchase.Status.Should().Be(GoldPurchase.Cancelled);
    }

    [Test]
    public void Given_APaidPurchase_When_TheAdviceIsAccepted_Then_ItIsAllocatedOnce()
    {
        // Given
        var purchase = DigitalGoldTestData.PaidPurchase();

        // When
        purchase.MarkAllocated(160.30m, 0.1247m, DigitalGoldTestData.Now);
        purchase.MarkAllocated(160.30m, 0.1247m, DigitalGoldTestData.Now);

        // Then
        purchase.Status.Should().Be(GoldPurchase.Allocated);
        purchase.Grams.Should().Be(0.1247m);
        purchase.Events.Count(e => e.EventType == DigitalGoldEventTypes.PurchaseAllocated).Should().Be(1);
    }

    [Test]
    [Category("Security")]
    public void Given_AnUnpaidOrder_When_AllocationIsReported_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();

        // When
        var act = () => purchase.MarkAllocated(160.30m, 0.1247m, DigitalGoldTestData.Now);

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
        purchase.Status.Should().Be(GoldPurchase.Ordered);
    }
}