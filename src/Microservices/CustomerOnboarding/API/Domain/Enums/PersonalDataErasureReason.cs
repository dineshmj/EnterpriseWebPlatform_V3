namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>Why a customer's personal data is to be erased: a code, never free text, so no personal data reaches the published events or the audit trail.</summary>
public enum PersonalDataErasureReason
{
    /// <summary>The customer asked to be forgotten (GDPR Art. 17, APP 13).</summary>
    CustomerRequest = 1,

    /// <summary>The period the bank must keep the record has ended (APP 11.2).</summary>
    RetentionEnded = 2
}