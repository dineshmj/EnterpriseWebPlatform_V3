using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding.Commands.RecordKycOutcome;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Controllers;

/// <summary>
/// Machine-only endpoints through which workflow facts from other bounded
/// contexts reach the onboarding application (saga choreography). Called by
/// the Onboarding Outcome Subscriber with its pinned M2M identity.
/// </summary>
[ApiController]
[Route("internal/v1/onboarding/applications")]
public sealed class InternalOnboardingApplicationsController(
    RecordKycOutcomeCommandHandler recordKycOutcomeHandler,
    ILogger<InternalOnboardingApplicationsController> logger) : ControllerBase
{
    /// <summary>
    /// Records a Customer KYC outcome (KycCaseCreated / KycCaseApproved /
    /// KycCaseRejected) on the application. Idempotent per KYC MessageId:
    /// a redelivered message returns 200 without changing anything.
    /// Workflow metadata comes in the X-Workflow-Id / X-Correlation-Id /
    /// X-Causation-Id / X-Initiated-By-User-Id headers.
    /// </summary>
    [HttpPost("{applicationRef:guid}/kyc-outcomes")]
    [Authorize(Policy = "OnboardingOutcomeSubscriberWrite")]
    public async Task<IActionResult> RecordKycOutcome(
        Guid applicationRef,
        [FromBody] RecordKycOutcomeRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MessageId == Guid.Empty
            || string.IsNullOrWhiteSpace(request.EventType)
            || string.IsNullOrWhiteSpace(request.ApplicationNumber))
        {
            return ValidationProblem("MessageId, EventType and ApplicationNumber are required.");
        }

        RecordKycOutcomeResult result;
        try
        {
            result = await recordKycOutcomeHandler.HandleAsync(
                new RecordKycOutcomeCommand(applicationRef, request.ApplicationNumber, request.MessageId, request.EventType),
                cancellationToken);
        }
        catch (DbUpdateException ex) when (IsInboxDuplicate(ex))
        {
            // Two deliveries of the same message raced; the unique Inbox key let
            // exactly one of them commit. This one is the duplicate.
            result = RecordKycOutcomeResult.Duplicate;
        }

        logger.LogInformation(
            "KYC outcome {EventType} (MessageId={MessageId}) for application {ApplicationRef}: {Result}.",
            request.EventType,
            request.MessageId,
            applicationRef,
            result);

        return result switch
        {
            RecordKycOutcomeResult.Applied or RecordKycOutcomeResult.NoChange or RecordKycOutcomeResult.Duplicate =>
                Ok(new { applicationRef, messageId = request.MessageId, result = result.ToString() }),
            RecordKycOutcomeResult.NotFound =>
                Problem(statusCode: StatusCodes.Status404NotFound, title: "Onboarding application not found"),
            RecordKycOutcomeResult.ApplicationMismatch =>
                Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Application number mismatch",
                    detail: $"Application {applicationRef} is not '{request.ApplicationNumber}'. The KYC fact is stale or misrouted."),
            _ =>
                Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: $"Unsupported KYC event type '{request.EventType}'")
        };
    }

    private static bool IsInboxDuplicate(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("uq_inbox_messages_message_consumer", StringComparison.Ordinal) == true;
}

public sealed record RecordKycOutcomeRequest(Guid MessageId, string EventType, string ApplicationNumber);