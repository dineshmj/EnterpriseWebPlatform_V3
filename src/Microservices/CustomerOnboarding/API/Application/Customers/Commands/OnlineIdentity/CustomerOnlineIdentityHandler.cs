using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.Commands.OnlineIdentity;

/// <summary>What the IDP may learn about a customer who registers for online access.</summary>
public sealed record OnlineRegistrationCandidate(string CustomerNumber, string DisplayName, string MobileEnding);

public enum OnlineIdentityOutcome
{
    Linked,
    NoMatch,
    AlreadyRegistered
}

// ┌─ :: SECURITY :: Online registration: the customer proves they are on record ───────────────────
// │ Why : A person may claim any customer number at the registration page; only details that match
// │       the record of an onboarded customer may lead to an online identity - and only one.
// │ How : The IDP asks with the customer number AND the e-mail address on record; the answer is
// │       a match or nothing (no reason, so the page cannot be used to probe customers), and holds
// │       only what the IDP needs: the name to greet and the last three digits of the mobile
// │       number. Linking repeats the check and refuses a second, different identity.
// │ Ref : Features 2.1.6 | OWASP A07 (account enumeration) | ASVS V2.5 | Threat-Model R-17
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>The checks behind the customer's online registration at the IDP.</summary>
public sealed class CustomerOnlineIdentityHandler(
    ICustomerRepository customers,
    IApplicationUnitOfWork unitOfWork,
    TimeProvider clock)
{
    /// <summary>The customer behind these details, or null when they match no onboarded customer still without online access.</summary>
    public async Task<OnlineRegistrationCandidate?> FindCandidateAsync(string customerNumber, string email, CancellationToken cancellationToken)
    {
        var customer = await FindMatchAsync(customerNumber, email, cancellationToken);
        if (customer is null || customer.SubjectId is not null)
            return null;

        var phone = customer.PhoneNumber.Value;
        return new OnlineRegistrationCandidate(
            customer.CustomerNumber.Value,
            customer.Name.FirstName,
            phone.Length >= 3 ? phone[^3..] : phone);
    }

    /// <summary>Links the IDP subject to the customer these details belong to.</summary>
    public async Task<OnlineIdentityOutcome> LinkAsync(string customerNumber, string email, Guid subjectId, CancellationToken cancellationToken)
    {
        var customer = await FindMatchAsync(customerNumber, email, cancellationToken);
        if (customer is null)
            return OnlineIdentityOutcome.NoMatch;

        try
        {
            customer.LinkOnlineIdentity(subjectId, clock.GetUtcNow());
        }
        catch (DomainRuleViolationException)
        {
            return OnlineIdentityOutcome.AlreadyRegistered;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return OnlineIdentityOutcome.Linked;
    }

    private async Task<Domain.Aggregates.Customer?> FindMatchAsync(string customerNumber, string email, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerNumber) || string.IsNullOrWhiteSpace(email))
            return null;

        EmailAddress typed;
        try
        {
            typed = EmailAddress.Create(email);
        }
        catch (Exception ex) when (ex is ArgumentException or DomainRuleViolationException)
        {
            return null;
        }

        var customer = await customers.GetByCustomerNumberAsync(customerNumber.Trim().ToUpperInvariant(), cancellationToken);
        return customer is not null && customer.MayRegisterOnlineAs(typed) ? customer : null;
    }
}