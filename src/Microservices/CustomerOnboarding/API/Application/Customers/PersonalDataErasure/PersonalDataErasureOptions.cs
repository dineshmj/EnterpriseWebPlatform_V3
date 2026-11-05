using EnterpriseWebPlatform.CustomerOnboarding.Domain.Aggregates;

namespace EnterpriseWebPlatform.CustomerOnboarding.Application.Customers.PersonalDataErasure;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The retention period is a legal setting, not code: production keeps a prospect's
// │ record 7 years (2557 days) after its last change; the laptop uses 0 so the demo and
// │ Playwright can erase a customer made a minute ago. Both values are required - a
// │ missing one stops the API instead of silently allowing (or blocking) every erasure.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>Configuration "PersonalData:Erasure".</summary>
public sealed class PersonalDataErasureOptions
{
    public const string SectionName = "PersonalData:Erasure";

    /// <summary>Days the record must be kept after its last change before it may be erased (0 or more).</summary>
    public int? RetentionDays { get; init; }

    /// <summary>Days a request waits for a second person before it expires (1 to 30).</summary>
    public int? RequestValidDays { get; init; }

    public bool IsValid() =>
        RetentionDays is >= 0 and <= 36_500 &&
        RequestValidDays is >= 1 and <= 30;

    public PersonalDataErasurePolicy ToPolicy() =>
        new(TimeSpan.FromDays(RetentionDays!.Value), TimeSpan.FromDays(RequestValidDays!.Value));
}