using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Abstractions;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

public sealed record PayeeConfirmationRequest(string? Bsb, string? AccountNumber, string? AccountName);

/// <summary>
/// Checks the payment screen makes while the payee is typed in, answered by the payment
/// network: which bank and branch a BSB belongs to, and Confirmation of Payee (does the
/// name match the account?). They help the staff member catch mistakes and scams before
/// sending; they decide nothing - the payment itself is checked again when it is sent.
/// </summary>
[ApiController]
[Route("v1/payments")]
[Authorize(Policy = "PaymentInitiate")]
public sealed class PaymentLookupsController(IPaymentNetwork network, ILogger<PaymentLookupsController> logger) : ControllerBase
{
    [HttpGet("bsb/{bsb}")]
    public async Task<IActionResult> LookupBsb(string bsb, CancellationToken cancellationToken)
    {
        BankAccountRef reference;
        try
        {
            reference = BankAccountRef.Create(bsb, "00000", "payee");
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }

        try
        {
            var info = await network.LookupBsbAsync(reference.Bsb, cancellationToken);
            return info is null
                ? NotFound(new { message = $"BSB {reference.Bsb} is not in the BSB directory." })
                : Ok(info);
        }
        catch (PaymentNetworkUnavailableException ex)
        {
            return Unavailable(ex);
        }
    }

    [HttpPost("payee-confirmations")]
    public async Task<IActionResult> ConfirmPayee([FromBody] PayeeConfirmationRequest request, CancellationToken cancellationToken)
    {
        BankAccountRef payee;
        string name;
        try
        {
            payee = BankAccountRef.Create(request.Bsb, request.AccountNumber, "payee");
            name = PaymentRules.ValidPayeeName(request.AccountName);
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }

        try
        {
            return Ok(await network.ConfirmPayeeAsync(payee.Bsb, payee.AccountNumber, name, cancellationToken));
        }
        catch (PaymentNetworkUnavailableException ex)
        {
            return Unavailable(ex);
        }
    }

    private ObjectResult Unavailable(PaymentNetworkUnavailableException ex)
    {
        logger.LogWarning(ex, "Payment network lookup unavailable.");
        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { message = "The payment network cannot answer right now. Try again shortly." });
    }
}