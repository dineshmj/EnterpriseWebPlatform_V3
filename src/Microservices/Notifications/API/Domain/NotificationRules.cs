using System.Text.Json;

namespace EnterpriseWebPlatform.Notifications.Api.Domain;

/// <summary>
/// The Notifications context's rules: which workflow event tells whom what. Reads the
/// published contracts as a tolerant reader (only the fields it needs). People are named
/// by LAN ID, customers by name and application number. An event that tells nobody
/// returns no drafts.
///
///   new work (the branch's officers of the next step)
///     KycCaseCreated            → staff:kyc_officer:{branch}
///     ComplianceCaseCreated     → staff:compliance_officer:{branch}
///     AccountApplicationCreated → staff:account_officer:{branch}
///   progress (the person who started the onboarding)
///     KycCaseApproved / Rejected, ComplianceCaseApproved / Rejected,
///     AccountApplicationRejected, AccountOpened, AccountOpeningFailed → user:{initiator}
/// </summary>
public static class NotificationRules
{
    public static IReadOnlyList<NotificationDraft> For(string eventType, JsonElement envelope)
    {
        var p = envelope.TryGetProperty("Payload", out var payload) ? payload : default;
        var app = Str(p, "ApplicationNumber") ?? "an application";
        var branch = Str(p, "BranchCode");
        var initiator = Str(envelope, "InitiatedByUserId");

        NotificationDraft? Progress(string title, string body, string? target) =>
            initiator is null ? null : new(NotificationAudiences.ForUser(initiator), NotificationCategory.Progress, title, body, target);

        NotificationDraft? NewWork(string role, string title, string body, string? target) =>
            branch is null ? null : new(NotificationAudiences.ForStaff(role, branch), NotificationCategory.NewWork, title, body, target);

        var draft = eventType switch
        {
            "KycCaseCreated" => NewWork("kyc_officer",
                "New KYC case",
                $"{Str(p, "ApplicantName") ?? Str(p, "CustomerNumber")} · {app} is waiting for KYC review.",
                Target("kyc", "cases/view-details", Long(p, "KycCaseId"))),

            "KycCaseApproved" => Progress(
                "KYC approved",
                $"{By(p, "DecisionByLanId")} approved KYC for {Applicant(p)} ({app}). Compliance review is next.",
                null),

            "KycCaseRejected" => Progress(
                "KYC rejected",
                $"{By(p, "DecisionByLanId")} rejected KYC for {Applicant(p)} ({app}){Reason(p, "DecisionRemarks")}",
                null),

            "ComplianceCaseCreated" => NewWork("compliance_officer",
                "New compliance case",
                $"{Str(p, "ApplicantName") ?? Str(p, "CustomerNumber")} · {app} is being screened and will need a compliance decision.",
                Target("compliance", "cases/view-details", Long(p, "ComplianceCaseId"))),

            "ComplianceCaseApproved" => Progress(
                "Compliance approved",
                $"{By(p, "DecisionByLanId")} cleared {Applicant(p)} ({app}) for compliance{Risk(p)}. Account opening is next.",
                null),

            "ComplianceCaseRejected" => Progress(
                "Compliance rejected",
                $"{By(p, "DecisionByLanId")} rejected {Applicant(p)} ({app}) in compliance{Reason(p, "DecisionRemarks")}",
                null),

            "AccountApplicationCreated" => NewWork("account_officer",
                "New account application",
                $"{Str(p, "HolderName") ?? Str(p, "CustomerNumber")} · {app} is waiting for an account decision.",
                Target("accounts", "applications/view-details", Long(p, "AccountApplicationId"))),

            "AccountApplicationRejected" => Progress(
                "Account opening rejected",
                $"{By(p, "DecisionByLanId")} rejected the account for {Str(p, "HolderName") ?? "the customer"} ({app}){Reason(p, "DecisionRemarks")}",
                null),

            "AccountOpened" => Progress(
                "Account opened",
                $"Account {Str(p, "Bsb")} {Str(p, "AccountNumber")} is open for {Str(p, "HolderName") ?? "the customer"} ({app}). Onboarding is complete.",
                null),

            "AccountOpeningFailed" => Progress(
                "Account could not be opened",
                $"Core banking could not open the account for {Str(p, "HolderName") ?? "the customer"} ({app}). The onboarding has ended and can be started again.",
                null),

            _ => null
        };

        return draft is null ? [] : [draft];
    }

    private static string Applicant(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Applicant", out var a) && a.ValueKind == JsonValueKind.Object
            ? $"{Str(a, "FirstName")} {Str(a, "LastName")}".Trim()
            : "the customer";

    private static string By(JsonElement p, string lanIdField) => Str(p, lanIdField) ?? "An officer";

    private static string Reason(JsonElement p, string field) =>
        Str(p, field) is { Length: > 0 } reason ? $": \"{reason}\"" : ".";

    private static string Risk(JsonElement p) =>
        Str(p, "RiskRating") is { } risk ? $" (risk {risk.ToLowerInvariant()})" : string.Empty;

    private static string? Target(string mfe, string page, long? recordId) =>
        recordId is null ? null : JsonSerializer.Serialize(new { mfe, page, recordId });

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static long? Long(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var number)
            ? number
            : null;
}