using System.ComponentModel.DataAnnotations;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Npgsql;

using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.AttachDocuments;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Controllers;

public sealed class AttachDocumentsRequest
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

    [MaxLength(100)]
    public List<Guid> DocumentIds { get; init; } = [];
}

/// <summary>
/// Machine-only endpoint through which the submission of an onboarding application
/// reaches Documents Management: its evidence documents become ATTACHED and are
/// retained (no longer deletable). Called only by the Document Invalidation Subscriber
/// with its pinned M2M identity. Idempotent per MessageId.
/// </summary>
[ApiController]
[Route("internal/v1/documents/attachments")]
public sealed class InternalDocumentAttachmentsController(
    AttachDocumentsCommandHandler handler,
    ILogger<InternalDocumentAttachmentsController> logger) : ControllerBase
{
    [HttpPost]
    [Authorize(Policy = "DocumentInvalidationSubscriberWrite")]
    public async Task<IActionResult> Attach(
        [FromBody] AttachDocumentsRequest request,
        CancellationToken cancellationToken)
    {
        if (request.MessageId == Guid.Empty || request.ApplicationRef == Guid.Empty)
            return ValidationProblem("MessageId and ApplicationRef are required.");

        if (!BranchCode.TryCreate(request.BranchCode, out var branch))
            return ValidationProblem("A valid BranchCode is required.");

        AttachDocumentsResult result;
        try
        {
            result = await handler.HandleAsync(
                new AttachDocumentsCommand(
                    request.MessageId,
                    request.ApplicationRef,
                    request.ApplicationNumber.Trim(),
                    branch,
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
            result = new AttachDocumentsResult(AttachDocumentsOutcome.Duplicate, 0, 0, 0, 0);
        }

        logger.LogInformation(
            "Attachment for application {ApplicationNumber} ({ApplicationRef}, MessageId={MessageId}): {Outcome}; attached {Attached}, unchanged {Unchanged}, not found {NotFound}, other branch {OtherBranch}.",
            request.ApplicationNumber, request.ApplicationRef, request.MessageId,
            result.Outcome, result.Attached, result.Unchanged, result.NotFound, result.OtherBranch);

        if (result.OtherBranch > 0)
            logger.LogWarning(
                "{Count} document(s) named by application {ApplicationNumber} belong to another branch and were NOT attached.",
                result.OtherBranch, request.ApplicationNumber);

        return Ok(new
        {
            messageId = request.MessageId,
            result = result.Outcome.ToString(),
            result.Attached,
            result.Unchanged,
            result.NotFound,
            result.OtherBranch
        });
    }
}