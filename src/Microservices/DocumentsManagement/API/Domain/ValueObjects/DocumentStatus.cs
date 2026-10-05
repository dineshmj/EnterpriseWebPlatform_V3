namespace EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

/// <summary>
/// Lifecycle of a stored document. ATTACHED: submitted as evidence of an onboarding
/// application, so it is a KYC record and must be retained. INVALIDATED is a business
/// compensation: the document is no longer valid evidence, but it is retained (never
/// deleted) for audit and regulatory retention. Only an AVAILABLE document is deletable.
/// </summary>
public enum DocumentStatus
{
    Available = 1,
    Invalidated = 2,
    Attached = 3
}

public static class DocumentStatusCode
{
    public static string ToCode(this DocumentStatus status) => status switch
    {
        DocumentStatus.Available => "AVAILABLE",
        DocumentStatus.Invalidated => "INVALIDATED",
        DocumentStatus.Attached => "ATTACHED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static DocumentStatus FromCode(string code) => code switch
    {
        "AVAILABLE" => DocumentStatus.Available,
        "INVALIDATED" => DocumentStatus.Invalidated,
        "ATTACHED" => DocumentStatus.Attached,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown document status.")
    };
}