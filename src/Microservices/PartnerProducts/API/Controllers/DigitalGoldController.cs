using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using EnterpriseWebPlatform.PartnerProducts.Api.Authorization;
using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure;
using EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Controllers;

/// <summary>
/// EWP's records of a customer's Digital Gold at the Partner Bank: the signed Form VII (account
/// application) and the signed transfer authorization slips (purchases). Called only by the
/// Partner Products Journey API, acting for the staff member at the counter.
/// </summary>
[ApiController]
[Route("v1/partner-products")]
public sealed class DigitalGoldController(PartnerProductsDbContext db, TimeProvider clock, PartnerBankClient partnerBank) : ControllerBase
{
    public sealed record FormViiRequest(
        string? CustomerNumber, string? CustomerName, bool KycVerified, string? PhoneOnRecord,
        DateOnly DateOfBirth, bool IdentityChecked, string? FormNumber, DateOnly FormSignedOn, bool SignatureChecked,
        Guid FormDocumentId, string? FormScanSha256);

    public sealed record TransferSlipRequest(
        string? CustomerNumber, decimal Amount, string? DebtorBsb, string? DebtorAccountNumber,
        string? SlipNumber, DateOnly SlipSignedOn, string? MobileOnSlip, bool SignatureChecked,
        Guid SlipDocumentId, string? SlipScanSha256);

    public sealed record ApplicationView(
        string Number, string CustomerNumber, string CustomerName, string MobileEnding, string FormNumber, DateOnly FormSignedOn,
        string FormScanSha256, string Status, string? PartnerAccountNumber, string? StatusReason, string RecordedByLanId, DateTimeOffset RecordedAt);

    public sealed record PurchaseView(
        string Number, string CustomerNumber, string PartnerAccountNumber, decimal AmountAud, string DebtorBsb, string DebtorAccountNumber,
        string SlipNumber, DateOnly SlipSignedOn, string Status, string? PartnerOrderNumber, string? StatusReason, string RecordedByLanId, DateTimeOffset RecordedAt);

