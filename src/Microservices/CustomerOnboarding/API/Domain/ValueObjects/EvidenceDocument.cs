using EnterpriseWebPlatform.CustomerOnboarding.Domain.Exceptions;

namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.ValueObjects;

/// <summary>
/// A document submitted as evidence with an onboarding application, referenced by
/// its Documents Management ID (DM owns the document; CO only remembers which ones
/// belong to the application, so a rejection can name exactly those documents).
/// </summary>
public sealed class EvidenceDocument
{
    public const int DocumentTypeMaxLength = 100;

    // For EF Core materialization.
    private EvidenceDocument()
    {
        DocumentType = null!;
    }

    private EvidenceDocument(Guid documentId, string documentType)
    {
        DocumentId = documentId;
        DocumentType = documentType;
    }

    public Guid DocumentId { get; private set; }

    /// <summary>The caller-supplied classification, e.g. <c>KYCProof</c> or <c>TaxProof</c>.</summary>
    public string DocumentType { get; private set; }

    public static EvidenceDocument Create(Guid documentId, string? documentType)
    {
        if (documentId == Guid.Empty)
            throw new DomainRuleViolationException("An evidence document needs its Documents Management ID.");

        if (string.IsNullOrWhiteSpace(documentType))
            throw new DomainRuleViolationException("An evidence document needs a document type.");

        var type = documentType.Trim();
        if (type.Length > DocumentTypeMaxLength)
            throw new DomainRuleViolationException($"A document type cannot exceed {DocumentTypeMaxLength} characters.");

        return new EvidenceDocument(documentId, type);
    }
}