namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Contracts.Customer.CustomerCreated;

public sealed record CustomerCreatedIntegrationEvent(
    Guid MessageId,
    string EventType,
    DateTimeOffset OccurredAt,
    string CustomerNumber,
    string FirstName,
    string LastName,
    string Email,
    string CustomerType,
    string Status,
    long? BranchId);