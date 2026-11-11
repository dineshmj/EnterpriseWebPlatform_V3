using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A retention shorter than the law's (laptop, cloud demo) must be flagged, so the
// │ Operations erasure screen warns that it is not production behaviour; the law's own
// │ period (7 years) must not be flagged.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PersonalDataErasurePolicy_IsShorterThanLegal_Edge_DemoWarning
{
    [Test]
    public void Given_NoRetentionPeriod_When_ThePolicyIsRead_Then_ItIsShorterThanTheLaws()
    {
        // Given
        var policy = new PersonalDataErasurePolicy(TimeSpan.Zero, TimeSpan.FromDays(7));

        // When
        var shorter = policy.IsShorterThanLegal;

        // Then
        shorter.Should().BeTrue();
    }

    [Test]
    [Category("Happy")]
    public void Given_TheLegalRetentionPeriod_When_ThePolicyIsRead_Then_ItIsNotFlagged()
    {
        // Given
        var policy = new PersonalDataErasurePolicy(TimeSpan.FromDays(2557), TimeSpan.FromDays(7));

        // When
        var shorter = policy.IsShorterThanLegal;

        // Then
        shorter.Should().BeFalse();
    }
}