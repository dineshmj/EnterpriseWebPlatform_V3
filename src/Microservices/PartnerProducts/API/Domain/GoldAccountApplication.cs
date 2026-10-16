using System.Text.Json.Nodes;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Domain;

/// <summary>
/// A customer's application for a Digital Gold account at the Partner Bank: Form VII, signed at
/// EWP's counter. EWP records it (with the scan in Documents Management), then sends it to the
/// Partner Bank as a signed instruction; the Partner Bank opens the account and is its system of
/// record. RECORDED -> SENT -> OPENED, or REFUSED.
/// </summary>
public sealed class GoldAccountApplication
{
    public const string Recorded = "RECORDED";
    public const string Sent = "SENT";
    public const string Opened = "OPENED";
    public const string Refused = "REFUSED";

    private GoldAccountApplication()
    {
    }

    public long Id { get; private set; }

    /// <summary>EWP's reference, e.g. DGA-261016-7K3QZ2.</summary>
    public string Number { get; private set; } = string.Empty;

    public string CustomerNumber { get; private set; } = string.Empty;

    public string CustomerName { get; private set; } = string.Empty;

    public DateOnly DateOfBirth { get; private set; }

    /// <summary>The customer's mobile in EWP's records, as the Partner Bank needs it (+614........).</summary>
    public string Mobile { get; private set; } = string.Empty;

    public string BranchCode { get; private set; } = string.Empty;

    public string FormNumber { get; private set; } = string.Empty;

    public DateOnly FormSignedOn { get; private set; }

    /// <summary>The scan of the signed form in Documents Management, and its SHA-256 - the evidence the Partner Bank relies on.</summary>
    public Guid FormDocumentId { get; private set; }

    public string FormScanSha256 { get; private set; } = string.Empty;

    public string RecordedBySubject { get; private set; } = string.Empty;

    public string RecordedByLanId { get; private set; } = string.Empty;

    public DateTimeOffset RecordedAt { get; private set; }

    public string Status { get; private set; } = Recorded;

    /// <summary>The Partner Bank's Digital Gold account number, once it is open.</summary>
    public string? PartnerAccountNumber { get; private set; }

    public string? StatusReason { get; private set; }

