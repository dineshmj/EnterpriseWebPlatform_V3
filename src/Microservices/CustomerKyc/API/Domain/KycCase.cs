namespace EnterpriseWebPlatform.CustomerKyc.Api.Domain;

public sealed class KycCase
{
    private KycCase() { }

    public KycCase(string customerNumber, string status, string? initiatedByUserId)
    {
        CustomerNumber = customerNumber;
        Status = status;
        InitiatedByUserId = initiatedByUserId;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public long Id { get; private set; }

    public string CustomerNumber { get; private set; } = string.Empty;

    public string Status { get; private set; } = string.Empty;

    public string? InitiatedByUserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}