using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Events;
using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Erasure cannot be undone, so the aggregates decide who may do it and when: a prospect
// │ untouched for the retention period only (checked at the request AND at the approval),
// │ one person asks and ANOTHER decides, an undecided request expires, a decided one
// │ stays decided - and an erased customer is never onboarded.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PersonalDataErasureRequest_Decide_Edge_FourEyesAndRetention
{
    private const string Daniel = "sub-daniel";      // operations: requests
    private const string Platform = "sub-platform";  // operations: decides

    private static readonly PersonalDataErasurePolicy Policy = new(TimeSpan.FromDays(30), TimeSpan.FromDays(7));
    private static readonly DateTimeOffset AfterRetention = OnboardingTestData.Now.AddDays(31);

    private static PersonalDataErasureRequest Requested(Customer customer) =>
        PersonalDataErasureRequest.Create(customer, PersonalDataErasureReason.CustomerRequest, Daniel, Policy, AfterRetention);

    [Test]
    [Category("Happy")]
    public void Given_AProspectUntouchedForTheRetentionPeriod_When_ErasureIsRequested_Then_ItWaitsForASecondPerson()
    {
        // Given
        var customer = OnboardingTestData.Prospect();

        // When
        var request = Requested(customer);

        // Then
        request.Status.Should().Be(PersonalDataErasureStatus.Pending);
        request.ExpiresAt.Should().Be(AfterRetention.AddDays(7));
        request.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<PersonalDataErasureRequestedDomainEvent>()
            .Which.RequestedByUserId.Should().Be(Daniel);
        customer.IsPersonalDataErased.Should().BeFalse();
    }

    [Test]
    public void Given_AProspectChangedWithinTheRetentionPeriod_When_ErasureIsRequested_Then_ItIsRefused()
    {
        // Given
        var customer = OnboardingTestData.Prospect();

        // When
        var request = () => PersonalDataErasureRequest.Create(
            customer, PersonalDataErasureReason.RetentionEnded, Daniel, Policy, OnboardingTestData.Now.AddDays(29));

        // Then
        request.Should().Throw<DomainRuleViolationException>().WithMessage("*must be kept until 2026-11-27*");
    }

    [TestCase(CustomerStatus.Onboarding)]
    [TestCase(CustomerStatus.Active)]
    public void Given_ACustomerWhoIsNotAProspect_When_ErasureIsRequested_Then_ItIsRefused(CustomerStatus status)
    {
        // Given - an application in progress, or accounts the bank must keep
        var customer = OnboardingTestData.Prospect();
        customer.StartOnboarding(OnboardingTestData.Now);
        if (status == CustomerStatus.Active)
            customer.Activate(OnboardingTestData.Now);

        // When
        var request = () => Requested(customer);

        // Then
        request.Should().Throw<DomainRuleViolationException>().WithMessage($"*this customer is {status.ToString().ToUpperInvariant()}*");
    }

    [Test]
    public void Given_ARequest_When_TheRequesterApprovesIt_Then_ItIsRefusedAndNothingIsErased()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        var request = Requested(customer);

        // When - the subject ID compared without regard to case
        var approve = () => request.Approve(customer, "SUB-DANIEL", Policy, AfterRetention);

        // Then
        approve.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("Four eyes*");
        customer.IsPersonalDataErased.Should().BeFalse();
        request.Status.Should().Be(PersonalDataErasureStatus.Pending);
    }

    [Test]
    public void Given_ARequest_When_TheRequesterRejectsIt_Then_ItIsRefusedToo()
    {
        // Given - a rejection is a decision as well
        var request = Requested(OnboardingTestData.Prospect());

        // When
        var reject = () => request.Reject(Daniel, "Changed my mind about this one.", AfterRetention);

        // Then
        reject.Should().Throw<SeparationOfDutiesViolationException>();
    }

    [Test]
    [Category("Happy")]
    public void Given_ARequest_When_AnotherPersonApprovesIt_Then_TheCustomerIsErasedAndBothPeopleAreNamed()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        var request = Requested(customer);
        request.ClearDomainEvents();

        // When
        request.Approve(customer, Platform, Policy, AfterRetention.AddHours(1));

        // Then
        request.Status.Should().Be(PersonalDataErasureStatus.Erased);
        customer.IsPersonalDataErased.Should().BeTrue();
        customer.PersonalDataErasedAt.Should().Be(AfterRetention.AddHours(1));
        var erased = request.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<CustomerPersonalDataErasedDomainEvent>().Subject;
        erased.RequestedByUserId.Should().Be(Daniel);
        erased.DecidedByUserId.Should().Be(Platform);
    }

    [Test]
    public void Given_ACustomerOnboardedAfterTheRequest_When_ItIsApproved_Then_TheApprovalChecksAgainAndRefuses()
    {
        // Given - eligible when requested, not any more when decided
        var customer = OnboardingTestData.Prospect();
        var request = Requested(customer);
        customer.StartOnboarding(AfterRetention);

        // When
        var approve = () => request.Approve(customer, Platform, Policy, AfterRetention.AddHours(1));

        // Then
        approve.Should().Throw<DomainRuleViolationException>().WithMessage("*ONBOARDING*");
        customer.IsPersonalDataErased.Should().BeFalse();
    }

    [Test]
    public void Given_ARequestNobodyDecidedInTime_When_ItIsApproved_Then_ItHasExpired()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        var request = Requested(customer);

        // When
        var approve = () => request.Approve(customer, Platform, Policy, request.ExpiresAt);

        // Then
        approve.Should().Throw<DomainConflictException>().WithMessage("*expired*");
        request.HasExpired(request.ExpiresAt).Should().BeTrue();
        customer.IsPersonalDataErased.Should().BeFalse();
    }

    [Test]
    public void Given_AnApprovedRequest_When_ItIsDecidedAgain_Then_ItWasAlreadyDecided()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        var request = Requested(customer);
        request.Approve(customer, Platform, Policy, AfterRetention);

        // When
        var reject = () => request.Reject("sub-another", "Too late to change this, but trying.", AfterRetention);

        // Then
        reject.Should().Throw<DomainConflictException>().WithMessage("*already decided (ERASED)*");
    }

    [TestCase(null)]
    [TestCase("   ")]
    [TestCase("Too short")]
    public void Given_ARequest_When_ItIsRejectedWithoutAReason_Then_ItIsRefused(string? remarks)
    {
        // Given
        var request = Requested(OnboardingTestData.Prospect());

        // When
        var reject = () => request.Reject(Platform, remarks, AfterRetention);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("Say why*");
        request.Status.Should().Be(PersonalDataErasureStatus.Pending);
    }

    [Test]
    public void Given_ARejectedRequest_When_Checked_Then_NothingWasErasedAndTheRemarksAreKept()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        var request = Requested(customer);

        // When
        request.Reject(Platform, "The customer has an open complaint.", AfterRetention);

        // Then
        request.Status.Should().Be(PersonalDataErasureStatus.Rejected);
        request.DecisionRemarks.Should().Be("The customer has an open complaint.");
        customer.IsPersonalDataErased.Should().BeFalse();
    }

    [Test]
    public void Given_AnErasedCustomer_When_TheyStartOnboarding_Then_ItIsRefused()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        Requested(customer).Approve(customer, Platform, Policy, AfterRetention);

        // When
        var start = () => customer.StartOnboarding(AfterRetention);

        // Then
        start.Should().Throw<DomainRuleViolationException>().WithMessage("*erased*");
    }

    [Test]
    public void Given_AnErasedCustomer_When_ErasureIsRequestedAgain_Then_ItWasAlreadyErased()
    {
        // Given
        var customer = OnboardingTestData.Prospect();
        Requested(customer).Approve(customer, Platform, Policy, AfterRetention);

        // When
        var again = () => Requested(customer);

        // Then
        again.Should().Throw<DomainConflictException>().WithMessage("*already erased*");
    }
}