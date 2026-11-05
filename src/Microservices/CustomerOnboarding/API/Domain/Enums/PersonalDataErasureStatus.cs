namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>Where an erasure request stands: PENDING until a second person decides it, or it expires.</summary>
public enum PersonalDataErasureStatus
{
    Pending = 1,

    /// <summary>Approved by a second person: the customer's key was destroyed.</summary>
    Erased = 2,

    Rejected = 3,

    /// <summary>Nobody decided it in time; a new request may be made.</summary>
    Expired = 4
}