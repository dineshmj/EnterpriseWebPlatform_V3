using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Once a customer is submitted for onboarding their name is verified identity and never
// │ changes; one onboarding runs at a time; only an ACTIVE customer links one online
// │ identity, which is never taken over by a second registration.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class Customer_Lifecycle_Edge_VerifiedIdentity
{
    [Test]
    [Category("Happy")]
    public void Given_AProspect_When_TheyAreRenamed_Then_TheNewNameIsKept()
    {
        // Given
        var customer = OnboardingTestData.Prospect();

        // When
        customer.Rename(PersonName.Create("Ava", "Tran"), OnboardingTestData.Now);

        // Then
        customer.Name.Should().Be(PersonName.Create("Ava", "Tran"));
    }

    [Test]
    public void Given_ACustomerInOnboarding_When_TheyAreRenamed_Then_ItIsRefused()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        customer.StartOnboarding(OnboardingTestData.Now);

        // When
        var rename = () => customer.Rename(PersonName.Create("Ava", "Tran"), OnboardingTestData.Now);

        // Then
        rename.Should().Throw<DomainRuleViolationException>().WithMessage("*verified identity*");
    }

    [Test]
    public void Given_ACustomerInOnboarding_When_TheyChangeContactDetails_Then_ItIsRefused()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        customer.StartOnboarding(OnboardingTestData.Now);

        // When
        var change = () => customer.ChangeContactDetails(
            EmailAddress.Create("ava@example.org"), customer.PhoneNumber, OnboardingTestData.Now);

        // Then
        change.Should().Throw<DomainRuleViolationException>().WithMessage("*while the customer is ONBOARDING*");
    }

    [Test]
    public void Given_AnActiveCustomer_When_TheirContactDetailsAreSentUnchanged_Then_NothingChanges()
    {
        // Given
        var customer = OnboardingTestData.ActiveCustomer();
        var version = customer.Version;

        // When
        customer.ChangeContactDetails(customer.Email, customer.PhoneNumber, OnboardingTestData.Now);

        // Then
        customer.Version.Should().Be(version);
    }

    [TestCase(CustomerStatus.Onboarding, "*in progress*")]
    [TestCase(CustomerStatus.Active, "*already onboarded*")]
    public void Given_ACustomerNotAProspect_When_OnboardingStarts_Then_ItIsRefused(CustomerStatus status, string reason)
    {
        // Given
        var customer = status == CustomerStatus.Active ? OnboardingTestData.ActiveCustomer() : OnboardingTestData.Prospect();
        if (status == CustomerStatus.Onboarding)
            customer.StartOnboarding(OnboardingTestData.Now);

        // When
        var start = () => customer.StartOnboarding(OnboardingTestData.Now);

        // Then
        start.Should().Throw<DomainRuleViolationException>().WithMessage(reason);
    }

    [Test]
    public void Given_ARejectedOnboarding_When_TheCustomerReturnsToProspect_Then_ANewOnboardingMayStart()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        customer.StartOnboarding(OnboardingTestData.Now);

        // When
        customer.AbandonOnboarding(OnboardingTestData.Now);
        customer.StartOnboarding(OnboardingTestData.Now);

        // Then
        customer.Status.Should().Be(CustomerStatus.Onboarding);
    }

    [Test]
    public void Given_AnActiveCustomer_When_TheyRegisterOnline_Then_OnlyTheirOwnEmailMatchesAndOneIdentityIsLinked()
    {
        // Given
        var customer = OnboardingTestData.ActiveCustomer();
        var subject = Guid.NewGuid();

        // When
        customer.LinkOnlineIdentity(subject, OnboardingTestData.Now);
        customer.LinkOnlineIdentity(subject, OnboardingTestData.Now);
        var takeOver = () => customer.LinkOnlineIdentity(Guid.NewGuid(), OnboardingTestData.Now);

        // Then
        customer.MayRegisterOnlineAs(EmailAddress.Create("ava.nguyen@example.com")).Should().BeTrue();
        customer.MayRegisterOnlineAs(EmailAddress.Create("someone@example.com")).Should().BeFalse();
        customer.SubjectId.Should().Be(subject);
        takeOver.Should().Throw<DomainRuleViolationException>().WithMessage("*already has an online identity*");
    }

    [Test]
    public void Given_AProspect_When_TheyRegisterOnline_Then_ItIsRefused()
    {
        // Given
        var customer = OnboardingTestData.Prospect();

        // When
        var link = () => customer.LinkOnlineIdentity(Guid.NewGuid(), OnboardingTestData.Now);

        // Then
        customer.MayRegisterOnlineAs(customer.Email).Should().BeFalse();
        link.Should().Throw<DomainRuleViolationException>().WithMessage("Only an onboarded customer*");
    }

    [TestCase(null)]
    [TestCase("SUB-SOPHIE")]
    public void Given_ACustomer_When_ItsManagingAgentIsChecked_Then_OnlyThatAgentMatches(string? otherForm)
    {
        // Given
        var customer = OnboardingTestData.Prospect();

        // When / Then
        customer.IsManagedBy(OnboardingTestData.Sophie).Should().BeTrue();
        customer.IsManagedBy(otherForm).Should().Be(otherForm is not null);
        customer.IsManagedBy("sub-ethan").Should().BeFalse();
    }
}