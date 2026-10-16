namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>
/// A Digital Gold purchase: the customer's transfer authorization slip (amount, phone number,
/// signature), signed at EWP's counter for an OPEN Digital Gold account. It is EWP's authority to
/// debit the customer's account and buy at that day's price; EWP records it (the scan in
/// Documents Management), then sends it to the Partner Bank, pays and sends the payment advice.
/// RECORDED -> SENT -> ORDERED -> PAID / ALLOCATED, or REFUSED / CANCELLED.
/// </summary>
public sealed class GoldPurchase
{
    public const string Recorded = "RECORDED";

    private GoldPurchase()
    {
    }

    public long Id { get; private set; }

    /// <summary>EWP's reference, e.g. DGP-261016-8KQNFR.</summary>
    public string Number { get; private set; } = string.Empty;

    public long ApplicationId { get; private set; }

    public string CustomerNumber { get; private set; } = string.Empty;

    public string PartnerAccountNumber { get; private set; } = string.Empty;

    public decimal AmountAud { get; private set; }

    public string DebtorBsb { get; private set; } = string.Empty;

    public string DebtorAccountNumber { get; private set; } = string.Empty;

    public string BranchCode { get; private set; } = string.Empty;

    public string SlipNumber { get; private set; } = string.Empty;

    public DateOnly SlipSignedOn { get; private set; }

    public string SlipMobile { get; private set; } = string.Empty;

    public Guid SlipDocumentId { get; private set; }

    public string SlipScanSha256 { get; private set; } = string.Empty;

    public string RecordedBySubject { get; private set; } = string.Empty;

    public string RecordedByLanId { get; private set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; private set; }

    public string Status { get; private set; } = Recorded;

    /// <summary>The Partner Bank's order number, once it has placed the order.</summary>
    public string? PartnerOrderNumber { get; private set; }

    public string? StatusReason { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A slip is EWP's authority to debit the customer: only for the customer's OPEN Digital
    // │ Gold account; signed TODAY (it buys at today's price, never an open-ended order); the
    // │ phone number written on it must be the customer's mobile on record (the Partner Bank
    // │ checks the same); the paying account is one the Journey API found among the customer's
    // │ own accounts; the staff member confirms the signature.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static GoldPurchase Record(
        GoldAccountApplication account, decimal amount, string debtorBsb, string debtorAccountNumber,
        string slipNumber, DateOnly slipSignedOn, string? mobileOnSlip, bool signatureChecked,
        Guid slipDocumentId, string slipScanSha256, Actor actor, DateTimeOffset now)
    {
        if (!account.IsOpen)
            throw new DigitalGoldConflictException("The customer's Digital Gold account is not open yet: a transfer slip needs an open account.");
        if (slipSignedOn != DigitalGoldRules.SydneyToday(now))
            throw new DigitalGoldRuleViolationException("The transfer slip must be signed today: it buys at today's price.");
        if (DigitalGoldRules.AustralianMobile(mobileOnSlip) != account.Mobile)
            throw new DigitalGoldRuleViolationException("The phone number on the slip is not the customer's mobile on record.");
        if (!signatureChecked)
            throw new DigitalGoldRuleViolationException("Confirm that you checked the customer's signature on the slip.");
        if (slipDocumentId == Guid.Empty)
            throw new DigitalGoldRuleViolationException("The scan of the signed transfer slip is required.");

        return new GoldPurchase
        {
            Number = DigitalGoldRules.NewNumber("DGP", now),
            ApplicationId = account.Id,
            CustomerNumber = account.CustomerNumber,
            PartnerAccountNumber = account.PartnerAccountNumber!,
            AmountAud = DigitalGoldRules.Amount(amount),
            DebtorBsb = DigitalGoldRules.Bsb(debtorBsb),
            DebtorAccountNumber = DigitalGoldRules.AccountNumber(debtorAccountNumber),
            BranchCode = actor.BranchCode,
            SlipNumber = DigitalGoldRules.DocumentNumber(slipNumber, "transfer slip"),
            SlipSignedOn = slipSignedOn,
            SlipMobile = account.Mobile,
            SlipDocumentId = slipDocumentId,
            SlipScanSha256 = DigitalGoldRules.ScanSha256(slipScanSha256),
            RecordedBySubject = actor.Subject,
            RecordedByLanId = actor.LanId,
            RecordedAt = now,
            UpdatedAt = now,
            Status = Recorded
        };
    }
}