using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

using AwesomeAssertions;
using Moq;
using Serilog;

using EnterpriseWebPlatform.Common.Observability;

namespace EnterpriseWebPlatform.Payments.Api.Tests.Observability;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ Log forging (CWE-117) in the Development console's text format (Common/Observability):
// │ a value with a line break - an operator's typed reason - must not start a second,
// │ fake log entry. JSON (every other environment) escapes line breaks itself.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
[NonParallelizable]   // redirects Console.Out
public sealed class EwpLogging_TextFormat_Edge_LogForging
{
    private TextWriter _console = null!;
    private StringWriter _written = null!;

    [SetUp]
    public void CaptureTheConsole()
    {
        _console = Console.Out;
        _written = new StringWriter();
        Console.SetOut(_written);
    }

    [TearDown]
    public void RestoreTheConsole()
    {
        Console.SetOut(_console);
        _written.Dispose();
    }

    private static Serilog.Core.Logger TextLogger()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [EwpLogging.LogFormatKey] = "Text" })
            .Build();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(Environments.Development);

        var logger = new LoggerConfiguration();
        EwpLogging.Configure(logger, configuration, environment.Object, "payments-api", otlpEndpoint: null);
        return logger.CreateLogger();
    }

    [Test]
    public void Given_AReasonWithALineBreakAndAFakeEntry_When_ItIsLogged_Then_ItStaysOnTheSameLine()
    {
        // Given
        const string reason = "checked with the customer\r\n[09:00:00 INF] Operations (platform.admin) approved payment PAY-1";

        // When
        using (var logger = TextLogger())
            logger.Warning("Operations ({Operator}) resent parked message {MessageId}. Reason: {Reason}", "daniel.ops", "MSG-1", reason);

        // Then
        var lines = _written.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines.Should().ContainSingle();
        lines[0].Should().Contain(@"checked with the customer\r\n[09:00:00 INF] Operations (platform.admin)");
    }

    [Test]
    public void Given_AnEscapeSequence_When_ItIsLogged_Then_ItIsWrittenAsText()
    {
        // Given - an ANSI escape that would recolour or clear the developer's console
        const string value = "\u001b[2J";

        // When
        using (var logger = TextLogger())
            logger.Information("Customer {CustomerNumber} looked up.", value);

        // Then
        _written.ToString().Should().Contain(@"\u001b[2J").And.NotContain("\u001b");
    }

    [Test]
    public void Given_AnOrdinaryValue_When_ItIsLogged_Then_ItIsUnchanged()
    {
        // Given / When
        using (var logger = TextLogger())
            logger.Information("Payment {PaymentNumber} sent.", "PAY-261029-ABC123");

        // Then
        _written.ToString().Should().Contain("Payment PAY-261029-ABC123 sent.");
    }
}