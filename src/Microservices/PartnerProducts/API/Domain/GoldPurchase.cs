using System.Globalization;
using System.Text.Json.Nodes;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>
/// A Digital Gold purchase: the customer's transfer authorization slip (amount, phone number,
/// signature), signed at EWP's counter for an OPEN Digital Gold account. It is EWP's authority to
/// debit the customer's account and buy at that day's price; EWP records it (the scan in
/// Documents Management), then sends it to the Partner Bank, pays and sends the payment advice.
/// RECORDED -> SENT -> ORDERED -> PAID -> ALLOCATED, or REFUSED (the Partner Bank said no) /
/// CANCELLED (the payment was not made, so the Partner Bank's order was cancelled).
/// </summary>
public sealed class GoldPurchase : IRaisesDigitalGoldEvents
{
    public const string Recorded = "RECORDED";
    public const string Sent = "SENT";
    public const string Ordered = "ORDERED";
    public const string Paid = "PAID";
    public const string Allocated = "ALLOCATED";
    public const string Refused = "REFUSED";
    public const string Cancelled = "CANCELLED";

    private readonly List<DigitalGoldEvent> _events = [];

    private GoldPurchase()
    {
    }

    /// <summary>Raised since the last save; the DbContext writes them to the Outbox with the change.</summary>
    public IReadOnlyList<DigitalGoldEvent> Events => _events;

    public void ClearEvents() => _events.Clear();

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

