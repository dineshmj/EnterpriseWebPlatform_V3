using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using EnterpriseWebPlatform.Accounts.Api.Application.Commands;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Accounts.Api.Controllers;

/// <summary>A funds command as the Accounts Command Subscriber hands it over (from accounts.commands).</summary>
public sealed record FundsCommandRequest(
    string? CommandType,
    Guid MessageId,
    Guid PaymentRef,
    string? PaymentNumber,
    string? CustomerNumber,
    string? Bsb,
    string? AccountNumber,
    decimal Amount,
    string? Currency,
    string? InitiatedByUserId,
    Guid? WorkflowId,
    Guid? CorrelationId);

/// <summary>
/// Machine-only endpoint: called by the AccountsCommandSubscriber (pinned M2M client) for
/// each command the Payments saga orchestrator sends. Accounts applies it to its own data
/// and replies through its Outbox (accounts.funds.replies); the HTTP answer only tells the
/// courier that the command was handled.
///  - 200: applied, or a repeat (Inbox / same PaymentRef);
///  - 400: the command is malformed (the courier dead-letters it);
///  - 409: the command contradicts the hold (e.g. settle what was released) - dead-lettered.
/// </summary>
[ApiController]
[Route("internal/v1/accounts/funds/commands")]
public sealed class InternalFundsCommandsController(FundsCommandHandler handler) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "AccountsCommandSubscriberWrite")]
    public async Task<IActionResult> Apply([FromBody] FundsCommandRequest request, CancellationToken cancellationToken)
    {
        FundsCommandType type;
        switch (request.CommandType)
        {
            case "ReserveFunds": type = FundsCommandType.Reserve; break;
            case "SettleFunds": type = FundsCommandType.Settle; break;
            case "ReleaseFunds": type = FundsCommandType.Release; break;
            default: return ValidationProblem($"Unknown funds command '{request.CommandType}'.");
        }

        if (request.MessageId == Guid.Empty || request.PaymentRef == Guid.Empty)
            return ValidationProblem("MessageId and PaymentRef are required.");
        if (string.IsNullOrWhiteSpace(request.PaymentNumber) || string.IsNullOrWhiteSpace(request.CustomerNumber) ||
            string.IsNullOrWhiteSpace(request.Bsb) || string.IsNullOrWhiteSpace(request.AccountNumber))
        {
            return ValidationProblem("PaymentNumber, CustomerNumber, Bsb and AccountNumber are required.");
        }

        try
        {
            var result = await handler.HandleAsync(
                new FundsCommand(
                    type, request.MessageId, request.PaymentRef, request.PaymentNumber, request.CustomerNumber,
                    request.Bsb, request.AccountNumber,
                    type == FundsCommandType.Reserve ? Money.ValidAmount(request.Amount) : request.Amount,
                    Money.ValidCurrency(request.Currency ?? Money.Aud),
                    request.InitiatedByUserId, request.WorkflowId, request.CorrelationId),
                cancellationToken);

            return Ok(new { holdStatus = result.HoldStatus, alreadyProcessed = result.AlreadyProcessed });
        }
        catch (DomainRuleViolationException ex)
        {
            return ValidationProblem(ex.Message);
        }
        catch (DomainConflictException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status409Conflict, title: "Funds command conflicts with the hold");
        }
    }
}