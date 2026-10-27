using System.Globalization;

using EnterpriseWebPlatform.DocumentsManagement.Application.Documents.Queries.GetDocument;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Bian;

/// <summary>
/// Translates EWP's document into BIAN's DocumentDirectory contract. Pure: no I/O, so every
/// mapping rule is unit-tested (tst/dotnet/DocumentsManagement.Api.Tests/Bian).
/// </summary>
public static class DocumentDirectoryMapper
{
    /// <summary>EWP keeps one reference record per document; BIAN addresses it by an id in the path.</summary>
    public const string ReferencePropertiesId = "primary";

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ Where EWP's document sits in BIAN's directory entry (doc/bian/README.md, "Documents"):
    // │   the document ID -> DocumentDirectoryEntryIdentification; uploaded at -> "OpenDate";
    // │   the lifecycle -> DirectoryEntryStatus. BIAN's Status has NO status value, only a reason
    // │   and a date-time - so the reason starts with EWP's own code (AVAILABLE, ATTACHED,
    // │   INVALIDATED), followed by what it is attached to or why it was invalidated, and the
    // │   date-time is when that state began.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static DocumentDirectoryEntry ToEntry(DocumentDetailsDto document) => new()
    {
        DocumentDirectoryEntryIdentification = Identification(document),
        DirectoryEntryDateType = "OpenDate",
        DirectoryEntryDate = new BianDate { DateContent = document.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        DirectoryEntryStatus = new BianStatus
        {
            StatusReason = StatusReason(document),
            StatusDateTime = new BianDatetime { DateTimeContent = StatusSince(document).ToString("o", CultureInfo.InvariantCulture) }
        }
    };

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ BIAN's reference properties: VersionNumber is EWP's version (every lifecycle change
    // │ raises it); the type is EWP's document type as the uploading channel named it (e.g. a
    // │ proof of identity), or none. BIAN's DocumentService block describes a service performed
    // │ on a document (provider, schedule, outcome) - EWP records none, so it is left out.
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static DocumentReferenceProperties ToReferenceProperties(DocumentDetailsDto document) => new()
    {
        VersionNumber = document.Version.ToString(CultureInfo.InvariantCulture),
        DocumentDirectoryEntryReference = new DocumentDirectoryEntry { DocumentDirectoryEntryIdentification = Identification(document) },
        DocumentReferencePropertiesType = document.DocumentType
    };

    private static string Identification(DocumentDetailsDto document) => document.DocumentId.ToString("D");

    private static string StatusReason(DocumentDetailsDto document) => document.Status switch
    {
        "ATTACHED" when document.AttachedTo is { } attachedTo => $"ATTACHED: evidence of {attachedTo}",
        "INVALIDATED" when document.InvalidationReason is { } reason => $"INVALIDATED: {reason}",
        var status => status
    };

    private static DateTimeOffset StatusSince(DocumentDetailsDto document) => document.Status switch
    {
        "ATTACHED" => document.AttachedAt ?? document.UpdatedAt,
        "INVALIDATED" => document.InvalidatedAt ?? document.UpdatedAt,
        _ => document.CreatedAt
    };
}