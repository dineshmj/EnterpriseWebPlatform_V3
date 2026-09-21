namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

public sealed record CustomerCreatedIntegrationEvent(
    long CustomerId,
    string CustomerNumber,
    Guid? SubjectId,
    string CustomerType,
    string Status);
