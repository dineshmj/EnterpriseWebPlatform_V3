using AwesomeAssertions;

using EnterpriseWebPlatform.Payments.Api.Tests.Support;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ An officer's clearance sets the most they may approve: the highest configured level at
// │ or below theirs applies; below every level they may approve nothing; a level without a
// │ maximum is unlimited.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Boundary")]
[Category("Security")]
public sealed class ApprovalLimits_Allows_Edge_ClearanceLevels
{
    [TestCase(3, 10_000.00, true)]
    [TestCase(3, 10_000.01, false)]
    [TestCase(4, 100_000.00, true)]
    [TestCase(4, 100_000.01, false)]
    [TestCase(5, 1_000_000.00, true)]
    [TestCase(9, 1_000_000.00, true)]
    [TestCase(2, 0.01, false)]
    [TestCase(0, 0.01, false)]
    public void Given_AClearanceLevel_When_AnAmountIsChecked_Then_ItIsAllowedOnlyUpToTheLevelsMaximum(int clearance, decimal amount, bool allowed)
    {
        // Given
        var limits = PaymentsTestData.Limits;

        // When
        var result = limits.Allows(clearance, amount);

        // Then
        result.Should().Be(allowed);
    }

    [TestCase(2, 0)]
    [TestCase(3, 10_000)]
    [TestCase(4, 100_000)]
    public void Given_AClearanceLevel_When_TheMaximumIsAsked_Then_TheHighestLevelAtOrBelowItApplies(int clearance, decimal max)
    {
        // Given / When
        var result = PaymentsTestData.Limits.MaxFor(clearance);

        // Then
        result.Should().Be(max);
    }

    [Test]
    public void Given_TheUnlimitedLevel_When_TheMaximumIsAsked_Then_ThereIsNone()
    {
        // Given / When
        var result = PaymentsTestData.Limits.MaxFor(5);

        // Then
        result.Should().BeNull();
    }
}