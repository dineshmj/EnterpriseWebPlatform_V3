using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.DocumentsManagement.Application.Abstractions.Storage;

namespace EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Commands.ErasePersonalData;

/// <summary>Customer Onboarding erased the customer's personal data: their documents go too.</summary>
public sealed record ErasePersonalDataCommand(string CustomerNumber, Guid CausationId);

/// <summary>How many of the customer's documents were erased now, out of all the documents filed under them.</summary>
public sealed record ErasePersonalDataResult(string CustomerNumber, int RecordsErased, int RecordsHeld);

// ┌─ :: SECURITY + RESILIENCE :: Documents obeys an erasure: names blanked, content deleted ───────
// │ Why : An identity document is the most personal record the bank holds. The erasure was
// │       decided in Customer Onboarding; Documents must not keep the scan, and must not report
// │       "done" while a file is left behind.
// │ How : 1. Mark every document filed under the customer erased (file name blanked) and save.
// │       2. Then delete the content of EVERY one of them - also those erased by an earlier try.
// │          A delete of a missing file does nothing, so a retry after a failure half-way
// │          removes what is left. The subscriber confirms only after this answers 200.
// │       The rows stay (type, size, SHA-256, status), so the trail still shows what existed.
// │       Known limit: a disk backup taken before still holds the scan (doc/Crypto-Shredding.md).
// │ Ref : GDPR Art. 17 | doc/Crypto-Shredding.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ErasePersonalDataCommandHandler(
    IDocumentRepository repository,
    IDocumentStorage storage,
    TimeProvider clock,
    ILogger<ErasePersonalDataCommandHandler> logger)
{
    public async Task<ErasePersonalDataResult> HandleAsync(ErasePersonalDataCommand command, CancellationToken cancellationToken)
    {
        var documents = await repository.ListForErasureAsync(command.CustomerNumber, cancellationToken);
        var now = clock.GetUtcNow();
        var erased = 0;
        foreach (var document in documents)
        {
            if (document.ErasePersonalData(now))
                erased++;
        }

        await repository.SaveChangesAsync(cancellationToken);

        foreach (var document in documents)
            await storage.DeleteAsync(document.StorageReference, cancellationToken);

        logger.LogInformation(
            "Personal data of customer {CustomerNumber} erased from {Erased} of {Held} documents; content deleted (CausationId={CausationId}).",
            command.CustomerNumber,
            erased,
            documents.Count,
            command.CausationId);

        return new ErasePersonalDataResult(command.CustomerNumber, erased, documents.Count);
    }
}