    /// <summary>EWP's ID of the signed purchase instruction (its jti) - fixed at the first send, so a resend is never a second order.</summary>
    public Guid? InstructionId { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? SentByLanId { get; private set; }

    /// <summary>The Partner Bank's order number, once it has placed the order.</summary>
    public string? PartnerOrderNumber { get; private set; }

    /// <summary>The Partner Bank's quote: the price, the grams it buys and until when the price is locked.</summary>
    public decimal? PricePerGram { get; private set; }

    public decimal? Grams { get; private set; }

    public DateTimeOffset? QuoteExpiresAt { get; private set; }

    /// <summary>The payment's Idempotency-Key at EWP Payments: fixed when the slip is recorded, so the slip is paid once.</summary>
    public Guid PaymentRef { get; private set; }

    public string? PaymentNumber { get; private set; }

    public string? PaymentStartedByLanId { get; private set; }

    public DateTimeOffset? PaymentStartedAt { get; private set; }

    /// <summary>The payment network's reference of the payment to the Partner Bank - the payment advice carries it.</summary>
    public string? NetworkReference { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? AllocatedAt { get; private set; }

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

        var purchase = new GoldPurchase
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
            PaymentRef = Guid.NewGuid(),
            Status = Recorded
        };
        purchase.Raise(DigitalGoldEventTypes.PurchaseRecorded, now);
        return purchase;
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Sending: only a RECORDED slip, or a SENT one whose answer never came. The instruction
    // │ ID is fixed the first time: the Partner Bank places each instruction once, so sending
    // │ again after a lost answer is never a second order. SENT is saved BEFORE the call.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public Guid PrepareToSend(string lanId, DateTimeOffset now)
    {
        if (Status is not (Recorded or Sent))
            throw new DigitalGoldConflictException($"Purchase {Number} is {Status}: there is nothing to send.");
        if (SlipSignedOn != DigitalGoldRules.SydneyToday(now))
            throw new DigitalGoldConflictException($"Transfer slip {SlipNumber} was signed on {SlipSignedOn:yyyy-MM-dd}: it buys at that day's price only. The customer signs a new slip.");
        var resend = Status == Sent;
        InstructionId ??= Guid.NewGuid();
        SentAt ??= now;
        SentByLanId = lanId;
        Status = Sent;
        Touch(now);
        _events.Add(new DigitalGoldEvent(DigitalGoldEventTypes.PurchaseSent, now, Resend: resend));
        return InstructionId.Value;
    }

    public void MarkOrdered(string partnerOrderNumber, decimal? pricePerGram, decimal? grams, DateTimeOffset? quoteExpiresAt, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(partnerOrderNumber) || partnerOrderNumber.Length > 30)
            throw new DigitalGoldConflictException("The Partner Bank's answer carried no order number.");
        PartnerOrderNumber = partnerOrderNumber;
        PricePerGram = pricePerGram;
        Grams = grams;
        QuoteExpiresAt = quoteExpiresAt;
        StatusReason = null;
        Status = Ordered;
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchaseOrdered, now);
    }

    public void MarkRefused(string reason, DateTimeOffset now)
    {
        StatusReason = Shorten(reason);
        Status = Refused;
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchaseRefused, now, StatusReason);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The payment pays EXACTLY the slip: the customer's account on the slip, the slip's
    // │ amount, to the Partner Bank's settlement account (configuration - never typed at the
    // │ counter), with the Partner Bank's order number as the reference and the slip's fixed
    // │ Idempotency-Key - paying "again" is always the same one payment.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public PurchasePayment PaymentInstruction(SettlementAccount settlement)
    {
        if (Status != Ordered)
            throw new DigitalGoldConflictException($"Purchase {Number} is {Status}: only an order the Partner Bank placed is paid.");
        return new PurchasePayment(PaymentRef, CustomerNumber, DebtorBsb, DebtorAccountNumber,
            settlement.PayeeName, settlement.Bsb, settlement.AccountNumber, AmountAud, PartnerOrderNumber!);
    }

    /// <summary>The payment EWP Payments started for this order (its number, for the screen and the trail); the same number again changes nothing.</summary>
    public void RecordPaymentStarted(string paymentNumber, string lanId, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(paymentNumber) || paymentNumber.Length > 40)
            throw new DigitalGoldRuleViolationException("The payment number is required.");
        if (PaymentNumber == paymentNumber)
            return;
        if (PaymentNumber is not null || Status != Ordered)
            throw new DigitalGoldConflictException($"Purchase {Number} is {Status} with payment {PaymentNumber ?? "-"}: no other payment can be recorded.");
        PaymentNumber = paymentNumber;
        PaymentStartedByLanId = lanId;
        PaymentStartedAt = now;
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchasePaymentStarted, now);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ PAID only for the payment this purchase started (its Idempotency-Key), completed, for
    // │ the exact amount, to the settlement account. PAID is saved before the payment advice
    // │ goes out: a lost answer leaves PAID, and the advice is sent again - never a refund
    // │ question about money that did leave the customer's account.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public void MarkPaid(Guid paymentRef, string networkReference, decimal amount, string payeeBsb, string payeeAccountNumber,
        SettlementAccount settlement, DateTimeOffset now)
    {
        if (Status == Paid && NetworkReference == networkReference)
            return;
        if (Status != Ordered)
            throw new DigitalGoldConflictException($"Purchase {Number} is {Status}: it does not await a payment.");
        if (paymentRef != PaymentRef || amount != AmountAud || payeeBsb != settlement.Bsb || payeeAccountNumber != settlement.AccountNumber)
            throw new DigitalGoldRuleViolationException($"The payment is not purchase {Number}'s payment (key, amount or payee differ).");
        if (string.IsNullOrWhiteSpace(networkReference) || networkReference.Length > 64)
            throw new DigitalGoldRuleViolationException("The payment network's reference is required.");
        NetworkReference = networkReference;
        PaidAt = now;
        StatusReason = null;
        Status = Paid;
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchasePaid, now);
    }

    public void MarkAllocated(decimal? pricePerGram, decimal? grams, DateTimeOffset now)
    {
        if (Status == Allocated)
            return;
        if (Status != Paid)
            throw new DigitalGoldConflictException($"Purchase {Number} is {Status}: only a paid purchase is allocated.");
        PricePerGram = pricePerGram ?? PricePerGram;
        Grams = grams ?? Grams;
        AllocatedAt = now;
        StatusReason = null;
        Status = Allocated;
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchaseAllocated, now);
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Cancelled only while unpaid (ORDERED): once money left the customer's account, the
    // │ purchase is completed with the payment advice, never cancelled.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public void EnsureCancellable()
    {
        if (Status is not (Ordered or Cancelled))
            throw new DigitalGoldConflictException($"Purchase {Number} is {Status}: only an unpaid order is cancelled.");
    }

    public void MarkCancelled(string reason, DateTimeOffset now)
    {
        EnsureCancellable();
        if (Status == Cancelled)
            return;
        StatusReason = Shorten(reason);
        Status = Cancelled;
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchaseCancelled, now, StatusReason);
    }

    /// <summary>Something needs a person (e.g. the Partner Bank refused a payment advice): the reason is kept, the status stays.</summary>
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ A problem only a person can settle (paid but the advice refused; the cancellation
    // │ refused) is an event of its own: it reaches the audit trail, not just this row.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public void NoteProblem(string reason, DateTimeOffset now)
    {
        StatusReason = Shorten(reason);
        Touch(now);
        Raise(DigitalGoldEventTypes.PurchaseNeedsAttention, now, StatusReason);
    }

    /// <summary>
    /// The Partner Bank's digital_gold_purchase instruction: exactly the fields its allow-list
    /// accepts (Partner Bank repo, Distribution/Instructions.cs), from EWP's record of the slip.
    /// </summary>
    public JsonObject PurchaseInstruction() => new()
    {
        ["type"] = "digital_gold_purchase",
        ["goldAccountNumber"] = PartnerAccountNumber,
        ["customer"] = new JsonObject { ["ewpCustomerNumber"] = CustomerNumber },
        ["instructedAmount"] = new JsonObject { ["currency"] = "AUD", ["amount"] = AmountAud.ToString("0.00", CultureInfo.InvariantCulture) },
        ["debtorAccount"] = new JsonObject { ["bsb"] = DebtorBsb, ["accountNumber"] = DebtorAccountNumber },
        ["slip"] = new JsonObject
        {
            ["number"] = SlipNumber,
            ["signedOn"] = SlipSignedOn.ToString("yyyy-MM-dd"),
            ["mobile"] = SlipMobile,
            ["scanSha256"] = SlipScanSha256
        },
        ["actor"] = new JsonObject { ["lanId"] = RecordedByLanId, ["branch"] = BranchCode }
    };

    private void Raise(string eventType, DateTimeOffset now, string? reason = null) =>
        _events.Add(new DigitalGoldEvent(eventType, now, reason));

    private static string Shorten(string reason) => reason.Length > 500 ? reason[..500] : reason;

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}

/// <summary>The Partner Bank's account that receives the purchases' payments (configuration "PartnerBank:Settlement").</summary>
public sealed record SettlementAccount(string PayeeName, string Bsb, string AccountNumber);

/// <summary>Exactly what EWP Payments is asked to pay for a purchase.</summary>
public sealed record PurchasePayment(
    Guid IdempotencyKey, string CustomerNumber, string FromBsb, string FromAccountNumber,
    string PayeeName, string ToBsb, string ToAccountNumber, decimal Amount, string Reference);