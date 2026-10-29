using System.Security.Cryptography;
using System.Text.Json;

using Duende.IdentityServer;
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
// │ PKCE and a short-lived request_uri, and prove themselves with private_key_jwt (a public
// │ key at the IDP, no shared secret). A new web app copied without either fails here.
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

    private string _keyFolder = string.Empty;

    [SetUp]
    public void Configure()
    {
        _keyFolder = Directory.CreateTempSubdirectory("ewp-idp-tests-").FullName;
        var settings = new Dictionary<string, string?>();
        foreach (var app in WebApps)
        {
            settings[$"ClientIds:{app}"] = $"{app}.ClientID";
            settings[$"ClientKeys:{app}.ClientID"] = WritePublicJwk(app);
            settings[$"ClientBaseUrls:{app}"] = $"https://{app.ToLowerInvariant()}.ewp.example";
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ClientSettings.Initialize(configuration);
        ClientKeyStore.Initialize(configuration);
    }

    [TearDown]
    public void RemoveKeys() =>
        Directory.Delete(_keyFolder, recursive: true);

    // The shape ps/pki/New-EwpClientKeys.mjs writes: the public part of an EC P-256 key only.
    private string WritePublicJwk(string app)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var jwk = new Dictionary<string, string>
        {
            ["kty"] = "EC", ["crv"] = "P-256", ["use"] = "sig", ["alg"] = "ES256", ["kid"] = $"{app}-test",
            ["x"] = Base64Url(point.X!), ["y"] = Base64Url(point.Y!)
        };
        var path = Path.Combine(_keyFolder, $"{app}.public.jwk");
        File.WriteAllText(path, JsonSerializer.Serialize(jwk));
        return path;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

    [TestCaseSource(nameof(StaffClients))]
    public void Given_AStaffWebApp_When_ItsClientIsBuilt_Then_ItProvesItselfWithItsOwnKeyOnly(Func<Client> build)
    {
        // Given / When
        var client = build();

        // Then
        var secret = client.ClientSecrets.Should().ContainSingle("a second, shared secret would still let a copy of it in").Subject;
        secret.Type.Should().Be(IdentityServerConstants.SecretTypes.JsonWebKey, "private_key_jwt (FAPI 2.0), not a shared secret");
        secret.Value.Should().NotContain("\"d\"", "the IDP holds the public key only");
        client.RequireClientSecret.Should().BeTrue();
    }
}