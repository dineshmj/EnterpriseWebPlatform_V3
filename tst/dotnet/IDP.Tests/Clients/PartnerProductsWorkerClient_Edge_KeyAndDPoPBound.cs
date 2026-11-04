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
// │ FAPI 2.0 for the Partner Products Worker (Temporal) too: it proves itself with its own
// │ key (private_key_jwt - a public key at the IDP, no shared secret), every token it gets
// │ is bound to its DPoP key (RFC 9449), and it gets only its own machine tokens for the
// │ two APIs it calls - never a person's.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PartnerProductsWorkerClient_Edge_KeyAndDPoPBound
{
    private string _keyFolder = string.Empty;

    [SetUp]
    public void Configure()
    {
        _keyFolder = Directory.CreateTempSubdirectory("ewp-idp-tests-").FullName;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ClientIds:PartnerProductsWorker"] = "PartnerProductsWorker.ClientID",
            ["ClientKeys:PartnerProductsWorker.ClientID"] = WritePublicJwk()
        }).Build();
        ClientSettings.Initialize(configuration);
        ClientKeyStore.Initialize(configuration);
    }

    [TearDown]
    public void RemoveKeys() =>
        Directory.Delete(_keyFolder, recursive: true);

    // The shape ps/pki/New-EwpClientKeys.mjs writes: the public part of an EC P-256 key only.
    private string WritePublicJwk()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = key.ExportParameters(includePrivateParameters: false).Q;
        var jwk = new Dictionary<string, string>
        {
            ["kty"] = "EC", ["crv"] = "P-256", ["use"] = "sig", ["alg"] = "ES256", ["kid"] = "worker-test",
            ["x"] = Base64Url(point.X!), ["y"] = Base64Url(point.Y!)
        };
        var path = Path.Combine(_keyFolder, "worker.public.jwk");
        File.WriteAllText(path, JsonSerializer.Serialize(jwk));
        return path;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Test]
    public void Given_TheWorker_When_ItsClientIsBuilt_Then_ItProvesItselfWithItsOwnKeyOnly()
    {
        // Given / When
        var client = PartnerProductsWorkerM2M.Client;

        // Then
        var secret = client.ClientSecrets.Should().ContainSingle("a second, shared secret would still let a copy of it in").Subject;
        secret.Type.Should().Be(IdentityServerConstants.SecretTypes.JsonWebKey, "private_key_jwt (FAPI 2.0), not a shared secret");
        secret.Value.Should().NotContain("\"d\"", "the IDP holds the public key only");
        client.RequireClientSecret.Should().BeTrue();
    }

    [Test]
    public void Given_TheWorker_When_ItsClientIsBuilt_Then_EveryTokenIsBoundToItsDPoPKey()
    {
        // Given / When
        var client = PartnerProductsWorkerM2M.Client;

        // Then
        client.RequireDPoP.Should().BeTrue("a machine token copied from a log or a proxy must be useless (FAPI 2.0, RFC 9449)");
    }

    [Test]
    public void Given_TheWorker_When_ItsClientIsBuilt_Then_ItGetsOnlyItsOwnMachineTokensForItsTwoApis()
    {
        // Given / When
        var client = PartnerProductsWorkerM2M.Client;

        // Then
        client.AllowedGrantTypes.Should().BeEquivalentTo(GrantTypes.ClientCredentials, "no person, no browser, no exchange");
        client.AllowedScopes.Should().BeEquivalentTo("payments.read", "partner-products.write");
    }
}