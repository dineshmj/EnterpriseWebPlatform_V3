using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.InvalidateDocuments;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Controllers;

public sealed class InvalidateDocumentsRequest
{
    [Required]
    public Guid MessageId { get; init; }

    [Required]
    public Guid ApplicationRef { get; init; }

    [Required]
    [MaxLength(30)]
    public string ApplicationNumber { get; init; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string BranchCode { get; init; } = string.Empty;

    [Required]
    [MaxLength(30)]
    public string RejectedBy { get; init; } = string.Empty;

    [MaxLength(100)]
    public List<Guid> DocumentIds { get; init; } = [];
}

/// <summary>
/// Machine-only endpoint through which a business rejection of an onboarding
/// application reaches Documents Management (saga compensation). Called only by the
/// Document Invalidation Subscriber with its pinned M2M identity. Idempotent per
/// MessageId; documents are invalidated and retained, never deleted.
/// </summary>
[ApiController]
[Route("internal/v1/documents/invalidations")]
public sealed class InternalDocumentInvalidationsController(
    InvalidateDocumentsCommandHandler handler,
    ILogger<InternalDocumentInvalidationsController> logger) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "DocumentInvalidationSubscriberWrite")]
    public async Task<IActionResult> Invalidate(
        [FromBody] InvalidateDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MessageId == Guid.Empty || request.ApplicationRef == Guid.Empty)
            return ValidationProblem("MessageId and ApplicationRef are required.");

        if (!BranchCode.TryCreate(request.BranchCode, out var branch))
            return ValidationProblem("A valid BranchCode is required.");

        InvalidateDocumentsResult result;
        try
        {
            result = await handler.HandleAsync(
                new InvalidateDocumentsCommand(
                    request.MessageId,
                    request.ApplicationRef,
                    request.ApplicationNumber.Trim(),
                    branch,
                    request.RejectedBy.Trim(),
                    request.DocumentIds),
                cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A document changed meanwhile: transient, the subscriber retries in place.
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Concurrent change; retry.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries of the same message raced; the unique Inbox key let one commit.
            result = new InvalidateDocumentsResult(InvalidateDocumentsOutcome.Duplicate, 0, 0, 0, 0);
        }

        logger.LogInformation(
            "Invalidation for application {ApplicationNumber} ({ApplicationRef}, rejected by {RejectedBy}, MessageId={MessageId}): {Outcome}; invalidated {Invalidated}, already {Already}, not found {NotFound}, other branch {OtherBranch}.",
            request.ApplicationNumber, request.ApplicationRef, request.RejectedBy, request.MessageId,
            result.Outcome, result.Invalidated, result.AlreadyInvalidated, result.NotFound, result.OtherBranch);

        if (result.OtherBranch > 0)
            logger.LogWarning(
                "{Count} document(s) named by application {ApplicationNumber} belong to another branch and were NOT invalidated.",
                result.OtherBranch, request.ApplicationNumber);

        return Ok(new
        {
            messageId = request.MessageId,
            result = result.Outcome.ToString(),
            result.Invalidated,
            result.AlreadyInvalidated,
            result.NotFound,
            result.OtherBranch
        });
    }
}