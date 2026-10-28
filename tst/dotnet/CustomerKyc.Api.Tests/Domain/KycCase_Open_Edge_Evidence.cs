using AwesomeAssertions;

using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Aggregates;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.Exceptions;
using EnterpriseWebPlatform.CustomerKyc.Api.Domain.ValueObjects;
using EnterpriseWebPlatform.CustomerKyc.Api.Tests.Support;

namespace EnterpriseWebPlatform.CustomerKyc.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A KYC case opens only with what it verifies: two different evidence documents and
// │ the applicant as submitted. Without them the officer could only guess (fail closed).
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
public sealed class KycCase_Open_Edge_Evidence
{
    private static KycCase Open(Guid identityProof, Guid taxProof) =>
        KycCase.Open(
            Guid.NewGuid(), "APP-261028-TEST01", "CUST-100001", KycTestData.Sydney, KycTestData.Ava(),
            identityProof, taxProof, KycTestData.Sophie, KycTestData.Now);

    [Test]
    [Category("Happy")]
    public void Given_TwoEvidenceDocuments_When_TheCaseOpens_Then_BothStagesWaitForReview()
    {
        // Given / When
        var kycCase = Open(Guid.NewGuid(), Guid.NewGuid());

        // Then
        kycCase.Status.Should().Be(KycCaseStatus.PendingReview);
        kycCase.IdentityVerification.IsPending.Should().BeTrue();
        kycCase.DocumentVerification.IsPending.Should().BeTrue();
        kycCase.AssignedOfficerUserId.Should().BeNull();
        kycCase.Applicant.CountryCode.Should().Be("AU");
    }

    [Test]
    [Category("Security")]
    public void Given_AMissingEvidenceDocument_When_TheCaseOpens_Then_ItIsRefused()
    {
        // Given / When
        var open = () => Open(Guid.NewGuid(), Guid.Empty);

        // Then
        open.Should().Throw<DomainRuleViolationException>().WithMessage("*identity proof and tax proof documents are required*");
    }

    [Test]
    [Category("Security")]
    public void Given_TheSameDocumentForBothProofs_When_TheCaseOpens_Then_ItIsRefused()
    {
        // Given
        var document = Guid.NewGuid();

        // When
        var open = () => Open(document, document);

        // Then
        open.Should().Throw<DomainRuleViolationException>().WithMessage("*must be different documents*");
    }

    [TestCase("", "Nguyen")]
    [TestCase("Ava", "  ")]
    [Category("Boundary")]
    public void Given_AnApplicantWithoutAFullName_When_TheyAreRecorded_Then_ItIsRefused(string first, string last)
    {
        // Given / When
        var create = () => Applicant.Create(first, last, null, null, null, null, null, null);

        // Then
        create.Should().Throw<DomainRuleViolationException>();
    }

    [TestCase("syd001", "SYD001")]
    [TestCase(" mel002 ", "MEL002")]
    [Category("Boundary")]
    public void Given_ABranchCode_When_ItIsRead_Then_ItIsNormalised(string input, string expected)
    {
        // Given / When / Then
        BranchCode.Create(input).Value.Should().Be(expected);
    }

    [TestCase("")]
    [TestCase("SYD-001")]
    [TestCase("ABCDEFGHIJKLMNOPQRSTU")]
    [Category("Boundary")]
    public void Given_AnInvalidBranchCode_When_ItIsRead_Then_ItIsRefused(string input)
    {
        // Given / When / Then
        BranchCode.TryCreate(input, out var branch).Should().BeFalse();
        branch.Should().BeNull();
    }
}