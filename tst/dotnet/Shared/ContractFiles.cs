namespace EnterpriseWebPlatform.Tests.Contracts;

// ┌─ :: ARCHITECTURE :: Consumer-driven contracts live in the repository ──────────────────────────
// │ Why : A provider must learn that a change breaks a consumer BEFORE it merges - without a Pact
// │       Broker (another server) and without starting the other side.
// │ How : Each consumer's tests write its contract (a Pact file, one per consumer and provider) to
// │       tst/contracts/pacts; the file is committed with the change that made it. Each provider's
// │       tests replay every file that names it. CI regenerates the files and fails when they
// │       differ from the committed ones, so a contract can never silently lag behind its consumer.
// │ Ref : tst/contracts/README.md | Pact specification V4 | Consumer-Driven Contracts (Fowler)
// └────────────────────────────────────────────────────────────────────────────────────────────────
/// <summary>Where the Pact files are, and the names each side uses in them.</summary>
public static class ContractFiles
{
    // ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
    // │ The participant names are part of each file's name and content: the Node consumer
    // │ (Partner Products Worker, test/contracts) uses the same spelling.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public const string PaymentsApi = "payments-api";
    public const string AccountsApi = "accounts-api";
    public const string AccountsCommandSubscriber = "accounts-command-subscriber";
    public const string PaymentsSagaReplySubscriber = "payments-saga-reply-subscriber";
    public const string PartnerProductsWorker = "partner-products-worker";

    /// <summary>tst/contracts/pacts, as the test project recorded it at build time.</summary>
    public static string Directory { get; } = Find();

    public static string FileOf(string consumer, string provider) => Path.Combine(Directory, $"{consumer}-{provider}.json");

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Pact's native core reports usage to the Pact Foundation unless told not to: nothing
    // │ from a test run leaves the machine. Call before the first Pact object is made.
    // └──────────────────────────────────────────────────────────────────────────────────────
    public static void DisablePactTelemetry() => Environment.SetEnvironmentVariable("PACT_DO_NOT_TRACK", "true");

    /// <summary>Every committed contract that names this provider.</summary>
    public static IReadOnlyList<string> FilesFor(string provider) =>
        System.IO.Directory.GetFiles(Directory, $"*-{provider}.json").Order(StringComparer.Ordinal).ToList();

    // ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    // │ Recorded by the test project (AssemblyMetadata "ContractsDirectory"), not searched for
    // │ from the output folder: built with --artifacts-path, the tests run from %TEMP%.
    // └──────────────────────────────────────────────────────────────────────────────────────
    private static string Find() =>
        typeof(ContractFiles).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "ContractsDirectory")?.Value is { Length: > 0 } folder
            ? Path.GetFullPath(folder)
            : throw new InvalidOperationException(
                "The test project does not record its contracts folder: add <AssemblyMetadata Include=\"ContractsDirectory\" Value=\"$(MSBuildThisFileDirectory)..\\..\\contracts\\pacts\" />.");
}