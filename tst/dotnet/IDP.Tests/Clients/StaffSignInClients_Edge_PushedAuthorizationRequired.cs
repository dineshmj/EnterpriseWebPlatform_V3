using Duende.IdentityServer.Models;

using Microsoft.Extensions.Configuration;

using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;
using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.MFEs;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Clients;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ FAPI 2.0 for the staff channel: the Shell and every web app sign people in with PAR
// │ only (RFC 9126) - an authorization request sent through the browser is refused - plus
// │ PKCE and a short-lived request_uri. A new web app copied without PAR fails here.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class StaffSignInClients_Edge_PushedAuthorizationRequired
{
    private static readonly string[] WebApps =
    [
        "Shell", "CustomerOnboardingWeb", "CustomerKycWeb", "ComplianceWeb", "AccountsWeb",
        "PaymentsWeb", "AuditWeb", "OperationsWeb", "PartnerProductsWeb"
    ];

    private static IEnumerable<TestCaseData> StaffClients()
    {
        yield return new TestCaseData(() => BssClient.Client).SetName("Shell");
        yield return new TestCaseData(() => MfeCustomerOnboarding.Client).SetName("Customer Onboarding");
        yield return new TestCaseData(() => MfeCustomerKyc.Client).SetName("Customer KYC");
        yield return new TestCaseData(() => MfeCompliance.Client).SetName("Compliance");
        yield return new TestCaseData(() => MfeAccounts.Client).SetName("Accounts");
        yield return new TestCaseData(() => MfePayments.Client).SetName("Payments");
        yield return new TestCaseData(() => MfeAudit.Client).SetName("Audit");
        yield return new TestCaseData(() => MfeOperations.Client).SetName("Operations");
        yield return new TestCaseData(() => MfePartnerProducts.Client).SetName("Partner Products");
    }

    [SetUp]
    public void Configure()
    {
        var settings = new Dictionary<string, string?>();
        foreach (var app in WebApps)
        {
            settings[$"ClientIds:{app}"] = $"{app}.ClientID";
            settings[$"ClientSecrets:{app}.ClientID"] = "test-only-secret";
            settings[$"ClientBaseUrls:{app}"] = $"https://{app.ToLowerInvariant()}.ewp.example";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ClientSettings.Initialize(configuration);
        ClientSecretStore.Initialize(configuration);
    }

    [TestCaseSource(nameof(StaffClients))]
    public void Given_AStaffWebApp_When_ItsClientIsBuilt_Then_ItSignsInOnlyThroughAPushedRequest(Func<Client> build)
    {
        // Given / When
        var client = build();

        // Then
        client.AllowedGrantTypes.Should().Contain(GrantType.AuthorizationCode);
        client.RequirePushedAuthorization.Should().BeTrue("the browser must carry only a request_uri (FAPI 2.0)");
        client.PushedAuthorizationLifetime.Should().Be(60);
        client.RequirePkce.Should().BeTrue();
        client.AllowPlainTextPkce.Should().BeFalse();
    }
}