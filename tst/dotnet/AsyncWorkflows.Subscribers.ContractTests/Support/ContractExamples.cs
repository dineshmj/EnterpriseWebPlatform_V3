using System.Text.Json;

using PactNet;
using PactNet.Matchers;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests.Support;

// ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
// │ The examples are FIXED values, so a run writes the same contract file every time
// │ (CI compares it with the committed one). Matchers say what the provider may vary:
// │ a GUID must be a GUID, a number a number, a code a string - the courier needs no more.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>The example payment, the Pact settings and the matchers the contract tests share.</summary>
internal static class ContractExamples
{
    public const string PaymentRef = "0199f2a4-6c1e-7b3a-9d42-5e8f1a2b3c4d";
    public const string CommandMessageId = "0199f2a4-6c1e-7b3a-9d42-000000000001";
    public const string ReplyMessageId = "0199f2a4-6c1e-7b3a-9d42-000000000002";
    public const string WorkflowId = "0199f2a4-6c1e-7b3a-9d42-000000000003";
    public const string PaymentNumber = "PAY-261015-TEST01";
    public const string Customer = "CUST-100001";
    public const string Bsb = "062-000";
    public const string AccountNumber = "72947086";
    public const decimal Amount = 140.25m;
    public const string Branch = "SYD001";
    public const string Sophie = "sub-sophie";

    private const string GuidPattern = "^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";

    /// <summary>Kafka messages: the platform's envelope is PascalCase (System.Text.Json defaults).</summary>
    public static readonly JsonSerializerOptions EnvelopeJson = new();

    /// <summary>HTTP bodies: camelCase, as the couriers send them (JsonContent defaults).</summary>
    public static readonly JsonSerializerOptions ApiJson = new(JsonSerializerDefaults.Web);

    public static PactConfig Config => new() { PactDir = PactFilesSetUp.WorkDirectory, LogLevel = PactLogLevel.Warn };

    public static IMatcher Guid(string example) => Match.Regex(example, GuidPattern);

    public static IMatcher Text(string example) => Match.Type(example);

    public static IMatcher Number(decimal example) => Match.Number(example);
}