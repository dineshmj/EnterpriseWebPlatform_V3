using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.PartnerProducts.Api.Authorization;
using EnterpriseWebPlatform.PartnerProducts.Api.Domain;
using EnterpriseWebPlatform.PartnerProducts.Api.Infrastructure;
using EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Controllers;

// ┌─ :: ARCHITECTURE + SECURITY :: The purchase workflow's follow-up (machine only) ───────────────
// │ Why : A purchase's payment may take minutes or hours (an approval, a slow network); nobody
// │       waits at the counter. The Temporal workflow (Partner Products Worker) follows it and then
// │       completes the purchase - the payment advice - or cancels the unpaid order.
// │ How : Internal endpoints for the worker's pinned machine client only (policy DigitalGoldWorker);
// │       PAID is saved BEFORE the advice goes out, CANCELLED only while unpaid; the Partner Bank's
// │       "no answer" is a 503 the workflow retries (the advice allocates once, the cancel is final
// │       once); a refused advice is kept for a person (status stays PAID, the reason is shown).
// │ Ref : Partner Products step 8 | Partner Bank repo doc/Architecture.md D11, D14 | OWASP API5
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("internal/v1/gold-purchases")]
[Authorize(Policy = DigitalGoldPolicies.Worker)]
public sealed class InternalGoldPurchasesController(
    PartnerProductsDbContext db, TimeProvider clock, PartnerBankClient partnerBank, SettlementAccount settlement) : ControllerBase
{
    public sealed record PurchaseState(string Number, string Status, decimal AmountAud, Guid PaymentRef, string? PartnerOrderNumber, string? PaymentNumber);

    public sealed record PaymentAdviceRequest(
        Guid PaymentRef, string? NetworkReference, decimal Amount, string? PayeeBsb, string? PayeeAccountNumber);

    public sealed record CancelRequest(string? Reason);

    [HttpGet("{number}")]
    public async Task<ActionResult<PurchaseState>> State(string number, CancellationToken cancellationToken)
    {
        var purchase = await db.Purchases.AsNoTracking().FirstOrDefaultAsync(p => p.Number == number, cancellationToken);
        if (purchase is null)
            return NotFound();
        return StateOf(purchase);
    }

    [HttpPost("{number}/payment-advice")]
    public async Task<ActionResult<PurchaseState>> PaymentAdvice(string number, PaymentAdviceRequest request, CancellationToken cancellationToken)
    {
        var purchase = await db.Purchases.FirstOrDefaultAsync(p => p.Number == number, cancellationToken);
        if (purchase is null)
            return NotFound();
        if (purchase.Status == GoldPurchase.Allocated)
            return StateOf(purchase);

        purchase.MarkPaid(request.PaymentRef, request.NetworkReference ?? string.Empty, request.Amount,
            request.PayeeBsb ?? string.Empty, request.PayeeAccountNumber ?? string.Empty, settlement, clock.GetUtcNow());
        await SaveAsync(cancellationToken);

        var answer = await partnerBank.PaymentAdviceAsync(purchase.PartnerOrderNumber!, purchase.NetworkReference!, purchase.AmountAud, cancellationToken);
        switch (answer.Outcome)
        {
            case PartnerOutcome.Accepted:
                purchase.MarkAllocated(Decimal(answer, "pricePerGram"), Decimal(answer, "grams"), clock.GetUtcNow());
                break;
            case PartnerOutcome.Refused:
                // Paid, but the Partner Bank will not allocate: a person must settle it with the Partner Bank.
                purchase.NoteProblem($"Paid, but the Partner Bank refused the payment advice: {answer.Reason}", clock.GetUtcNow());
                await SaveAsync(cancellationToken);
                throw new DigitalGoldConflictException(purchase.StatusReason!);
            default:
                throw new PartnerBankUnavailableException($"{answer.Reason} The purchase stays PAID: the advice is sent again.");
        }
        await SaveAsync(cancellationToken);
        return StateOf(purchase);
    }

    [HttpPost("{number}/cancel")]
    public async Task<ActionResult<PurchaseState>> Cancel(string number, CancelRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 200)
            throw new DigitalGoldRuleViolationException("A reason (1-200 characters) is required.");

        var purchase = await db.Purchases.FirstOrDefaultAsync(p => p.Number == number, cancellationToken);
        if (purchase is null)
            return NotFound();
        purchase.EnsureCancellable();
        if (purchase.Status == GoldPurchase.Cancelled)
            return StateOf(purchase);

        var answer = await partnerBank.CancelOrderAsync(purchase.PartnerOrderNumber!, reason, cancellationToken);
        switch (answer.Outcome)
        {
            case PartnerOutcome.Accepted:
                purchase.MarkCancelled(reason, clock.GetUtcNow());
                break;
            case PartnerOutcome.Refused:
                purchase.NoteProblem($"The Partner Bank refused to cancel order {purchase.PartnerOrderNumber}: {answer.Reason}", clock.GetUtcNow());
                await SaveAsync(cancellationToken);
                throw new DigitalGoldConflictException(purchase.StatusReason!);
            default:
                throw new PartnerBankUnavailableException($"{answer.Reason} The order stays open: the cancellation is sent again.");
        }
        await SaveAsync(cancellationToken);
        return StateOf(purchase);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone changed the purchase meanwhile: the workflow's activity retries with a fresh read.
            throw new PartnerBankUnavailableException("The purchase changed meanwhile - try again.");
        }
    }

    private static decimal? Decimal(PartnerAnswer answer, string name) =>
        answer.Body?[name] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<decimal>(out var number) ? number : null;

    private static PurchaseState StateOf(GoldPurchase p) => new(p.Number, p.Status, p.AmountAud, p.PaymentRef, p.PartnerOrderNumber, p.PaymentNumber);
}