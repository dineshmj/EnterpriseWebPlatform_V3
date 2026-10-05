namespace EnterpriseWebPlatform.DocumentsManagement.Domain.ValueObjects;

/// <summary>
/// Lifecycle of a stored document. INVALIDATED is a business compensation: the
/// document is no longer valid evidence, but it is retained (never deleted) for
/// audit and regulatory retention.
/// </summary>
public enum DocumentStatus
{
    Available = 1,
    Invalidated = 2
}

public static class DocumentStatusCode
{
    public static string ToCode(this DocumentStatus status) => status switch
    {
        DocumentStatus.Available => "AVAILABLE",
        DocumentStatus.Invalidated => "INVALIDATED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static DocumentStatus FromCode(string code) => code switch
    {
        "AVAILABLE" => DocumentStatus.Available,
        "INVALIDATED" => DocumentStatus.Invalidated,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown document status.")
    };
}