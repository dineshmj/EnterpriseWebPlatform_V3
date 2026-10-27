using System.Text.Json;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.DocumentsManagement.API.Bian;

// ┌─ :: ARCHITECTURE :: BIAN "Document Directory" - the contract, not the model ───────────────────
// │ Why : To show that the Documents Management API can speak BIAN's standard service-domain
// │       contract (release 14.0.0, service domain DocumentDirectory) next to its own /v1 - without
// │       letting BIAN's very general shapes into the domain. EWP's callers stay on /v1.
// │ How : These records carry BIAN's property names and nesting EXACTLY (checked against BIAN's
// │       published OpenAPI file in the tests), but only the properties EWP can fill honestly: the
// │       directory entry (which document, since when, in which state) and its reference
// │       properties (version and document type). BIAN's directory holds a POINTER to a document -
// │       no file content, name, type or hash anywhere in the file - so the content stays on /v1.
// │       Anti-corruption layer: the mapper translates from the existing query; the domain and
// │       the tables do not know BIAN exists. Each bounded context owns its copy of the BIAN shapes.
// │ Ref : doc/bian/README.md | BIAN semantic APIs release 14.0.0 (DocumentDirectory.yaml)
// │       | Evans, DDD - Anti-Corruption Layer
// └────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>BIAN's JSON: its PascalCase property names as they are, and no empty properties.</summary>
public static class BianJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>BIAN "DocumentDirectoryEntry" (response of Retrieve).</summary>
public sealed record DocumentDirectoryEntry
{
    public string? DocumentDirectoryEntryIdentification { get; init; }
    public string? DirectoryEntryDateType { get; init; }
    public BianDate? DirectoryEntryDate { get; init; }
    public BianStatus? DirectoryEntryStatus { get; init; }
}

/// <summary>BIAN "DocumentReferenceProperties" (response of RetrieveDocumentReferenceProperties).</summary>
public sealed record DocumentReferenceProperties
{
    public string? VersionNumber { get; init; }
    public DocumentDirectoryEntry? DocumentDirectoryEntryReference { get; init; }
    public string? DocumentReferencePropertiesType { get; init; }
}

/// <summary>BIAN "Status": no status code of its own - the reason and when it was set.</summary>
public sealed record BianStatus
{
    public string? StatusReason { get; init; }
    public BianDatetime? StatusDateTime { get; init; }
}

public sealed record BianDate
{
    public string? DateContent { get; init; }
}

public sealed record BianDatetime
{
    public string? DateTimeContent { get; init; }
}