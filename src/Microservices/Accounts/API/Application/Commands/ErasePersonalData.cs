using EnterpriseWebPlatform.Accounts.Api.Application.Abstractions;

namespace EnterpriseWebPlatform.Accounts.Api.Application.Commands;

/// <summary>Customer Onboarding erased the customer's personal data: Accounts forgets its copy too.</summary>
public sealed record ErasePersonalDataCommand(
    string CustomerNumber,
    Guid CausationId,
    Guid? WorkflowId,
    Guid? CorrelationId);

/// <summary>How many of the customer's applications were erased now, out of all the applications Accounts holds for them.</summary>
public sealed record ErasePersonalDataResult(string CustomerNumber, int RecordsErased, int RecordsHeld);

// ┌─ :: SECURITY :: Accounts obeys an erasure, it does not decide one ─────────────────────────────
// │ Why : The erasure was asked for and approved in Customer Onboarding (four eyes, eligibility).
// │       Checking it again here would let one context keep what the bank decided to forget.
// │ How : Every application of the customer is marked erased in one transaction, which also deletes the
// │       customer's key (AccountsDbContext). A redelivered event erases nothing new and answers the
// │       same - the subscriber confirms only after this answer.
// │ Ref : doc/Crypto-Shredding.md | GDPR Art. 17
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class ErasePersonalDataCommandHandler(
    IAccountApplicationRepository repository,
    IAccountsUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ErasePersonalDataCommandHandler> logger)
{
    public async Task<ErasePersonalDataResult> HandleAsync(ErasePersonalDataCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var applications = await repository.ListForErasureAsync(command.CustomerNumber, cancellationToken);
        var now = clock.GetUtcNow();
        var erased = 0;
        foreach (var application in applications)
        {
            if (application.ErasePersonalData(now))
                erased++;
        }

        await unitOfWork.SaveChangesAsync(
            new WorkflowContext(command.WorkflowId, command.CorrelationId, command.CausationId),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Personal data of customer {CustomerNumber} erased from {Erased} of {Held} account applications (CausationId={CausationId}).",
            command.CustomerNumber,
            erased,
            applications.Count,
            command.CausationId);

        return new ErasePersonalDataResult(command.CustomerNumber, erased, applications.Count);
    }
}