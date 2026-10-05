namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Kafka;

public static class KafkaTopicNames
{
    public const string CustomerCreated = "customer.created";

    public const string OnboardingApplicationSubmitted =
        "onboarding.application.submitted";

    public const string OnboardingApplicationStatusChanged =
        "onboarding.application.status.changed";

    /// <summary>
    /// A verifying context rejected an onboarding application (saga business failure);
    /// carries the evidence document IDs for compensation by Documents Management.
    /// </summary>
    public const string OnboardingApplicationRejected =
        "onboarding.application.rejected";

    public const string KycCaseCreated = "kyc.case.created";

    public const string KycCaseApproved = "kyc.case.approved";

    public const string KycCaseRejected = "kyc.case.rejected";

    public const string ComplianceCaseCreated = "compliance.case.created";

    public const string ComplianceCaseApproved = "compliance.case.approved";

    public const string ComplianceCaseRejected = "compliance.case.rejected";

    public const string AccountApplicationCreated = "accounts.application.created";

    public const string AccountApplicationRejected = "accounts.application.rejected";

    public const string AccountOpened = "accounts.account.opened";

    public const string AccountOpeningFailed = "accounts.account.opening.failed";

    /// <summary>
    /// Dead-letter topic of the Account Application Opening Subscriber (Accounts):
    /// compliance.case.approved messages that can never open an account application.
    /// </summary>
    public const string AccountApplicationOpeningSubscriberDeadLetter =
        "accounts.application-opening-subscriber.dlq";

    /// <summary>
    /// Dead-letter topic of the Compliance Case Opening Subscriber (Compliance):
    /// kyc.case.approved messages that can never open a compliance case.
    /// </summary>
    public const string ComplianceCaseOpeningSubscriberDeadLetter =
        "compliance.case-opening-subscriber.dlq";

    /// <summary>
    /// Dead-letter topic of the Onboarding Outcome Subscriber: messages it can
    /// never process (malformed, unknown, or permanently rejected) are parked here
    /// with diagnostic headers instead of blocking the partition.
    /// </summary>
    public const string OnboardingOutcomeSubscriberDeadLetter =
        "customer-onboarding.outcome-subscriber.dlq";

    /// <summary>
    /// Dead-letter topic of the KYC Case Opening Subscriber (Customer KYC):
    /// onboarding.application.submitted messages that can never open a case.
    /// </summary>
    public const string KycCaseOpeningSubscriberDeadLetter =
        "customer-kyc.case-opening-subscriber.dlq";

    /// <summary>
    /// Dead-letter topic of the Document Invalidation Subscriber (Documents Management):
    /// onboarding.application.rejected messages that can never be compensated.
    /// </summary>
    public const string DocumentInvalidationSubscriberDeadLetter =
        "documents-management.invalidation-subscriber.dlq";

    /// <summary>
    /// Dead-letter topic of the Notifications Subscriber: workflow events that can never
    /// become notifications (malformed, or permanently refused by the Notifications API).
    /// </summary>
    public const string NotificationsSubscriberDeadLetter =
        "notifications.subscriber.dlq";
}