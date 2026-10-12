using System.Text.Json;
using System.Text.RegularExpressions;

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
///   payments (orchestrated saga; the target opens the payment's status page)
///     PaymentApprovalRequired   → staff:payments_officer:{branch}   (new work)
///     PaymentCompleted / Rejected / Failed → user:{initiator}
///     PaymentCompensationFailed → user:{initiator}, staff:payments_officer:{branch} and
///                                 staff:operations_administrator:* (the desk that retries the release)
/// </summary>
public static partial class NotificationRules
{
    public static IReadOnlyList<NotificationDraft> For(string eventType, JsonElement envelope)
    {
        var p = envelope.TryGetProperty("Payload", out var payload) ? payload : default;
        var app = Str(p, "ApplicationNumber") ?? "an application";
        var applications = ApplicationsTarget(Str(p, "ApplicationNumber"));
        var branch = Str(p, "BranchCode");
        var initiator = Str(envelope, "InitiatedByUserId");

        NotificationDraft? Progress(string title, string body, string? target) =>
            initiator is null ? null : new(NotificationAudiences.ForUser(initiator), NotificationCategory.Progress, title, body, target);

        NotificationDraft? NewWork(string role, string title, string body, string? target) =>
            branch is null ? null : new(NotificationAudiences.ForStaff(role, branch), NotificationCategory.NewWork, title, body, target);

        if (eventType.StartsWith("Payment", StringComparison.Ordinal))
            return ForPayment(eventType, p, Progress, NewWork);

        var draft = eventType switch
        {
            "KycCaseCreated" => NewWork("kyc_officer",
                "New KYC case",
                $"{Str(p, "ApplicantName") ?? Str(p, "CustomerNumber")} · {app} is waiting for KYC review.",
                Record("kyc", "/v1/kyc/cases/view-details", "caseId", Long(p, "KycCaseId"))),

            "KycCaseApproved" => Progress(
                "KYC approved",
                $"{By(p, "DecisionByLanId")} approved KYC for {Applicant(p)} ({app}). Compliance review is next.",
                applications),

            "KycCaseRejected" => Progress(
                "KYC rejected",
                $"{By(p, "DecisionByLanId")} rejected KYC for {Applicant(p)} ({app}){Reason(p, "DecisionRemarks")}",
                applications),

            "ComplianceCaseScreened" => NewWork("compliance_officer",
                Str(p, "ScreeningOutcome") == "CLEAR" ? "New compliance case" : "New compliance case - screening alert",
                $"{Str(p, "ApplicantName") ?? Str(p, "CustomerNumber")} · {app} was screened {Screening(p)} and awaits a compliance decision{Clearance(p)}.",
                Record("compliance", "/v1/compliance/cases/view-details", "caseId", Long(p, "ComplianceCaseId"))),

            "ComplianceCaseApproved" => Progress(
                "Compliance approved",
                $"{By(p, "DecisionByLanId")} cleared {Applicant(p)} ({app}) for compliance{Risk(p)}. Account opening is next.",
                applications),

            "ComplianceCaseRejected" => Progress(
                "Compliance rejected",
                $"{By(p, "DecisionByLanId")} rejected {Applicant(p)} ({app}) in compliance{Reason(p, "DecisionRemarks")}",
                applications),

            "AccountApplicationCreated" => NewWork("account_officer",
                "New account application",
                $"{Str(p, "HolderName") ?? Str(p, "CustomerNumber")} · {app} is waiting for an account decision.",
                Record("accounts", "/v1/accounts/applications/view-details", "applicationId", Long(p, "AccountApplicationId"))),

            "AccountApplicationRejected" => Progress(
                "Account opening rejected",
                $"{By(p, "DecisionByLanId")} rejected the account for {Str(p, "HolderName") ?? "the customer"} ({app}){Reason(p, "DecisionRemarks")}",
                applications),

            "AccountOpened" => Progress(
                "Account opened",
                $"Account {Str(p, "Bsb")} {Str(p, "AccountNumber")} is open for {Str(p, "HolderName") ?? "the customer"} ({app}). Onboarding is complete.",
                applications),

            "AccountOpeningFailed" => Progress(
                "Account could not be opened",
                $"Core banking could not open the account for {Str(p, "HolderName") ?? "the customer"} ({app}). The onboarding has ended and can be started again.",
                applications),

            _ => null
        };

        return draft is null ? [] : [draft];
    }

