using System.Security.Cryptography;
using System.Text.Json;

using Duende.IdentityServer;
using Duende.IdentityServer.Models;

using Microsoft.Extensions.Configuration;

using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients.M2M;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Clients;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ FAPI 2.0 for the machines too: every Kafka subscriber proves itself with its own key
// │ (private_key_jwt - a public key at the IDP, no shared secret), and every token it gets
// │ is bound to its DPoP key (RFC 9449), so a token copied from a log or a proxy is useless.
// │ A new subscriber client copied from an old one with a secret, or without DPoP, fails
// │ here. Its grant stays client credentials with exactly one scope.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class KafkaSubscriberClients_Edge_KeyAndDPoPBound
{
    private static readonly string[] Subscribers =
    [
        "KycCaseOpeningSubscriber", "OnboardingOutcomeSubscriber", "ComplianceCaseOpeningSubscriber",
        "DocumentInvalidationSubscriber", "AccountApplicationOpeningSubscriber", "NotificationsSubscriber",
        "AccountsCommandSubscriber", "PaymentsSagaReplySubscriber"
    ];

    private static IEnumerable<TestCaseData> SubscriberClients()
    {
        yield return new TestCaseData(() => KycCaseOpeningSubscriberToCustomerKycApiM2M.Client).SetName("KYC case opening");
        yield return new TestCaseData(() => OnboardingOutcomeSubscriberToCustomerOnboardingApiM2M.Client).SetName("Onboarding outcome");
        yield return new TestCaseData(() => ComplianceCaseOpeningSubscriberToComplianceApiM2M.Client).SetName("Compliance case opening");
        yield return new TestCaseData(() => DocumentInvalidationSubscriberToDocumentsManagementApiM2M.Client).SetName("Document invalidation");
        yield return new TestCaseData(() => AccountApplicationOpeningSubscriberToAccountsApiM2M.Client).SetName("Account application opening");
        yield return new TestCaseData(() => NotificationsSubscriberToNotificationsApiM2M.Client).SetName("Notifications");
        yield return new TestCaseData(() => AccountsCommandSubscriberToAccountsApiM2M.Client).SetName("Accounts command");
        yield return new TestCaseData(() => PaymentsSagaReplySubscriberToPaymentsApiM2M.Client).SetName("Payments saga reply");
    }

    private string _keyFolder = string.Empty;

    [SetUp]
    public void Configure()
    {
        _keyFolder = Directory.CreateTempSubdirectory("ewp-idp-tests-").FullName;
        var settings = new Dictionary<string, string?>();
        foreach (var subscriber in Subscribers)
        {
            settings[$"ClientIds:{subscriber}"] = $"{subscriber}.ClientID";
            settings[$"ClientKeys:{subscriber}.ClientID"] = WritePublicJwk(subscriber);
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ClientSettings.Initialize(configuration);
        ClientKeyStore.Initialize(configuration);
        ClientSecretStore.Initialize(configuration);
    }

    [TearDown]
    public void RemoveKeys() =>
        Directory.Delete(_keyFolder, recursive: true);

    // The shape ps/pki/New-EwpClientKeys.mjs writes: the public part of an EC P-256 key only.
    private string WritePublicJwk(string subscriber)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var jwk = new Dictionary<string, string>
        {
            ["kty"] = "EC", ["crv"] = "P-256", ["use"] = "sig", ["alg"] = "ES256", ["kid"] = $"{subscriber}-test",
            ["x"] = Base64Url(point.X!), ["y"] = Base64Url(point.Y!)
        };
        var path = Path.Combine(_keyFolder, $"{subscriber}.public.jwk");
        File.WriteAllText(path, JsonSerializer.Serialize(jwk));
        return path;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [TestCaseSource(nameof(SubscriberClients))]
    public void Given_AKafkaSubscriber_When_ItsClientIsBuilt_Then_ItProvesItselfWithItsOwnKeyOnly(Func<Client> build)
    {
        // Given / When
        var client = build();

        // Then
        var secret = client.ClientSecrets.Should().ContainSingle("a second, shared secret would still let a copy of it in").Subject;
        secret.Type.Should().Be(IdentityServerConstants.SecretTypes.JsonWebKey, "private_key_jwt (FAPI 2.0), not a shared secret");
        secret.Value.Should().NotContain("\"d\"", "the IDP holds the public key only");
        client.RequireClientSecret.Should().BeTrue();
    }

    [TestCaseSource(nameof(SubscriberClients))]
    public void Given_AKafkaSubscriber_When_ItsClientIsBuilt_Then_EveryTokenIsBoundToItsDPoPKey(Func<Client> build)
    {
        // Given / When
        var client = build();

        // Then
        client.RequireDPoP.Should().BeTrue("a machine token copied from a log or a proxy must be useless (FAPI 2.0, RFC 9449)");
    }

    [TestCaseSource(nameof(SubscriberClients))]
    public void Given_AKafkaSubscriber_When_ItsClientIsBuilt_Then_ItGetsOnlyItsOwnMachineTokenForOneScope(Func<Client> build)
    {
        // Given / When
        var client = build();

        // Then
        client.AllowedGrantTypes.Should().BeEquivalentTo(GrantTypes.ClientCredentials, "no person, no browser, no exchange");
        client.AllowedScopes.Should().ContainSingle("one caller-callee purpose, least privilege");
    }
}