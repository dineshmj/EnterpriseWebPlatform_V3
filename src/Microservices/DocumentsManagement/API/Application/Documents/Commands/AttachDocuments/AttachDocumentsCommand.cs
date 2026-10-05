using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.AttachDocuments;

/// <summary>
/// An onboarding application was submitted with these evidence documents: attach them,
/// so they are retained and can no longer be deleted. Sent once per
/// onboarding.application.submitted message (<see cref="MessageId"/>).
/// </summary>
public sealed record AttachDocumentsCommand(
    Guid MessageId,
    Guid ApplicationRef,
    string ApplicationNumber,
    BranchCode Branch,
    IReadOnlyList<Guid> DocumentIds);

public enum AttachDocumentsOutcome
{
    Applied,
    Duplicate
}

/// <summary>What happened to each named document (all counted, none fails the message).</summary>
public sealed record AttachDocumentsResult(
    AttachDocumentsOutcome Outcome,
    int Attached,
    int Unchanged,
    int NotFound,
    int OtherBranch);

public sealed class AttachDocumentsCommandHandler(
    IDocumentRepository repository,
    IInboxStore inbox,
    TimeProvider clock)
{
    public const string InboxConsumer = "documents-management.attachment";

    public async Task<AttachDocumentsResult> HandleAsync(
        AttachDocumentsCommand command,
        CancellationToken cancellationToken)
    {
        if (await inbox.HasProcessedAsync(command.MessageId, InboxConsumer, cancellationToken))
            return new AttachDocumentsResult(AttachDocumentsOutcome.Duplicate, 0, 0, 0, 0);

        var now = clock.GetUtcNow();
        var attachedTo = $"Onboarding application {command.ApplicationNumber}";

        var ids = command.DocumentIds.Distinct().ToList();
        var documents = await repository.GetForUpdateAsync(ids, cancellationToken);

        int attached = 0, unchanged = 0, otherBranch = 0;
        foreach (var document in documents)
        {
            // Only documents of the application's own branch (defence in depth, as for invalidation).
            if (!document.BelongsTo(command.Branch))
            {
                otherBranch++;
                continue;
            }

            if (document.Attach(attachedTo, now))
                attached++;
            else
                unchanged++;
        }

        // Recorded even when nothing changed, so the message is never re-applied.
        inbox.RecordProcessed(command.MessageId, InboxConsumer, now);
        await repository.SaveChangesAsync(cancellationToken);

        return new AttachDocumentsResult(
            AttachDocumentsOutcome.Applied,
            attached,
            unchanged,
            NotFound: ids.Count - documents.Count,
            otherBranch);
    }
}