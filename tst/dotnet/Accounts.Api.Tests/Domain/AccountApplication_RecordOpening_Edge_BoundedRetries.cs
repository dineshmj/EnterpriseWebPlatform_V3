using AwesomeAssertions;

using EnterpriseWebPlatform.Accounts.Api.Domain.Exceptions;
using EnterpriseWebPlatform.Accounts.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.Accounts.Api.Tests.Support;

namespace EnterpriseWebPlatform.Accounts.Api.Tests.Domain;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Opening at core banking: a temporary failure is retried a bounded number of times,
// │ then the application fails (and the onboarding is compensated); a refusal is final at
// │ once. Opened exactly once.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Resilience")]
public sealed class AccountApplication_RecordOpening_Edge_BoundedRetries
{
    private const int MaxAttempts = 3;

    [Test]
    public void Given_CoreBankingIsDown_When_AnAttemptFailsBeforeTheLast_Then_TheNextAttemptIsScheduled()
    {
        // Given
        var application = AccountsTestData.OpeningApplication();
        var next = AccountsTestData.Now.AddSeconds(15);

        // When
        application.RecordOpeningFailure("503 Service Unavailable", MaxAttempts, next, AccountsTestData.Now);

        // Then
        application.Status.Should().Be(AccountApplicationStatus.Opening);
        application.OpeningAttempts.Should().Be(1);
        application.NextOpeningAt.Should().Be(next);
    }

    [Test]
    [Category("Compensation")]
    public void Given_CoreBankingStaysDown_When_TheLastAttemptFails_Then_TheApplicationFails()
    {
        // Given
        var application = AccountsTestData.OpeningApplication();

        // When
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            application.RecordOpeningFailure("503 Service Unavailable", MaxAttempts, AccountsTestData.Now.AddSeconds(15), AccountsTestData.Now);

        // Then
        application.Status.Should().Be(AccountApplicationStatus.Failed);
        application.NextOpeningAt.Should().BeNull();
        application.FailureReason.Should().Contain("after 3 attempts").And.Contain("503 Service Unavailable");
    }

    [Test]
    [Category("Compensation")]
    public void Given_CoreBankingRefuses_When_TheRefusalIsRecorded_Then_TheApplicationFailsAtOnce()
    {
        // Given
        var application = AccountsTestData.OpeningApplication();

        // When
        application.RecordOpeningRefused("Customer is on the bank's exit list.", AccountsTestData.Now);

        // Then
        application.Status.Should().Be(AccountApplicationStatus.Failed);
        application.OpeningAttempts.Should().Be(1);
        application.FailureReason.Should().Contain("refused").And.Contain("exit list");
    }

    [Test]
    [Category("Happy")]
    public void Given_CoreBankingBackUp_When_TheAccountOpens_Then_ItIsRecordedOnceAndNotAgain()
    {
        // Given
        var application = AccountsTestData.OpeningApplication();
        application.RecordOpeningFailure("503", MaxAttempts, AccountsTestData.Now.AddSeconds(15), AccountsTestData.Now);

        // When
        application.RecordAccountOpened("72947086", "062-000", AccountsTestData.Now);
        var again = () => application.RecordAccountOpened("72947087", "062-000", AccountsTestData.Now);

        // Then
        application.Status.Should().Be(AccountApplicationStatus.Opened);
        application.AccountNumber.Should().Be("72947086");
        again.Should().Throw<DomainConflictException>().WithMessage("*not being opened*");
    }
}