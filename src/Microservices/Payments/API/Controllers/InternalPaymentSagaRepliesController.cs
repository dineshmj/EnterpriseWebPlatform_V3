using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Payments.Api.Application.Commands;
using EnterpriseWebPlatform.Payments.Api.Domain.Exceptions;

namespace EnterpriseWebPlatform.Payments.Api.Controllers;

/// <summary>A reply from Accounts as the Payments Saga Reply Subscriber hands it over.</summary>
public sealed record SagaReplyRequest(
    Guid MessageId,
    string? ReplyType,
    Guid PaymentRef,
    string? ReasonCode,
    string? Reason,
    bool? NothingWasHeld,
    Guid? CausationId = null);

// ┌─ :: SECURITY :: Internal endpoint pinned to one machine client ────────────────────────────────
// │ Why : Anyone holding the write scope could otherwise post a fake "funds reserved" reply and
// │       move a payment.
// │ How : Only the PaymentsSagaReplySubscriber's client_id is accepted (policy
// │       PaymentsSagaReplySubscriberWrite) - the scope alone grants nothing.
// │ Ref : Features 2.1.2 | OWASP API5 Broken Function Level Authorization |
// │       AI review 1 (client pinning)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// Machine-only endpoint: called by the PaymentsSagaReplySubscriber (pinned M2M client) for
/// each reply Accounts publishes on accounts.funds.replies. The saga decides what the reply
/// means; the HTTP answer only tells the courier that it was handled.
///  - 200: applied, ignored as late / duplicate, or already processed (Inbox);
///  - 400: malformed, or for a payment that does not exist (the courier dead-letters it).
/// </summary>
[ApiController]
[Route("internal/v1/payment-sagas/replies")]
public sealed class InternalPaymentSagaRepliesController(HandleSagaReplyCommandHandler handler) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "PaymentsSagaReplySubscriberWrite")]
    public async Task<IActionResult> Handle([FromBody] SagaReplyRequest request, CancellationToken cancellationToken)
    {
        // CausationId: the command the reply answers - the saga accepts only the answer to its current command.
        if (request.MessageId == Guid.Empty || request.PaymentRef == Guid.Empty || string.IsNullOrWhiteSpace(request.ReplyType) ||
            request.CausationId is not { } causationId || causationId == Guid.Empty)
        {
            return ValidationProblem("MessageId, CausationId, ReplyType and PaymentRef are required.");
        }

        try
        {
            var outcome = await handler.HandleAsync(
                new SagaReplyCommand(request.MessageId, causationId, request.ReplyType, request.PaymentRef, request.ReasonCode,
                    request.Reason, request.NothingWasHeld ?? false),
                cancellationToken);

            return Ok(new { outcome = outcome.ToString() });
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }
    }
}