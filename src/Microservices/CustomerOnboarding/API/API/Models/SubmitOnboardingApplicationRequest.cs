using System.ComponentModel.DataAnnotations;

namespace EnterpriseWebPlatform.CustomerOnboarding.API.Models;

public sealed class SubmitOnboardingApplicationRequest
{
    [Range(1, long.MaxValue)]
    public long ExpectedVersion { get; init; }

    /// <summary>The evidence uploaded to Documents Management for this application.</summary>
    [Required]
    [MinLength(1)]
    public List<EvidenceDocumentRequest> EvidenceDocuments { get; init; } = [];
}

public sealed class EvidenceDocumentRequest
{
    [Required]
    public Guid DocumentId { get; init; }

    [Required]
    [MaxLength(100)]
    public string DocumentType { get; init; } = string.Empty;
}