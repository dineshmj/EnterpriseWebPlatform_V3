using AwesomeAssertions;

using EnterpriseWebPlatform.DocumentsManagement.Domain.Aggregates;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Events;
using EnterpriseWebPlatform.DocumentsManagement.Domain.Exceptions;
using EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

namespace EnterpriseWebPlatform.DocumentsManagement.Api.Tests.Domain;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Evidence is retained, never deleted: only an AVAILABLE document may be removed by its
// │ uploader; once ATTACHED to an application it is a KYC record, and a rejection
// │ INVALIDATES it (kept, with the reason). Repeated saga commands change nothing; the
// │ branch fixed at upload decides who may see it.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class Document_Lifecycle_Edge_EvidenceRetained
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly BranchCode Sydney = BranchCode.Create("SYD001");

    private static Document Uploaded() =>
        Document.Upload(
            Guid.NewGuid(), FileName.Create("passport.pdf"), "application/pdf", size: 2048,
            ContentHash.Create(new string('a', 64)), "documents/2026/10/passport.pdf", Sydney, Now, " PASSPORT ", " CUST-100001 ");

    [Test]
    [Category("Happy")]
    public void Given_AnUploadedDocument_When_ItIsAttachedToAnApplication_Then_ItCanNoLongerBeRemoved()
    {
        // Given
        var document = Uploaded();
        document.CanBeRemoved.Should().BeTrue();
        document.DocumentType.Should().Be("PASSPORT");

        // When
        var attached = document.Attach("APP-20261028-000001", Now);

        // Then
        attached.Should().BeTrue();
        document.Status.Should().Be(DocumentStatus.Attached);
        document.CanBeRemoved.Should().BeFalse();
        document.DomainEvents.OfType<DocumentAttachedDomainEvent>().Should().ContainSingle();
    }

    [Test]
    [Category("Compensation")]
    public void Given_AnAttachedDocument_When_TheApplicationIsRejected_Then_ItIsInvalidatedAndKeptWithTheReason()
    {
        // Given
        var document = Uploaded();
        document.Attach("APP-20261028-000001", Now);

        // When
        var invalidated = document.Invalidate(" Onboarding rejected at KYC. ", Now.AddHours(1));

        // Then
        invalidated.Should().BeTrue();
        document.Status.Should().Be(DocumentStatus.Invalidated);
        document.InvalidationReason.Should().Be("Onboarding rejected at KYC.");
        document.InvalidatedAt.Should().Be(Now.AddHours(1));
        document.CanBeRemoved.Should().BeFalse();
        document.StorageReference.Should().NotBeEmpty();
    }

    [Test]
    [Category("Resilience")]
    public void Given_AnInvalidatedDocument_When_TheSameCommandsArriveAgain_Then_NothingChanges()
    {
        // Given
        var document = Uploaded();
        document.Attach("APP-20261028-000001", Now);
        document.Invalidate("Onboarding rejected at KYC.", Now);
        var version = document.Version;

        // When
        var invalidatedAgain = document.Invalidate("Onboarding rejected at KYC.", Now);
        var attachedAgain = document.Attach("APP-20261028-000002", Now);

        // Then
        invalidatedAgain.Should().BeFalse();
        attachedAgain.Should().BeFalse();
        document.Version.Should().Be(version);
        document.AttachedTo.Should().Be("APP-20261028-000001");
    }

    [Test]
    [Category("Boundary")]
    public void Given_LongTexts_When_TheDocumentIsAttachedAndInvalidated_Then_TheyAreCutToTheColumnLimits()
    {
        // Given
        var document = Uploaded();

        // When
        document.Attach(new string('a', 300), Now);
        document.Invalidate(new string('r', 900), Now);

        // Then
        document.AttachedTo.Should().HaveLength(Document.MaxAttachedToLength);
        document.InvalidationReason.Should().HaveLength(Document.MaxInvalidationReasonLength);
    }

    [Test]
    public void Given_NoReasonOrTarget_When_TheDocumentIsInvalidatedOrAttached_Then_ItIsRefused()
    {
        // Given
        var document = Uploaded();

        // When
        var invalidate = () => document.Invalidate("  ", Now);
        var attach = () => document.Attach("", Now);

        // Then
        invalidate.Should().Throw<DomainRuleViolationException>();
        attach.Should().Throw<DomainRuleViolationException>();
        document.Status.Should().Be(DocumentStatus.Available);
    }

    [TestCase(0)]
    [TestCase(-1)]
    [Category("Boundary")]
    public void Given_AnEmptyFile_When_ItIsUploaded_Then_ItIsRefused(long size)
    {
        // Given / When
        var upload = () => Document.Upload(
            Guid.NewGuid(), FileName.Create("empty.pdf"), "application/pdf", size,
            ContentHash.Create(new string('a', 64)), "documents/empty.pdf", Sydney, Now);

        // Then
        upload.Should().Throw<DomainRuleViolationException>().WithMessage("*cannot be empty*");
    }

    [TestCase("SYD001", true)]
    [TestCase("syd001", true)]
    [TestCase("MEL001", false)]
    public void Given_ADocumentOfSydney_When_ABranchAsksForIt_Then_OnlySydneyMatches(string branch, bool expected)
    {
        // Given
        var document = Uploaded();

        // When / Then
        document.BelongsTo(BranchCode.Create(branch)).Should().Be(expected);
        document.BelongsTo(null).Should().BeFalse();
    }

    [TestCase("ABC")]
    [TestCase("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAG")]
    [Category("Boundary")]
    public void Given_ANonSha256Hash_When_ItIsRecorded_Then_ItIsRefused(string hash)
    {
        // Given / When
        var create = () => ContentHash.Create(hash);

        // Then
        create.Should().Throw<DomainRuleViolationException>();
    }
}