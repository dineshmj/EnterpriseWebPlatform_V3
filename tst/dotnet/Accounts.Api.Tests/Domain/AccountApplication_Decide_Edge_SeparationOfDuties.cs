using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Domain.Aggregates;
using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Separation of duties across the onboarding: whoever started it, or approved it in
// │ Compliance, never opens its account; an unknown initiator fails closed. Once an officer
// │ takes the application, only they act on it (ReBAC); a hold must be released first.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("SoD")]
public sealed class AccountApplication_Decide_Edge_SeparationOfDuties
{
    [Test]
    [Category("Happy")]
    public void Given_AnIndependentOfficer_When_TheyApprove_Then_CoreBankingIsAskedToOpenTheAccount()
    {
        // Given
        var application = AccountsTestData.PendingApplication();

        // When
        application.Approve(AccountsTestData.Jack, AccountProduct.Savings, OfficerText.From("Identity and tax proof in order."),
            Guid.NewGuid(), AccountsTestData.Now);

        // Then
        application.Status.Should().Be(AccountApplicationStatus.Opening);
        application.Product.Should().Be(AccountProduct.Savings);
        application.AssignedOfficerUserId.Should().Be(AccountsTestData.Jack);
        application.NextOpeningAt.Should().Be(AccountsTestData.Now);
    }

    [TestCase(AccountsTestData.Sophie)]
    [TestCase("SUB-SOPHIE")]
    [Category("Security")]
    public void Given_TheOnboardingsInitiator_When_TheyApprove_Then_ItIsRefused(string officer)
    {
        // Given
        var application = AccountsTestData.PendingApplication();

        // When
        var approve = () => application.Approve(officer, AccountProduct.Savings, null, Guid.NewGuid(), AccountsTestData.Now);

        // Then
        approve.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("The workflow initiator*");
        application.Status.Should().Be(AccountApplicationStatus.PendingReview);
    }

    [Test]
    [Category("Security")]
    public void Given_TheComplianceApprover_When_TheyTakeTheApplication_Then_ItIsRefused()
    {
        // Given
        var application = AccountsTestData.PendingApplication();

        // When
        var claim = () => application.Claim(AccountsTestData.Olivia, AccountsTestData.Now);

        // Then
        claim.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("*approved this application in Compliance*");
    }

    [Test]
    [Category("Security")]
    public void Given_AnUnknownInitiator_When_AnyOfficerActs_Then_ItIsRefusedFailClosed()
    {
        // Given
        var application = AccountsTestData.PendingApplication(initiatedBy: null);

        // When
        var approve = () => application.Approve(AccountsTestData.Jack, AccountProduct.Savings, null, Guid.NewGuid(), AccountsTestData.Now);

        // Then
        approve.Should().Throw<SeparationOfDutiesViolationException>().WithMessage("*cannot be verified*");
    }

    [Test]
    [Category("Security")]
    public void Given_AnApplicationTakenByJack_When_AnotherOfficerApproves_Then_ItIsRefused()
    {
        // Given
        var application = AccountsTestData.PendingApplication();
        application.Claim(AccountsTestData.Jack, AccountsTestData.Now);

        // When
        var approve = () => application.Approve(AccountsTestData.Ruby, AccountProduct.Savings, null, Guid.NewGuid(), AccountsTestData.Now);

        // Then
        approve.Should().Throw<AssignmentViolationException>();
    }

    [Test]
    public void Given_AnApplicationOnHold_When_TheOfficerApproves_Then_TheHoldMustBeReleasedFirst()
    {
        // Given
        var application = AccountsTestData.PendingApplication();
        application.PlaceOnHold(AccountsTestData.Jack, OfficerText.From("Waiting for the customer's call back."), AccountsTestData.Now);

        // When
        var approve = () => application.Approve(AccountsTestData.Jack, AccountProduct.Savings, null, Guid.NewGuid(), AccountsTestData.Now);

        // Then
        approve.Should().Throw<DomainConflictException>().WithMessage("*on hold*");
    }

    [Test]
    [Category("Boundary")]
    public void Given_NoReason_When_AnOfficerRejects_Then_ItIsRefused()
    {
        // Given
        var application = AccountsTestData.PendingApplication();

        // When
        var reject = () => application.Reject(AccountsTestData.Jack, OfficerText.From("   "), Guid.NewGuid(), AccountsTestData.Now);

        // Then
        reject.Should().Throw<DomainRuleViolationException>().WithMessage("Remarks are required*");
    }

    [Test]
    public void Given_ADecidedApplication_When_AnotherDecisionArrives_Then_ItIsRefused()
    {
        // Given
        var application = AccountsTestData.OpeningApplication();

        // When
        var reject = () => application.Reject(AccountsTestData.Jack, OfficerText.From("Changed my mind."), Guid.NewGuid(), AccountsTestData.Now);

        // Then
        reject.Should().Throw<DomainConflictException>().WithMessage("*already decided*");
    }

    [Test]
    public void Given_AnApplicationNotYetOpened_When_AnAccountIsOpenedForIt_Then_ItIsRefused()
    {
        // Given
        var application = AccountsTestData.OpeningApplication();

        // When
        var open = () => Account.Open("72947086", "062-000", "CB-REF-1", application, AccountsTestData.Now);

        // Then
        open.Should().Throw<DomainConflictException>().WithMessage("*core-banking system has opened*");
    }
}