    public sealed record CustomerDigitalGold(string CustomerNumber, ApplicationView? Application, IReadOnlyList<PurchaseView> Purchases);

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Every read is limited to the staff member's OWN branch (from the token): another
    // │ branch's customer looks exactly like a customer without Digital Gold - no probing.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("customers/{customerNumber}/digital-gold")]
    [Authorize(Policy = DigitalGoldPolicies.View)]
    public async Task<ActionResult<CustomerDigitalGold>> ForCustomer(string customerNumber, CancellationToken cancellationToken)
    {
        var branch = ActorOf(User).BranchCode;
        var number = DigitalGoldRules.CustomerNumber(customerNumber);

        var application = await db.Applications.AsNoTracking()
            .Where(a => a.CustomerNumber == number && a.BranchCode == branch)
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var purchases = await db.Purchases.AsNoTracking()
            .Where(p => p.CustomerNumber == number && p.BranchCode == branch)
            .OrderByDescending(p => p.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        return new CustomerDigitalGold(number, application is null ? null : View(application), purchases.Select(View).ToList());
    }

    /// <summary>The branch's latest Form VII applications - the ones still to be sent first.</summary>
    [HttpGet("gold-account-applications")]
    [Authorize(Policy = DigitalGoldPolicies.View)]
    public async Task<ActionResult<IReadOnlyList<ApplicationView>>> Applications(CancellationToken cancellationToken)
    {
        var branch = ActorOf(User).BranchCode;
        var applications = await db.Applications.AsNoTracking()
            .Where(a => a.BranchCode == branch)
            .OrderBy(a => a.Status == GoldAccountApplication.Recorded ? 0 : 1)
            .ThenByDescending(a => a.Id)
            .Take(50)
            .ToListAsync(cancellationToken);
        return applications.Select(View).ToList();
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ One active application per customer and one per Form VII, enforced by the database's
    // │ unique indexes too (a double click or two counters at once still create ONE record);
    // │ the actor comes from the token.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [HttpPost("gold-account-applications")]
    [Authorize(Policy = DigitalGoldPolicies.Order)]
    public async Task<ActionResult<ApplicationView>> RecordFormVii(FormViiRequest request, CancellationToken cancellationToken)
    {
        var actor = ActorOf(User);
        var customerNumber = DigitalGoldRules.CustomerNumber(request.CustomerNumber);
        if (await db.Applications.AnyAsync(a => a.CustomerNumber == customerNumber && a.Status != GoldAccountApplication.Refused, cancellationToken))
            throw new DigitalGoldConflictException($"Customer {customerNumber} already has a Digital Gold application or account: a second Form VII is not needed.");

        var application = GoldAccountApplication.Record(
            customerNumber, request.CustomerName ?? string.Empty, request.KycVerified, request.PhoneOnRecord,
            request.DateOfBirth, request.IdentityChecked, request.FormNumber ?? string.Empty, request.FormSignedOn, request.SignatureChecked,
            request.FormDocumentId, request.FormScanSha256 ?? string.Empty, actor, clock.GetUtcNow());

        db.Applications.Add(application);
        await SaveOnceAsync($"Form VII {application.FormNumber} or this customer's application is already recorded.", cancellationToken);
        return Created($"/v1/partner-products/gold-account-applications/{application.Number}", View(application));
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Sending Form VII to the Partner Bank (EWP as distributor): SENT is saved first (with
    // │ the fixed instruction ID), then the signed instruction goes out over mutual TLS. The
    // │ answer is recorded: OPENED with the Partner Bank's account number, or REFUSED with its
    // │ reason. No answer leaves it SENT - "send again" re-sends the SAME instruction.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [HttpPost("gold-account-applications/{number}/send")]
    [Authorize(Policy = DigitalGoldPolicies.Order)]
    public async Task<ActionResult<ApplicationView>> SendFormVii(string number, CancellationToken cancellationToken)
    {
        var actor = ActorOf(User);
        var application = await db.Applications.FirstOrDefaultAsync(a => a.Number == number && a.BranchCode == actor.BranchCode, cancellationToken);
        if (application is null)
            return NotFound();

        var instructionId = application.PrepareToSend(actor.LanId, clock.GetUtcNow());
        await SaveOnceAsync($"Application {number} is being sent by someone else right now.", cancellationToken);

        var answer = await partnerBank.OpenAccountAsync(application.OpeningInstruction(), instructionId, cancellationToken);
        switch (answer.Outcome)
        {
            case PartnerOutcome.Accepted:
                application.MarkOpened((string?)answer.Body?["number"] ?? string.Empty, clock.GetUtcNow());
                break;
            case PartnerOutcome.Refused:
                application.MarkRefused(answer.Reason!, clock.GetUtcNow());
                break;
            default:
                throw new PartnerBankUnavailableException($"{answer.Reason} Form VII stays SENT: send it again later - the Partner Bank opens it once only.");
        }
        await SaveOnceAsync($"Application {number} changed meanwhile - reload it.", cancellationToken);
        return View(application);
    }

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The Partner Bank is the system of record of the gold: the holding is READ from it on
    // │ every look (grams, today's value, its own activity and notices), never copied here.
    // └──────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("gold-account-applications/{number}/holding")]
    [Authorize(Policy = DigitalGoldPolicies.View)]
    public async Task<IActionResult> Holding(string number, CancellationToken cancellationToken)
    {
        var branch = ActorOf(User).BranchCode;
        var application = await db.Applications.AsNoTracking().FirstOrDefaultAsync(a => a.Number == number && a.BranchCode == branch, cancellationToken);
        if (application is not { IsOpen: true })
            return NotFound();
        var holding = await partnerBank.HoldingAsync(application.PartnerAccountNumber!, cancellationToken)
            ?? throw new PartnerBankUnavailableException("The Partner Bank could not be reached for the holding. Try again shortly.");
        return Ok(holding);
    }

    [HttpPost("gold-purchases")]
    [Authorize(Policy = DigitalGoldPolicies.Order)]
    public async Task<ActionResult<PurchaseView>> RecordTransferSlip(TransferSlipRequest request, CancellationToken cancellationToken)
    {
        var actor = ActorOf(User);
        var customerNumber = DigitalGoldRules.CustomerNumber(request.CustomerNumber);
        var account = await db.Applications
            .Where(a => a.CustomerNumber == customerNumber && a.BranchCode == actor.BranchCode && a.Status != GoldAccountApplication.Refused)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new DigitalGoldConflictException("The customer has no Digital Gold account yet: record and send Form VII first.");

        var purchase = GoldPurchase.Record(
            account, request.Amount, request.DebtorBsb ?? string.Empty, request.DebtorAccountNumber ?? string.Empty,
            request.SlipNumber ?? string.Empty, request.SlipSignedOn, request.MobileOnSlip, request.SignatureChecked,
            request.SlipDocumentId, request.SlipScanSha256 ?? string.Empty, actor, clock.GetUtcNow());

        db.Purchases.Add(purchase);
        await SaveOnceAsync($"Transfer slip {purchase.SlipNumber} is already recorded.", cancellationToken);
        return Created($"/v1/partner-products/gold-purchases/{purchase.Number}", View(purchase));
    }

    // The unique indexes are the final word on "once only": a race or a double click loses here, as a 409.
    private async Task SaveOnceAsync(string conflict, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e is DbUpdateConcurrencyException || e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DigitalGoldConflictException(conflict);
        }
    }

    /// <summary>The staff member, from the token the policy already checked (sub, lan_id, branch).</summary>
    private static Actor ActorOf(ClaimsPrincipal user) =>
        new(user.FindFirstValue("sub")!, user.FindFirstValue("lan_id") ?? string.Empty, user.FindFirstValue("branch")!);

    // The mobile number is shown masked (last three digits): enough to compare with the slip.
    private static ApplicationView View(GoldAccountApplication a) => new(
        a.Number, a.CustomerNumber, a.CustomerName, a.Mobile[^3..], a.FormNumber, a.FormSignedOn, a.FormScanSha256,
        a.Status, a.PartnerAccountNumber, a.StatusReason, a.RecordedByLanId, a.RecordedAt);

    private static PurchaseView View(GoldPurchase p) => new(
        p.Number, p.CustomerNumber, p.PartnerAccountNumber, p.AmountAud, p.DebtorBsb, p.DebtorAccountNumber,
        p.SlipNumber, p.SlipSignedOn, p.Status, p.PartnerOrderNumber, p.StatusReason, p.RecordedByLanId, p.RecordedAt);
}