using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;

/// <summary>
/// Known people, customers and applications for the Customer Onboarding domain tests -
/// built through the aggregates' own public methods, as the API builds them.
/// </summary>
internal static class OnboardingTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    public const string Sophie = "sub-sophie";   // customer service: manages the customer

    public static readonly BranchCode Sydney = BranchCode.Create("SYD001");

    public static Customer Prospect() =>
        Customer.Create(
            CustomerNumber.Create(100001), PersonName.Create("Ava", "Nguyen"), EmailAddress.Create("ava.nguyen@example.com"),
            PhoneNumber.Create("+61 412 345 678"), CustomerType.Individual, Sophie, Now);

    public static Customer ActiveCustomer()
    {
        var customer = Prospect();
        customer.StartOnboarding(Now);
        customer.Activate(Now);
        return customer;
    }

    public static IReadOnlyCollection<EvidenceDocument> Evidence() =>
        [EvidenceDocument.Create(Guid.NewGuid(), "PASSPORT"), EvidenceDocument.Create(Guid.NewGuid(), "TAX_NOTICE")];

    public static OnboardingApplication Draft() =>
        OnboardingApplication.Create(ApplicationNumber.Issue(1, Now), customerId: 42, Sydney, Now);

    /// <summary>Submitted with two evidence documents: waiting for KYC to open its case.</summary>
    public static OnboardingApplication Submitted()
    {
        var application = Draft();
        application.Submit(Evidence(), Now);
        application.ClearDomainEvents();
        return application;
    }
}