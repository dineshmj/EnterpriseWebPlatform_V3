using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.InvalidateDocuments;

/// <summary>
/// Compensation for a rejected onboarding application: invalidate (retain, never
/// delete) the evidence documents the application named. Sent once per
/// onboarding.application.rejected message (<see cref="MessageId"/>).
/// </summary>
public sealed record InvalidateDocumentsCommand(
    Guid MessageId,
    Guid ApplicationRef,
    string ApplicationNumber,
    BranchCode Branch,
    string RejectedBy,
    IReadOnlyList<Guid> DocumentIds);

public enum InvalidateDocumentsOutcome
{
    Applied,
    Duplicate
}

/// <summary>What happened to each named document (all counted, none fails the message).</summary>
public sealed record InvalidateDocumentsResult(
    InvalidateDocumentsOutcome Outcome,
    int Invalidated,
    int AlreadyInvalidated,
    int NotFound,
    int OtherBranch);

public sealed class InvalidateDocumentsCommandHandler(
    IDocumentRepository repository,
    IInboxStore inbox,
    TimeProvider clock)
{
    public const string InboxConsumer = "documents-management.invalidation";

    public async Task<InvalidateDocumentsResult> HandleAsync(
        InvalidateDocumentsCommand command,
        CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(command.MessageId, InboxConsumer, cancellationToken))
            return new InvalidateDocumentsResult(InvalidateDocumentsOutcome.Duplicate, 0, 0, 0, 0);

        var now = clock.GetUtcNow();
        var reason = $"Onboarding application {command.ApplicationNumber} ({command.ApplicationRef}) was rejected by {command.RejectedBy}.";

        var ids = command.DocumentIds.Distinct().ToList();
        var documents = await repository.GetForUpdateAsync(ids, cancellationToken);

        int invalidated = 0, alreadyInvalidated = 0, otherBranch = 0;
        foreach (var document in documents)
        {
            // An event may only compensate documents of its own branch (defence in depth:
            // the event is trusted, but never to reach across organisational scope).
            if (!document.BelongsTo(command.Branch))
            {
                otherBranch++;
                continue;
            }

            if (document.Invalidate(reason, now))
                invalidated++;
            else
                alreadyInvalidated++;
        }

        // Recorded even when nothing changed, so the message is never re-applied.
        inbox.RecordProcessed(command.MessageId, InboxConsumer, now);
        await repository.SaveChangesAsync(cancellationToken);

        return new InvalidateDocumentsResult(
            InvalidateDocumentsOutcome.Applied,
            invalidated,
            alreadyInvalidated,
            NotFound: ids.Count - documents.Count,
            otherBranch);
    }
}