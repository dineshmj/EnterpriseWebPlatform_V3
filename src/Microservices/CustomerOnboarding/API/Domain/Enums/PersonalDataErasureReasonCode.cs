namespace EnterpriseWebPlatform.CustomerOnboarding.Domain.Enums;

/// <summary>
/// The persisted / published code of a <see cref="PersonalDataErasureReason"/>: CUSTOMER_REQUEST
/// or RETENTION_ENDED (matching ck_personal_data_erasure_requests_reason_code and the
/// Integration Event Catalogue's enum convention).
/// </summary>
public static class PersonalDataErasureReasonCode
{
    public static string ToCode(this PersonalDataErasureReason reason) => reason switch
    {
        PersonalDataErasureReason.CustomerRequest => "CUSTOMER_REQUEST",
        PersonalDataErasureReason.RetentionEnded => "RETENTION_ENDED",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown erasure reason.")
    };

    /// <summary>The reason for a code, or false for anything else (no free text gets in).</summary>
    public static bool TryFromCode(string? code, out PersonalDataErasureReason reason)
    {
        reason = code switch
        {
            "CUSTOMER_REQUEST" => PersonalDataErasureReason.CustomerRequest,
            "RETENTION_ENDED" => PersonalDataErasureReason.RetentionEnded,
            _ => default
        };
        return reason != default;
    }
}