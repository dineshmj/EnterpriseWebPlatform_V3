using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

/// <summary>A payment's outcome, as much as a follow-up process needs to act on it.</summary>
public sealed record PaymentOutcomeView(
    string PaymentNumber, string Status, decimal Amount, string PayeeBsb, string PayeeAccountNumber, string? Reference, string? NetworkReference);

// ┌─ :: SECURITY :: Internal read pinned to one machine client ────────────────────────────────────
// │ Why : The Partner Products Worker (Temporal) follows a gold purchase's payment long after the
// │       staff member's token has expired - but a machine must not get the staff screens' reach.
// │ How : Only the worker's client_id with payments.read (policy PaymentOutcomeForPartnerProducts);
// │       one payment at a time, found by its Idempotency-Key (a random GUID only the purchase
// │       knows) - no lists, no search; the answer is the outcome only (no paying account). The
// │       Partner Products API checks the payee and amount again before it advises the Partner Bank.
// │ Ref : OWASP API1 BOLA, API3 (minimal data), API5 BFLA | Partner Products step 8
// └────────────────────────────────────────────────────────────────────────────────────────────────
[ApiController]
[Route("internal/v1/payments")]
public sealed class InternalPaymentStatusController(IPaymentRepository payments) : ControllerBase
{
    [HttpGet("by-idempotency-key/{paymentRef:guid}")]
    [Authorize(Policy = "PaymentOutcomeForPartnerProducts")]
    public async Task<ActionResult<PaymentOutcomeView>> ByIdempotencyKey(Guid paymentRef, CancellationToken cancellationToken)
    {
        if (paymentRef == Guid.Empty)
            return NotFound();
        var payment = await payments.GetByRefAsync(paymentRef, cancellationToken);
        return payment is null
            ? NotFound()
            : new PaymentOutcomeView(payment.PaymentNumber, payment.Status.ToCode(), payment.Amount, payment.To.Bsb, payment.To.AccountNumber,
                payment.Reference, payment.NetworkReference);
    }
}