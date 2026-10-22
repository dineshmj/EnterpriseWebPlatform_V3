using Microsoft.Extensions.Configuration;

namespace EnterpriseWebPlatform.Common.WebUtilities.Configuration;

// ┌─ :: ARCHITECTURE + SECURITY :: Client IDs per environment, from configuration ─────────────────
// │ Why : The IDP issues the client IDs, and they differ per environment. Compiled into a shared
// │       library, every change to one forced a rebuild and redeployment of every component.
// │ How : A deployable reads ONLY the client IDs it checks (authorization pins) or names, from its
// │       own section "ClientIds", and Require(...) reads them at start-up: a missing one stops it.
// │       A pin must never be empty - an empty client ID would match nothing at best and hide a
// │       misconfiguration at worst.
// │ Ref : Blueprint §7 (independent deployability) | Authorization-Model | OWASP A05 |
// │       RFC 8693 (act)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// The OAuth client IDs a deployable relies on, from its configuration section
/// <c>ClientIds</c> (e.g. <c>"ClientIds": { "OperationsJourneyApi": "Operations.JourneyApi.ClientID" }</c>).
/// Reading a client ID that is not configured throws; call <see cref="Require"/> at start-up
/// with every client ID the deployable uses.
/// </summary>
public sealed class ClientIds
{
    public const string SectionName = "ClientIds";

    private readonly IConfigurationSection _section;

    private ClientIds(IConfigurationSection section) => _section = section;

    public static ClientIds From(IConfiguration configuration) =>
        new(configuration.GetSection(SectionName));

    /// <summary>Reads the named client IDs now, so a missing one stops the start-up.</summary>
    public ClientIds Require(params string[] names)
    {
        foreach (var name in names)
            _ = Of(name);
        return this;
    }

    // Web BFFs (authorization code + PKCE; they exchange a person's token).
    public string Shell => Of(nameof(Shell));
    public string CustomerOnboardingWeb => Of(nameof(CustomerOnboardingWeb));
    public string CustomerKycWeb => Of(nameof(CustomerKycWeb));
    public string ComplianceWeb => Of(nameof(ComplianceWeb));
    public string AccountsWeb => Of(nameof(AccountsWeb));
    public string PaymentsWeb => Of(nameof(PaymentsWeb));
    public string AuditWeb => Of(nameof(AuditWeb));
    public string OperationsWeb => Of(nameof(OperationsWeb));
    public string PartnerProductsWeb => Of(nameof(PartnerProductsWeb));

    // The customers' own channel ("My EWP", FAPI 2.0: PAR, private_key_jwt, DPoP) and its Journey API.
    public string SelfServiceWeb => Of(nameof(SelfServiceWeb));
    public string SelfServiceJourneyApi => Of(nameof(SelfServiceJourneyApi));

    // Journey APIs (token exchange only: they always act for a person).
    public string AuditJourneyApi => Of(nameof(AuditJourneyApi));
    public string OperationsJourneyApi => Of(nameof(OperationsJourneyApi));
    public string PartnerProductsJourneyApi => Of(nameof(PartnerProductsJourneyApi));

    // Machine identities (client credentials): the Kafka subscribers and the Temporal worker.
    public string KycCaseOpeningSubscriber => Of(nameof(KycCaseOpeningSubscriber));
    public string OnboardingOutcomeSubscriber => Of(nameof(OnboardingOutcomeSubscriber));
    public string ComplianceCaseOpeningSubscriber => Of(nameof(ComplianceCaseOpeningSubscriber));
    public string DocumentInvalidationSubscriber => Of(nameof(DocumentInvalidationSubscriber));
    public string AccountApplicationOpeningSubscriber => Of(nameof(AccountApplicationOpeningSubscriber));
    public string NotificationsSubscriber => Of(nameof(NotificationsSubscriber));
    public string AccountsCommandSubscriber => Of(nameof(AccountsCommandSubscriber));
    public string PaymentsSagaReplySubscriber => Of(nameof(PaymentsSagaReplySubscriber));
    public string PartnerProductsWorker => Of(nameof(PartnerProductsWorker));

    // The IDP itself, asking Customer Onboarding during a customer's online registration.
    public string IdpCustomerDirectory => Of(nameof(IdpCustomerDirectory));

    private string Of(string name) =>
        RequiredSetting.Text(_section[name], $"{SectionName}:{name}");
}