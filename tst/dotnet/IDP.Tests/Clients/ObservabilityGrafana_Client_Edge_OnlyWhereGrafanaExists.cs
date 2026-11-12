using Duende.IdentityServer.Models;

using Microsoft.Extensions.Configuration;

using AwesomeAssertions;

using EnterpriseWebPlatform.IdentityServer.ConfigRegistration.Clients;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Clients;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ The cloud demo's Grafana signs in through the IDP: the client exists only where Grafana
// │ does (ClientBaseUrls:Grafana), redirects only to Grafana's own https callback, may only
// │ sign a person in (no API scope, no token exchange, no refresh token) - and asks for the
// │ roles Grafana admits people by.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class ObservabilityGrafana_Client_Edge_OnlyWhereGrafanaExists
{
    private static void Configure(string? grafanaBaseUrl)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ClientIds:Grafana"] = "Grafana.ClientID",
            ["ClientSecrets:Grafana.ClientID"] = "test-only-secret",
            ["ClientBaseUrls:Grafana"] = grafanaBaseUrl
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        ClientSettings.Initialize(configuration);
        ClientSecretStore.Initialize(configuration);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Given_NoGrafanaAddress_When_TheClientsAreBuilt_Then_ThereIsNoGrafanaClient(string? baseUrl)
    {
        // Given
        Configure(baseUrl);

        // When / Then
        ObservabilityGrafana.IsConfigured.Should().BeFalse();
    }

    [Test]
    public void Given_GrafanasAddress_When_ItsClientIsBuilt_Then_ItOnlySignsAPersonInBackToGrafana()
    {
        // Given
        Configure("https://grafana.demo.ewp.example");

        // When
        var client = ObservabilityGrafana.Client;

        // Then
        ObservabilityGrafana.IsConfigured.Should().BeTrue();
        client.ClientId.Should().Be("Grafana.ClientID");
        client.AllowedGrantTypes.Should().Equal(GrantType.AuthorizationCode);
        client.RequirePkce.Should().BeTrue();
        client.AllowOfflineAccess.Should().BeFalse();
        client.RedirectUris.Should().Equal("https://grafana.demo.ewp.example/login/generic_oauth");
        // email: Grafana refuses a sign-in without it (it would ask a GitHub-only /emails address).
        client.AllowedScopes.Should().BeEquivalentTo("openid", "profile", "email", "roles");
        client.ClientSecrets.Should().ContainSingle();
    }

    [Test]
    public void Given_APlainHttpGrafanaAddress_When_ItsClientIsBuilt_Then_TheIdpRefusesIt()
    {
        // Given
        Configure("http://grafana.demo.ewp.example");

        // When
        var build = () => ObservabilityGrafana.Client;

        // Then
        build.Should().Throw<Exception>().WithMessage("*https*");
    }
}