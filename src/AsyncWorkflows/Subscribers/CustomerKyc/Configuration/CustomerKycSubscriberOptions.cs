namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.CustomerKyc.CustomerKycSubscriber.Configuration;

public sealed class CustomerKycSubscriberOptions
{
    public const string SectionName = "CustomerKycSubscriber";

    public string Topic { get; init; } = "customer.created";
    public string GroupId { get; init; } = "customer-kyc-subscriber";
}
