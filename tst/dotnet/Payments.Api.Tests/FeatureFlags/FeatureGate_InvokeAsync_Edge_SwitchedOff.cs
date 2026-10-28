using System.Reflection;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

using AwesomeAssertions;
using OpenFeature;
using OpenFeature.Model;
using OpenFeature.Providers.Flagd;

using OpenFeatureApi = OpenFeature.Api;

using EnterpriseWebPlatform.Common.WebUtilities.FeatureFlags;
using EnterpriseWebPlatform.Payments.Api.Controllers;

namespace EnterpriseWebPlatform.Payments.Api.Tests.FeatureFlags;

/// <summary>
/// The BIAN /v2 release toggle (Common/WebUtilities/FeatureFlags), read from a real flag file by
/// flagd's file resolver - the same path the APIs take on a developer's machine.
/// </summary>
[TestFixture]
[Category("Resilience")]
public sealed class FeatureGate_InvokeAsync_Edge_SwitchedOff
{
    private readonly List<string> _files = [];

    [TearDown]
    public void DeleteFlagFiles() => _files.ForEach(File.Delete);

    [Test]
    public async Task Given_TheFlagIsOn_When_AGatedEndpointIsCalled_Then_TheRequestGoesOn()
    {
        // Given
        var flags = await FlagsFromFileAsync(FlagFile(bianV2: "on"));
        var (middleware, reached) = Gate(flags);

        // When
        var context = RequestTo(new FeatureGateAttribute(FeatureFlagKeys.BianV2Endpoints, defaultValue: true));
        await middleware.InvokeAsync(context);

        // Then
        reached().Should().BeTrue();
    }

    [Test]
    public async Task Given_TheFlagIsOff_When_AGatedEndpointIsCalled_Then_ItAnswers404LikeNoEndpoint()
    {
        // Given
        var flags = await FlagsFromFileAsync(FlagFile(bianV2: "off"));
        var (middleware, reached) = Gate(flags);

        // When
        var context = RequestTo(new FeatureGateAttribute(FeatureFlagKeys.BianV2Endpoints, defaultValue: true));
        await middleware.InvokeAsync(context);

        // Then
        reached().Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        context.Response.ContentLength.Should().BeNull();
    }

    [Test]
    public async Task Given_TheFlagIsOff_When_AnEndpointWithoutAGateIsCalled_Then_TheRequestGoesOn()
    {
        // Given - /v1 has no gate: the toggle never touches it
        var flags = await FlagsFromFileAsync(FlagFile(bianV2: "off"));
        var (middleware, reached) = Gate(flags);

        // When
        await middleware.InvokeAsync(RequestTo(gate: null));

        // Then
        reached().Should().BeTrue();
    }

    [Test]
    public async Task Given_TheFlagFileIsMissing_When_AGatedEndpointIsCalled_Then_ItsDefaultKeepsItOn()
    {
        // Given - the file never appears: the source is not ready, every flag keeps its default
        var flags = await FlagsFromFileAsync(Path.Combine(Path.GetTempPath(), $"ewp-flags-missing-{Guid.NewGuid():N}.json"));
        var (middleware, reached) = Gate(flags);

        // When
        await middleware.InvokeAsync(RequestTo(new FeatureGateAttribute(FeatureFlagKeys.BianV2Endpoints, defaultValue: true)));

        // Then
        reached().Should().BeTrue();
    }

    [Test]
    public async Task Given_TheFlagFileIsBroken_When_AFlagIsRead_Then_ItsDefaultIsUsed()
    {
        // Given
        var path = NewFile("{ \"flags\": ");
        var flags = await FlagsFromFileAsync(path);

        // When
        var bian = await flags.IsOnAsync(FeatureFlagKeys.BianV2Endpoints, defaultValue: true);
        var paused = await flags.IsOnAsync(FeatureFlagKeys.DigitalGoldSalesPaused, defaultValue: false);

        // Then
        bian.Should().BeTrue();
        paused.Should().BeFalse();
    }

    [Test]
    public async Task Given_TheRepositorysFlagFile_When_ItIsRead_Then_TodaysBehaviourIsKept()
    {
        // Given - feature-flags/ewp-flags.json, as committed
        var committed = Path.Combine(TestContext.CurrentContext.TestDirectory, "FeatureFlags", "ewp-flags.json");
        var flags = await FlagsFromFileAsync(committed);

        // When
        var bian = await flags.IsOnAsync(FeatureFlagKeys.BianV2Endpoints, defaultValue: false);
        var paused = await flags.IsOnAsync(FeatureFlagKeys.DigitalGoldSalesPaused, defaultValue: true);

        // Then - read from the file (the defaults asked for here are the opposite)
        bian.Should().BeTrue();
        paused.Should().BeFalse();
    }

    [Test]
    public void Given_ThePaymentsV2Controller_When_ItsGateIsRead_Then_ItIsTheBianToggleDefaultingOn()
    {
        // When
        var gate = typeof(PaymentOrderInitiationController).GetCustomAttribute<FeatureGateAttribute>();

        // Then
        gate.Should().NotBeNull();
        gate!.Flag.Should().Be(FeatureFlagKeys.BianV2Endpoints);
        gate.DefaultValue.Should().BeTrue();
    }

    private static async Task<EwpFeatureFlags> FlagsFromFileAsync(string path)
    {
        // Each test its own OpenFeature domain: providers never leak between tests.
        var domain = $"test-{Guid.NewGuid():N}";
        var provider = new FlagdProvider(new FlagdConfigBuilder()
            .WithResolverType(ResolverType.FILE)
            .WithOfflineFlagSourcePath(path)
            .WithDeadlineMs(500)
            .Build());
        try
        {
            await OpenFeatureApi.Instance.SetProviderAsync(domain, provider);
        }
        catch (Exception)
        {
            // A missing or broken file: the provider is not ready - exactly the case under test.
        }

        return new EwpFeatureFlags(
            OpenFeatureApi.Instance.GetClient(domain),
            EvaluationContext.Builder().SetTargetingKey("payments-api").Build(),
            NullLogger<EwpFeatureFlags>.Instance);
    }

    private string FlagFile(string bianV2) => NewFile($$"""
        {
          "flags": {
            "{{FeatureFlagKeys.BianV2Endpoints}}": { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "{{bianV2}}" }
          }
        }
        """);

    private string NewFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ewp-flags-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        _files.Add(path);
        return path;
    }

    private static (FeatureGateMiddleware Middleware, Func<bool> Reached) Gate(EwpFeatureFlags flags)
    {
        var reached = false;
        var middleware = new FeatureGateMiddleware(_ => { reached = true; return Task.CompletedTask; }, flags);
        return (middleware, () => reached);
    }

    private static DefaultHttpContext RequestTo(FeatureGateAttribute? gate)
    {
        var context = new DefaultHttpContext();
        var metadata = gate is null ? new EndpointMetadataCollection() : new EndpointMetadataCollection(gate);
        context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, metadata, "test endpoint"));
        return context;
    }
}