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
///     ComplianceCaseScreened    → staff:compliance_officer:{branch}   (when screening is done:
///                                 the case is ready for a decision; at creation nobody can act yet)
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
                Record("kyc", "/v1/kyc/cases/view-details", "caseId", Long(p, "KycCaseId"))),

            "KycCaseApproved" => Progress(
                "KYC approved",
                $"{By(p, "DecisionByLanId")} approved KYC for {Applicant(p)} ({app}). Compliance review is next.",
                Applications),

            "KycCaseRejected" => Progress(
                "KYC rejected",
                $"{By(p, "DecisionByLanId")} rejected KYC for {Applicant(p)} ({app}){Reason(p, "DecisionRemarks")}",
                Applications),

            "ComplianceCaseScreened" => NewWork("compliance_officer",
                Str(p, "ScreeningOutcome") == "CLEAR" ? "New compliance case" : "New compliance case - screening alert",
                $"{Str(p, "ApplicantName") ?? Str(p, "CustomerNumber")} · {app} was screened {Screening(p)} and awaits a compliance decision{Clearance(p)}.",
                Record("compliance", "/v1/compliance/cases/view-details", "caseId", Long(p, "ComplianceCaseId"))),

            "ComplianceCaseApproved" => Progress(
                "Compliance approved",
                $"{By(p, "DecisionByLanId")} cleared {Applicant(p)} ({app}) for compliance{Risk(p)}. Account opening is next.",
                Applications),

            "ComplianceCaseRejected" => Progress(
                "Compliance rejected",
                $"{By(p, "DecisionByLanId")} rejected {Applicant(p)} ({app}) in compliance{Reason(p, "DecisionRemarks")}",
                Applications),

            "AccountApplicationCreated" => NewWork("account_officer",
                "New account application",
                $"{Str(p, "HolderName") ?? Str(p, "CustomerNumber")} · {app} is waiting for an account decision.",
                Record("accounts", "/v1/accounts/applications/view-details", "applicationId", Long(p, "AccountApplicationId"))),

            "AccountApplicationRejected" => Progress(
                "Account opening rejected",
                $"{By(p, "DecisionByLanId")} rejected the account for {Str(p, "HolderName") ?? "the customer"} ({app}){Reason(p, "DecisionRemarks")}",
                Applications),

            "AccountOpened" => Progress(
                "Account opened",
                $"Account {Str(p, "Bsb")} {Str(p, "AccountNumber")} is open for {Str(p, "HolderName") ?? "the customer"} ({app}). Onboarding is complete.",
                Applications),

            "AccountOpeningFailed" => Progress(
                "Account could not be opened",
                $"Core banking could not open the account for {Str(p, "HolderName") ?? "the customer"} ({app}). The onboarding has ended and can be started again.",
                Applications),

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

    private static string Screening(JsonElement p) =>
        (Str(p, "ScreeningOutcome"), Str(p, "RiskRating")) switch
        {
            ("CLEAR", var risk) => $"clear (risk {risk?.ToLowerInvariant() ?? "unknown"})",
            (var outcome, var risk) => $"with a {outcome?.ToLowerInvariant().Replace('_', ' ') ?? "result"} (risk {risk?.ToLowerInvariant() ?? "unknown"})"
        };

    private static string Clearance(JsonElement p) =>
        Long(p, "RequiredClearance") is >= 5 ? " - approval needs clearance level 5" : string.Empty;

    /// <summary>
    /// What a click opens: a path of the owning MFE (the Shell opens it only through a
    /// microservice in the person's own menu, and the MFE's BFF checks it against its
    /// allow-list). New work opens the record; progress opens the initiator's application list.
    /// </summary>
    private static string? Record(string mfe, string page, string idParameter, long? recordId) =>
        recordId is null ? null : JsonSerializer.Serialize(new { mfe, path = $"{page}?{idParameter}={recordId}", recordId });

    private static readonly string Applications =
        JsonSerializer.Serialize(new { mfe = "customer-onboarding", path = "/v1/onboarding/applications/view-all" });

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