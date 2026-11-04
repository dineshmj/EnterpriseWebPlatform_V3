using System.Security.Cryptography;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using AwesomeAssertions;

using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Subscribers;
using EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Support;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Infrastructure.Tests.Subscribers;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ A worker without its keys must not start - not run "half working" and fail message by
// │ message later. The settings are validated as the host validates them at start-up
// │ (ValidateOnStart): the key files named, readable, EC P-256, and two different keys.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class SubscriberServiceCollectionExtensions_AddCachedM2MTokenClient_Edge_FailClosed
{
    private const string Section = "TestSubscriber";

    private string _folder = string.Empty;

    [SetUp]
    public void SetUp() => _folder = Directory.CreateTempSubdirectory("ewp-subscriber-tests-").FullName;

    [TearDown]
    public void TearDown() => Directory.Delete(_folder, recursive: true);

    private static SettingsFromConfiguration Validate(KeyFileSettings clientKey, KeyFileSettings dpopKey)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{Section}:IdentityServerAuthority"] = "https://idp.test",
            [$"{Section}:ClientId"] = "Test.Subscriber.M2M.ClientID",
            [$"{Section}:ClientKey:Path"] = clientKey.Path,
            [$"{Section}:ClientKey:PasswordPath"] = clientKey.PasswordPath,
            [$"{Section}:DPoPKey:Path"] = dpopKey.Path,
            [$"{Section}:DPoPKey:PasswordPath"] = dpopKey.PasswordPath
        }).Build();

        var services = new ServiceCollection();
        services.AddOptions<SettingsFromConfiguration>().Bind(configuration.GetSection(Section));
        services.AddCachedM2MTokenClient<SettingsFromConfiguration>(Section);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<SettingsFromConfiguration>>().Value;   // runs the start-up validation
    }

    [Test]
    public void Given_TwoReadableKeys_When_TheWorkerStarts_Then_ItsSettingsAreAccepted()
    {
        // Given
        var clientKey = TestKeys.WriteKeyFile(_folder, "client");
        var dpopKey = TestKeys.WriteKeyFile(_folder, "dpop");

        // When
        var settings = Validate(clientKey, dpopKey);

        // Then
        settings.ClientKey.Path.Should().Be(clientKey.Path);
    }

    [Test]
    public void Given_NoKeyFilesNamed_When_TheWorkerStarts_Then_ItStops()
    {
        // Given / When
        var start = () => Validate(new KeyFileSettings(), new KeyFileSettings());

        // Then
        start.Should().Throw<OptionsValidationException>().WithMessage("*ClientKey*");
    }

    [Test]
    public void Given_AKeyFileThatDoesNotExist_When_TheWorkerStarts_Then_ItStops()
    {
        // Given
        var missing = new KeyFileSettings { Path = Path.Combine(_folder, "missing.key.pem"), PasswordPath = Path.Combine(_folder, "missing.password") };

        // When
        var start = () => Validate(missing, TestKeys.WriteKeyFile(_folder, "dpop"));

        // Then
        start.Should().Throw<Exception>("a worker without its key could never get a token");
    }

    [Test]
    public void Given_AKeyOnAnotherCurve_When_TheWorkerStarts_Then_ItStops()
    {
        // Given
        var p384 = TestKeys.WriteKeyFile(_folder, "client", ECCurve.NamedCurves.nistP384);

        // When
        var start = () => Validate(p384, TestKeys.WriteKeyFile(_folder, "dpop"));

        // Then
        start.Should().Throw<InvalidOperationException>().WithMessage("*P-256*");
    }

    [Test]
    public void Given_TheSameKeyFileForBoth_When_TheWorkerStarts_Then_ItStops()
    {
        // Given
        var one = TestKeys.WriteKeyFile(_folder, "only");

        // When
        var start = () => Validate(one, one);

        // Then
        start.Should().Throw<InvalidOperationException>().WithMessage("*DPoP key must not be the client key*");
    }

    /// <summary>A subscriber's settings bound from configuration, as each worker's options class is.</summary>
    public sealed class SettingsFromConfiguration : IM2MClientSettings
    {
        public string IdentityServerAuthority { get; init; } = string.Empty;

        public string ClientId { get; init; } = string.Empty;

        public KeyFileSettings ClientKey { get; init; } = new();

        public KeyFileSettings DPoPKey { get; init; } = new();

        public string Scope { get; init; } = "test.write";
    }
}