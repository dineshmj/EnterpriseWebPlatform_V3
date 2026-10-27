using AwesomeAssertions;

using EnterpriseWebPlatform.DocumentsManagement.API.Bian;
using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocument;
using EnterpriseWebPlatform.Tests.Bian;

namespace EnterpriseWebPlatform.DocumentsManagement.Api.Tests.Bian;

// ┌─ :: ARCHITECTURE :: /v2 is BIAN only if BIAN's own file says so ───────────────────────────────
// │ Why : The /v2 contract claims BIAN DocumentDirectory (release 14.0.0). A renamed property, a
// │       wrong nesting or a value BIAN does not list would break that claim silently.
// │ How : Every body /v2 sends is validated against BIAN's published OpenAPI file (doc/bian,
// │       pinned) - the entry of a document in each of its states, and its reference properties
// │       (with and without a document type) - and the check is shown to be able to fail.
// │ Ref : doc/bian/README.md | Documents Management API README (/v2)
// └────────────────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Happy")]
public sealed class DocumentDirectory_Contract_Happy_ValidAgainstBian
{
    private const string BianFile = "DocumentDirectory.yaml";

    private static readonly DateTimeOffset UploadedAt = new(2026, 10, 15, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ChangedAt = new(2026, 10, 16, 11, 0, 0, TimeSpan.Zero);

    [Test]
    public void Given_AnAvailableDocument_When_RetrievedThroughV2_Then_TheEntryIsValidAndOpenSinceItsUpload()
    {
        // Given
        var document = Document();

        // When
        var entry = DocumentDirectoryMapper.ToEntry(document);

        // Then
        BianSchema.Validate(BianFile, "DocumentDirectoryEntry", entry, BianJson.Options).Should().BeEmpty();
        entry.DocumentDirectoryEntryIdentification.Should().Be("6f1c2a4e-8b3d-4c5a-9e7f-0a1b2c3d4e5f");
        entry.DirectoryEntryDateType.Should().Be("OpenDate");
        entry.DirectoryEntryDate!.DateContent.Should().Be("2026-10-15");
        entry.DirectoryEntryStatus.Should().Be(new BianStatus
        {
            StatusReason = "AVAILABLE",
            StatusDateTime = new BianDatetime { DateTimeContent = "2026-10-15T09:30:00.0000000+00:00" }
        });
    }

    [Test]
    public void Given_AnAttachedDocument_When_RetrievedThroughV2_Then_TheStatusSaysWhatItIsEvidenceOfAndSinceWhen()
    {
        // Given
        var document = Document() with { Status = "ATTACHED", AttachedAt = ChangedAt, AttachedTo = "APP-000042", Version = 2 };

        // When
        var entry = DocumentDirectoryMapper.ToEntry(document);

        // Then
        BianSchema.Validate(BianFile, "DocumentDirectoryEntry", entry, BianJson.Options).Should().BeEmpty();
        entry.DirectoryEntryStatus!.StatusReason.Should().Be("ATTACHED: evidence of APP-000042", "BIAN's Status has no value of its own, so the reason starts with EWP's code");
        entry.DirectoryEntryStatus.StatusDateTime!.DateTimeContent.Should().Be("2026-10-16T11:00:00.0000000+00:00");
    }

    [Test]
    public void Given_AnInvalidatedDocument_When_RetrievedThroughV2_Then_TheStatusSaysWhyAndSinceWhen()
    {
        // Given
        var document = Document() with { Status = "INVALIDATED", InvalidatedAt = ChangedAt, InvalidationReason = "Application APP-000042 rejected", Version = 3 };

        // When
        var entry = DocumentDirectoryMapper.ToEntry(document);

        // Then
        BianSchema.Validate(BianFile, "DocumentDirectoryEntry", entry, BianJson.Options).Should().BeEmpty();
        entry.DirectoryEntryStatus!.StatusReason.Should().Be("INVALIDATED: Application APP-000042 rejected");
        entry.DirectoryEntryStatus.StatusDateTime!.DateTimeContent.Should().Be("2026-10-16T11:00:00.0000000+00:00");
    }

    [Test]
    public void Given_ADocumentWithAType_When_ItsReferencePropertiesAreRetrieved_Then_TheyAreValidAndCarryVersionAndType()
    {
        // Given
        var document = Document() with { Version = 4 };

        // When
        var reference = DocumentDirectoryMapper.ToReferenceProperties(document);

        // Then
        BianSchema.Validate(BianFile, "DocumentReferenceProperties", reference, BianJson.Options).Should().BeEmpty();
        reference.Should().Be(new DocumentReferenceProperties
        {
            VersionNumber = "4",
            DocumentDirectoryEntryReference = new DocumentDirectoryEntry { DocumentDirectoryEntryIdentification = "6f1c2a4e-8b3d-4c5a-9e7f-0a1b2c3d4e5f" },
            DocumentReferencePropertiesType = "KYCProof"
        });
    }

    [Test]
    public void Given_ADocumentWithoutAType_When_ItsReferencePropertiesAreRetrieved_Then_TheyAreStillValidWithoutOne()
    {
        // Given
        var document = Document() with { DocumentType = null };

        // When
        var reference = DocumentDirectoryMapper.ToReferenceProperties(document);

        // Then
        BianSchema.Validate(BianFile, "DocumentReferenceProperties", reference, BianJson.Options).Should().BeEmpty();
        reference.DocumentReferencePropertiesType.Should().BeNull("a type EWP was not given is left out, not guessed");
    }

    [Test]
    public void Given_ABodyWithADateTypeBianDoesNotList_When_Validated_Then_TheCheckFindsIt()
    {
        // Given - the check itself must be able to fail, or a green test proves nothing (two $refs deep)
        var valid = DocumentDirectoryMapper.ToReferenceProperties(Document());
        var reference = valid with { DocumentDirectoryEntryReference = valid.DocumentDirectoryEntryReference! with { DirectoryEntryDateType = "UploadDate" } };

        // When
        var errors = BianSchema.Validate(BianFile, "DocumentReferenceProperties", reference, BianJson.Options);

        // Then
        errors.Should().NotBeEmpty();
    }

    private static DocumentDetailsDto Document() => new(
        DocumentId: Guid.Parse("6f1c2a4e-8b3d-4c5a-9e7f-0a1b2c3d4e5f"),
        FileName: "identity-proof.pdf",
        ContentType: "application/pdf",
        Size: 18_432,
        ContentHash: new string('a', 64),
        CreatedAt: UploadedAt,
        UpdatedAt: UploadedAt,
        Version: 1,
        Status: "AVAILABLE",
        InvalidatedAt: null,
        InvalidationReason: null,
        AttachedAt: null,
        AttachedTo: null,
        DocumentType: "KYCProof");
}