using EnterpriseWebPlatform.PartnerProducts.Api.Domain;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

/// <summary>
/// Known people, accounts and purchases for the Digital Gold domain tests - built through the
/// aggregates' own public methods, as the Partner Products API builds them.
/// </summary>
internal static class DigitalGoldTestData
{
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ 00:00 UTC is 11:00 in Sydney (AEDT): "today" for a slip is 2026-10-22 on both clocks,
    // │ so no test depends on the machine's time zone.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static readonly DateTimeOffset Now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 10, 22);

    public static readonly Actor Sophie = new("sub-sophie", "sophie.cs", "SYD001");

    public const string Customer = "CUST-100001";
    public const string MobileOnRecord = "0412 345 678";
    public const string PartnerAccount = "PB-DGA-CCRNCU";
    public const string PartnerOrder = "PB-ORD-7KQ2MX";
    public const string NetworkReference = "NPP-20261022-000123";

    public static readonly SettlementAccount Settlement = new("Partner Bank Digital Gold Settlement", "182-512", "961234567");

    private static readonly string ScanSha256 = new('a', 64);

    /// <summary>Camilla's Form VII, opened at the Partner Bank.</summary>
    public static GoldAccountApplication OpenAccount()
    {
        var application = GoldAccountApplication.Record(
            Customer, "Camilla Rossi", kycVerified: true, MobileOnRecord, new DateOnly(1990, 5, 14), identityChecked: true,
            "FORM-VII-0001", Today, signatureChecked: true, Guid.NewGuid(), ScanSha256, Sophie, Now);
        application.PrepareToSend(Sophie.LanId, Now);
        application.MarkOpened(PartnerAccount, Now);
        return application;
    }

    /// <summary>A AUD 20 transfer slip, recorded at the counter (not yet sent).</summary>
    public static GoldPurchase RecordedPurchase(decimal amount = 20m) =>
        GoldPurchase.Record(
            OpenAccount(), amount, "062-000", "72947086", "SLIP-0001", Today, MobileOnRecord, signatureChecked: true,
            Guid.NewGuid(), ScanSha256, Sophie, Now);

    /// <summary>The Partner Bank placed the order: waiting for EWP's payment.</summary>
    public static GoldPurchase OrderedPurchase(decimal amount = 20m)
    {
        var purchase = RecordedPurchase(amount);
        purchase.PrepareToSend(Sophie.LanId, Now);
        purchase.MarkOrdered(PartnerOrder, 160.25m, 0.1248m, Now.AddMinutes(15), Now);
        return purchase;
    }

    /// <summary>Paid with its own payment: the payment advice is due.</summary>
    public static GoldPurchase PaidPurchase()
    {
        var purchase = OrderedPurchase();
        PayExactly(purchase);
        return purchase;
    }

    /// <summary>The purchase's own payment: its Idempotency-Key, its amount, to the settlement account.</summary>
    public static void PayExactly(GoldPurchase purchase, string networkReference = NetworkReference) =>
        purchase.MarkPaid(purchase.PaymentRef, networkReference, purchase.AmountAud, Settlement.Bsb, Settlement.AccountNumber,
            Settlement, Now);
}