    /// <summary>EWP's ID of the signed instruction (its jti) - fixed at the first send, so a resend is never a second account.</summary>
    public Guid? InstructionId { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public string? SentByLanId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public long Version { get; private set; }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ The customer's facts (name, mobile, KYC status) come from EWP's own customer record,
    // │ read FOR the staff member (their branch) by the Journey API - never typed. What the
    // │ staff member confirms is that they checked the date of birth against the customer's
    // │ photo ID (EWP's record holds no date of birth) and the signature against the record.
    // │ No KYC-verified customer, no adult, no Australian mobile, no checks: no application.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static GoldAccountApplication Record(
        string customerNumber, string customerName, bool kycVerified, string? phoneOnRecord,
        DateOnly dateOfBirth, bool identityChecked, string formNumber, DateOnly formSignedOn, bool signatureChecked,
        Guid formDocumentId, string formScanSha256, Actor actor, DateTimeOffset now)
    {
        var today = DigitalGoldRules.SydneyToday(now);
        if (!kycVerified)
            throw new DigitalGoldRuleViolationException("Only a customer whose onboarding (KYC) is complete can apply - the Partner Bank relies on EWP's identification.");
        if (string.IsNullOrWhiteSpace(customerName) || customerName.Length > 140)
            throw new DigitalGoldRuleViolationException("The customer's name is missing from EWP's record.");
        var mobile = DigitalGoldRules.AustralianMobile(phoneOnRecord)
            ?? throw new DigitalGoldRuleViolationException("The customer's phone number in EWP's records is not an Australian mobile: correct the customer's record first (the Partner Bank sends its notices there).");
        if (dateOfBirth > today.AddYears(-DigitalGoldRules.MinimumAge) || dateOfBirth < today.AddYears(-120))
            throw new DigitalGoldRuleViolationException($"The customer must be at least {DigitalGoldRules.MinimumAge} years old (check the date of birth on Form VII).");
        if (!identityChecked)
            throw new DigitalGoldRuleViolationException("Confirm that you checked the date of birth against the customer's photo ID.");
        if (!signatureChecked)
            throw new DigitalGoldRuleViolationException("Confirm that you checked the customer's signature on Form VII.");
        if (formSignedOn > today || formSignedOn < today.AddDays(-DigitalGoldRules.FormValidDays))
            throw new DigitalGoldRuleViolationException($"Form VII must have been signed within the last {DigitalGoldRules.FormValidDays} days, and not in the future.");
        if (formDocumentId == Guid.Empty)
            throw new DigitalGoldRuleViolationException("The scan of the signed Form VII is required.");

        return new GoldAccountApplication
        {
            Number = DigitalGoldRules.NewNumber("DGA", now),
            CustomerNumber = DigitalGoldRules.CustomerNumber(customerNumber),
            CustomerName = customerName.Trim(),
            DateOfBirth = dateOfBirth,
            Mobile = mobile,
            BranchCode = actor.BranchCode,
            FormNumber = DigitalGoldRules.DocumentNumber(formNumber, "Form VII"),
            FormSignedOn = formSignedOn,
            FormDocumentId = formDocumentId,
            FormScanSha256 = DigitalGoldRules.ScanSha256(formScanSha256),
            RecordedBySubject = actor.Subject,
            RecordedByLanId = actor.LanId,
            RecordedAt = now,
            UpdatedAt = now,
            Status = Recorded
        };
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Sending: only a RECORDED application, or a SENT one whose answer never came. The
    // │ instruction ID is fixed the first time and never changes: the Partner Bank does each
    // │ instruction once, so sending again after a lost answer can never open a second account.
    // │ SENT is saved BEFORE the call - a crash in between leaves "SENT, send again", never a
    // │ silent RECORDED that hides an account the Partner Bank may already have opened.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public Guid PrepareToSend(string lanId, DateTimeOffset now)
    {
        if (Status is not (Recorded or Sent))
            throw new DigitalGoldConflictException($"Application {Number} is {Status}: there is nothing to send.");
        InstructionId ??= Guid.NewGuid();
        SentAt ??= now;
        SentByLanId = lanId;
        Status = Sent;
        Touch(now);
        return InstructionId.Value;
    }

    public void MarkOpened(string partnerAccountNumber, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(partnerAccountNumber) || partnerAccountNumber.Length > 20)
            throw new DigitalGoldConflictException("The Partner Bank's answer carried no account number.");
        PartnerAccountNumber = partnerAccountNumber;
        StatusReason = null;
        Status = Opened;
        Touch(now);
    }

    public void MarkRefused(string reason, DateTimeOffset now)
    {
        StatusReason = reason.Length > 500 ? reason[..500] : reason;
        Status = Refused;
        Touch(now);
    }

    /// <summary>
    /// The Partner Bank's digital_gold_account_opening instruction: exactly the fields its allow-list
    /// accepts (Partner Bank repo, Distribution/Instructions.cs), from EWP's record of Form VII.
    /// </summary>
    public JsonObject OpeningInstruction() => new()
    {
        ["type"] = "digital_gold_account_opening",
        ["customer"] = new JsonObject
        {
            ["ewpCustomerNumber"] = CustomerNumber,
            ["name"] = CustomerName,
            ["dateOfBirth"] = DateOfBirth.ToString("yyyy-MM-dd"),
            ["mobile"] = Mobile,
            ["kycVerified"] = true
        },
        ["form"] = new JsonObject
        {
            ["number"] = FormNumber,
            ["signedOn"] = FormSignedOn.ToString("yyyy-MM-dd"),
            ["scanSha256"] = FormScanSha256
        },
        ["actor"] = new JsonObject { ["lanId"] = RecordedByLanId, ["branch"] = BranchCode }
    };

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }

    /// <summary>Open: the customer may buy gold through it (a transfer slip needs an open account).</summary>
    public bool IsOpen => Status == Opened && PartnerAccountNumber is not null;

    /// <summary>Still counts as the customer's application (a refused one may be followed by a new Form VII).</summary>
    public bool IsActive => Status != Refused;
}