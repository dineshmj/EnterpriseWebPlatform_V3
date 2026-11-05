using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.PersonalDataErasure;

/// <summary>An operator asks for a customer's personal data to be erased.</summary>
public sealed record RequestPersonalDataErasureCommand(
    string CustomerNumber,
    PersonalDataErasureReason Reason,
    string RequestedByUserId);

/// <summary>A second operator approves (the key is destroyed) or rejects (remarks required).</summary>
public sealed record DecidePersonalDataErasureCommand(
    long ErasureRequestId,
    bool Approve,
    string? Remarks,
    string DecidedByUserId);

public sealed record PersonalDataErasureResult(long ErasureRequestId, string CustomerNumber, string Status);

// ┌─ :: SECURITY :: The erasure workflow: request, then a second person's decision ────────────────
// │ Why : Erasing a person cannot be undone, and some records must still be kept by law.
// │ How : The aggregates decide (PersonalDataErasureRequest, Customer): eligibility at the request
// │       and again at the approval, four eyes, one open request per customer, expiry. Approval
// │       saves the decision, the destroyed key and the Outbox event in ONE transaction - there is
// │       no state in which the key is gone but nobody was told, or the reverse.
// │ Ref : doc/Crypto-Shredding.md | Features 2.2 | separation of duties
// └────────────────────────────────────────────────────────────────────────────────────────────────
public sealed class PersonalDataErasureCommandHandler(
    ICustomerRepository customers,
    IPersonalDataErasureRequestRepository requests,
    IApplicationUnitOfWork unitOfWork,
    PersonalDataErasurePolicy policy,
    TimeProvider clock)
{
    public async Task<PersonalDataErasureResult> RequestAsync(RequestPersonalDataErasureCommand command, CancellationToken cancellationToken)
    {
        var customerNumber = command.CustomerNumber.Trim().ToUpperInvariant();
        var customer = await customers.GetByCustomerNumberAsync(customerNumber, cancellationToken)
            ?? throw new KeyNotFoundException($"Customer '{customerNumber}' was not found.");

        var now = clock.GetUtcNow();

        // One open request per customer: an undecided one past its expiry is closed first.
        var pending = await requests.GetPendingForCustomerAsync(customer.Id, cancellationToken);
        if (pending is not null)
        {
            if (!pending.HasExpired(now))
                throw new DomainConflictException("A request to erase this customer is already waiting for a second person's decision.");
            pending.Expire(now);
        }

        var request = PersonalDataErasureRequest.Create(customer, command.Reason, command.RequestedByUserId, policy, now);
        await requests.AddAsync(request, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result(request);
    }

    public async Task<PersonalDataErasureResult> DecideAsync(DecidePersonalDataErasureCommand command, CancellationToken cancellationToken)
    {
        var request = await requests.GetByIdAsync(command.ErasureRequestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Erasure request '{command.ErasureRequestId}' was not found.");

        var now = clock.GetUtcNow();

        if (command.Approve)
        {
            // Loading the customer needs their key: an already erased customer is refused here.
            var customer = await customers.GetByIdAsync(request.CustomerId, cancellationToken)
                ?? throw new KeyNotFoundException($"Customer '{request.CustomerNumber}' was not found.");
            request.Approve(customer, command.DecidedByUserId, policy, now);
        }
        else
        {
            request.Reject(command.DecidedByUserId, command.Remarks, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result(request);
    }

    private static PersonalDataErasureResult Result(PersonalDataErasureRequest request) =>
        new(request.Id, request.CustomerNumber, request.Status.ToString().ToUpperInvariant());
}