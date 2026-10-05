using EnterpriseWebPlatform.CustomerOnboarding.Application.Abstractions.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Onboarding;

/// <summary>
/// Keeps the customer's status in step with the end of their onboarding application:
/// COMPLETED → customer ACTIVE; REJECTED or CANCELLED → customer back to PROSPECT, so
/// a new application can start. Like starting onboarding, this changes the Customer in
/// the same transaction as the application: both aggregates live in this context and
/// database, and a customer must never stay ONBOARDING after their application ended.
/// </summary>
public sealed class CustomerLifecycleSync(ICustomerRepository customerRepository)
{
    public async Task ApplyAsync(OnboardingApplication application, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (application.Status is not (OnboardingApplicationStatus.Completed or
                                       OnboardingApplicationStatus.Rejected or
                                       OnboardingApplicationStatus.Cancelled))
            return;

        var customer = await customerRepository.GetByIdAsync(application.CustomerId, cancellationToken)
            ?? throw new InvalidOperationException($"Customer '{application.CustomerId}' of application '{application.Id}' was not found.");

        // Already applied (e.g. the customer was suspended meanwhile): nothing to do.
        if (customer.Status != CustomerStatus.Onboarding)
            return;

        if (application.Status == OnboardingApplicationStatus.Completed)
            customer.Activate(now);
        else
            customer.AbandonOnboarding(now);
    }
}