using AwesomeAssertions;

using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A transfer slip is EWP's authority to debit the customer: only for an OPEN Digital Gold
// │ account, signed today, with the customer's mobile on record, the signature checked and
// │ the scan stored. Its payment key and the Partner Bank instruction ID are fixed once, so
// │ paying or sending again is never a second payment or a second order.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class GoldPurchase_Record_Edge_SlipRules
{
    private static readonly string ScanSha256 = new('a', 64);

    [Test]
    [Category("Happy")]
    public void Given_AnOpenAccount_When_ATodaysSlipIsRecorded_Then_ItIsRecordedForTheCustomersAccount()
    {
        // When
        var purchase = DigitalGoldTestData.RecordedPurchase(20m);

        // Then
        purchase.Status.Should().Be(GoldPurchase.Recorded);
        purchase.PartnerAccountNumber.Should().Be(DigitalGoldTestData.PartnerAccount);
        purchase.SlipMobile.Should().Be("+61412345678");
        purchase.PaymentRef.Should().NotBeEmpty();
        purchase.BranchCode.Should().Be(DigitalGoldTestData.Sophie.BranchCode);
    }

    [Test]
    [Category("Security")]
    public void Given_AnAccountNotYetOpened_When_ASlipIsRecorded_Then_ItIsRefused()
    {
        // Given
        var application = GoldAccountApplication.Record(
            DigitalGoldTestData.Customer, "Camilla Rossi", kycVerified: true, DigitalGoldTestData.MobileOnRecord,
            new DateOnly(1990, 5, 14), identityChecked: true, "FORM-VII-0001", DigitalGoldTestData.Today, signatureChecked: true,
            Guid.NewGuid(), ScanSha256, DigitalGoldTestData.Sophie, DigitalGoldTestData.Now);

        // When
        var act = () => RecordSlip(application);

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
    }

    [TestCase(-1)]
    [TestCase(1)]
    [Category("Boundary")]
    public void Given_AnOpenAccount_When_TheSlipIsNotSignedToday_Then_ItIsRefused(int days)
    {
        // When
        var act = () => RecordSlip(DigitalGoldTestData.OpenAccount(), signedOn: DigitalGoldTestData.Today.AddDays(days));

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
    }

    [TestCase("0412 345 679")]
    [TestCase("02 9999 0000")]
    [TestCase(null)]
    [Category("Security")]
    public void Given_AnOpenAccount_When_TheSlipCarriesAnotherPhoneNumber_Then_ItIsRefused(string? mobileOnSlip)
    {
        // When
        var act = () => RecordSlip(DigitalGoldTestData.OpenAccount(), mobileOnSlip: mobileOnSlip);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
    }

    [TestCase("+61 412 345 678")]
    [TestCase("0412-345-678")]
    public void Given_AnOpenAccount_When_TheMobileIsWrittenDifferently_Then_ItIsAccepted(string mobileOnSlip)
    {
        // When
        var purchase = RecordSlip(DigitalGoldTestData.OpenAccount(), mobileOnSlip: mobileOnSlip);

        // Then
        purchase.SlipMobile.Should().Be("+61412345678");
    }

    [Test]
    [Category("Security")]
    public void Given_AnOpenAccount_When_TheSignatureWasNotChecked_Then_ItIsRefused()
    {
        // When
        var act = () => RecordSlip(DigitalGoldTestData.OpenAccount(), signatureChecked: false);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
    }

    [TestCase(0)]
    [TestCase(-5)]
    [TestCase(50_000.01)]
    [TestCase(20.005)]
    [Category("Boundary")]
    public void Given_AnOpenAccount_When_TheAmountIsOutOfRange_Then_ItIsRefused(decimal amount)
    {
        // When
        var act = () => RecordSlip(DigitalGoldTestData.OpenAccount(), amount: amount);

        // Then
        act.Should().Throw<DigitalGoldRuleViolationException>();
    }

    [Test]
    [Category("Resilience")]
    public void Given_ASentPurchaseWithoutAnAnswer_When_ItIsSentAgain_Then_TheInstructionIdStaysTheSame()
    {
        // Given
        var purchase = DigitalGoldTestData.RecordedPurchase();
        var first = purchase.PrepareToSend(DigitalGoldTestData.Sophie.LanId, DigitalGoldTestData.Now);

        // When
        var second = purchase.PrepareToSend("ruby.cs", DigitalGoldTestData.Now.AddMinutes(2));

        // Then
        second.Should().Be(first);
        purchase.SentAt.Should().Be(DigitalGoldTestData.Now);
        purchase.Events.Last().Resend.Should().BeTrue();
    }

    [Test]
    [Category("Boundary")]
    public void Given_YesterdaysRecordedSlip_When_ItIsSent_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.RecordedPurchase();

        // When
        var act = () => purchase.PrepareToSend(DigitalGoldTestData.Sophie.LanId, DigitalGoldTestData.Now.AddDays(1));

        // Then
        act.Should().Throw<DigitalGoldConflictException>();
        purchase.Status.Should().Be(GoldPurchase.Recorded);
    }

    [Test]
    [Category("Security")]
    public void Given_AnOrderedPurchase_When_ItsPaymentIsPrepared_Then_ItPaysExactlyTheSlipToTheSettlementAccount()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase(20m);
        var settlement = DigitalGoldTestData.Settlement;

        // When
        var payment = purchase.PaymentInstruction(settlement);

        // Then
        payment.IdempotencyKey.Should().Be(purchase.PaymentRef);
        payment.Amount.Should().Be(20m);
        payment.FromBsb.Should().Be("062-000");
        payment.FromAccountNumber.Should().Be("72947086");
        payment.ToBsb.Should().Be(settlement.Bsb);
        payment.ToAccountNumber.Should().Be(settlement.AccountNumber);
        payment.Reference.Should().Be(DigitalGoldTestData.PartnerOrder);
    }

    [Test]
    [Category("Security")]
    public void Given_AnOrderedPurchaseWithAPayment_When_AnotherPaymentIsRecorded_Then_ItIsRefused()
    {
        // Given
        var purchase = DigitalGoldTestData.OrderedPurchase();
        purchase.RecordPaymentStarted("PAY-261022-AAAAAA", DigitalGoldTestData.Sophie.LanId, DigitalGoldTestData.Now);

        // When
        var same = () => purchase.RecordPaymentStarted("PAY-261022-AAAAAA", DigitalGoldTestData.Sophie.LanId, DigitalGoldTestData.Now);
        var other = () => purchase.RecordPaymentStarted("PAY-261022-BBBBBB", DigitalGoldTestData.Sophie.LanId, DigitalGoldTestData.Now);

        // Then
        same.Should().NotThrow();
        other.Should().Throw<DigitalGoldConflictException>();
        purchase.PaymentNumber.Should().Be("PAY-261022-AAAAAA");
    }

    private static GoldPurchase RecordSlip(
        GoldAccountApplication account, decimal amount = 20m, DateOnly? signedOn = null,
        string? mobileOnSlip = DigitalGoldTestData.MobileOnRecord, bool signatureChecked = true) =>
        GoldPurchase.Record(
            account, amount, "062-000", "72947086", "SLIP-0001", signedOn ?? DigitalGoldTestData.Today, mobileOnSlip,
            signatureChecked, Guid.NewGuid(), ScanSha256, DigitalGoldTestData.Sophie, DigitalGoldTestData.Now);
}