    /// <summary>
    /// The Payments saga's public facts. People are named by LAN ID; the amount, payee and
    /// payment number identify the payment; the target opens its status page.
    /// </summary>
    private static IReadOnlyList<NotificationDraft> ForPayment(
        string eventType, JsonElement p,
        Func<string, string, string?, NotificationDraft?> progress,
        Func<string, string, string, string?, NotificationDraft?> newWork)
    {
        var number = Str(p, "PaymentNumber") ?? "a payment";
        var what = $"{Amount(p)} to {Str(p, "PayeeName") ?? "the payee"} ({number})";
        var target = Record("payments", "/v1/payments/view-details", "paymentId", Long(p, "PaymentId"));
        var reason = Str(p, "Reason");

        IEnumerable<NotificationDraft?> drafts = eventType switch
        {
            "PaymentApprovalRequired" =>
            [
                newWork("payments_officer", "Payment awaiting approval",
                    $"{what} for {Str(p, "CustomerNumber") ?? "a customer"} needs a payments officer's approval. The funds are reserved.", target)
            ],
            "PaymentCompleted" =>
            [
                progress("Payment completed",
                    $"{what} was sent{(Str(p, "DecisionByLanId") is { } approver ? $" (approved by {approver})" : string.Empty)}.", target)
            ],
            "PaymentRejected" =>
            [
                progress("Payment rejected",
                    $"{what} was not made{(reason is null ? "." : $": {reason}")}", target)
            ],
            "PaymentFailed" =>
            [
                progress("Payment failed - funds released",
                    $"{what} could not be sent{(reason is null ? "." : $": {reason}")} The reserved funds are available again.", target)
            ],
            "PaymentCompensationFailed" =>
            [
                progress("Payment needs attention",
                    $"{what} was not sent, but the release of the reserved funds is not confirmed. Operations must retry it.", target),
                newWork("payments_officer", "Payment needs attention",
                    $"{what}: the release of the reserved funds is not confirmed. Operations must retry it.", target),
                new NotificationDraft(NotificationAudiences.ForStaffInAllBranches("operations_administrator"), NotificationCategory.NewWork,
                    "Release to retry",
                    $"{what} in branch {Str(p, "BranchCode") ?? "?"}: the release of the reserved funds is not confirmed. Open it and retry the release.", target)
            ],
            _ => []
        };

        return drafts.Where(d => d is not null).Select(d => d!).ToList();
    }

    private static string Amount(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("Amount", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out var amount)
            ? amount.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("en-AU"))
            : "A payment";

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
    /// allow-list). New work opens the record; progress opens the initiator's applications list
    /// with that application selected.
    /// </summary>
    private static string? Record(string mfe, string page, string idParameter, long? recordId) =>
        recordId is null ? null : JsonSerializer.Serialize(new { mfe, path = $"{page}?{idParameter}={recordId}", recordId });

    private const string ApplicationsPage = "/v1/onboarding/applications/view-all";

    /// <summary>
    /// The applications list, opened at the application the event is about. The number travels
    /// only in its exact shape (APP-yyyyMMdd-nnnnnn); anything else opens the plain list.
    /// </summary>
    private static string ApplicationsTarget(string? applicationNumber) =>
        JsonSerializer.Serialize(new
        {
            mfe = "customer-onboarding",
            path = applicationNumber is not null && ApplicationNumberShape().IsMatch(applicationNumber)
                ? $"{ApplicationsPage}?applicationNumber={applicationNumber}"
                : ApplicationsPage
        });

    [GeneratedRegex(@"^APP-\d{8}-\d{6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ApplicationNumberShape();

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