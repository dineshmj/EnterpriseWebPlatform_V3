using AwesomeAssertions;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The Worker cancels the Partner Bank's order when no payment came. Once money has left
// │ the customer's account (PAID, ALLOCATED) the purchase is finished with the payment
// │ advice and never cancelled: a cancellation then would leave the customer paid without
// │ gold. EnsureCancellable is asked BEFORE the cancellation goes to the Partner Bank.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class GoldPurchase_MarkCancelled_Edge_OnlyWhileUnpaid
{
    [Test]
    [Category("Compensation")]
    public void Given_AnUnpaidOrder_When_ItIsCancelled_Then_ItIsCancelledWithTheReason()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();
        purchase.ClearEvents();

        // When
        purchase.EnsureCancellable();
        purchase.MarkCancelled("No payment within 30 minutes.", DigitalGoldTestData.Now);

        // Then
        purchase.Status.Should().Be(GoldPurchase.Cancelled);
        purchase.StatusReason.Should().Be("No payment within 30 minutes.");
        purchase.Events.Should().ContainSingle().Which.EventType.Should().Be(DigitalGoldEventTypes.PurchaseCancelled);
    }

    [Test]
    [Category("Resilience")]
    public void Given_ACancelledPurchase_When_TheCancellationIsRepeated_Then_NothingChanges()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();
        purchase.MarkCancelled("No payment within 30 minutes.", DigitalGoldTestData.Now);
        var version = purchase.Version;
        purchase.ClearEvents();

        // When
        purchase.EnsureCancellable();
        purchase.MarkCancelled("Cancelled again after a lost answer.", DigitalGoldTestData.Now);

        // Then
        purchase.Status.Should().Be(GoldPurchase.Cancelled);
        purchase.StatusReason.Should().Be("No payment within 30 minutes.");
        purchase.Version.Should().Be(version);
        purchase.Events.Should().BeEmpty();
    }

    [Test]
    [Category("Security")]
    public void Given_APaidPurchase_When_ACancellationIsAsked_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.PaidPurchase();

        // When
        var ensure = () => purchase.EnsureCancellable();
        var cancel = () => purchase.MarkCancelled("No payment within 30 minutes.", DigitalGoldTestData.Now);

        // Then
        ensure.Should().Throw<DigitalGoldConflictException>();
        cancel.Should().Throw<DigitalGoldConflictException>();
        purchase.Status.Should().Be(GoldPurchase.Paid);
    }

    [Test]
    [Category("Security")]
    public void Given_AnAllocatedPurchase_When_ACancellationIsAsked_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.PaidPurchase();
        purchase.MarkAllocated(160.30m, 0.1247m, DigitalGoldTestData.Now);

        // When
        var act = () => purchase.EnsureCancellable();

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
        purchase.Status.Should().Be(GoldPurchase.Allocated);
    }

    [Test]
    [Category("Boundary")]
    public void Given_APurchaseTheBankHasNotOrdered_When_ACancellationIsAsked_Then_ItIsRefused()
    {
        // Given
        var recorded = DigitalGoldTestData.RecordedPurchase();
        var sent = DigitalGoldTestData.RecordedPurchase();
        sent.PrepareToSend(DigitalGoldTestData.Sophie.LanId, DigitalGoldTestData.Now);

        // When
        var cancelRecorded = () => recorded.EnsureCancellable();
        var cancelSent = () => sent.EnsureCancellable();

        // Then
        cancelRecorded.Should().Throw<DigitalGoldConflictException>();
        cancelSent.Should().Throw<DigitalGoldConflictException>("a SENT purchase may be ORDERED at the Partner Bank: send again first");
    }
}