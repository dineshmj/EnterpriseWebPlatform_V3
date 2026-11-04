using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;

using EnterpriseWebPlatform.Tests.Contracts;

namespace EnterpriseWebPlatform.BSS.AsyncWorkflows.Subscribers.ContractTests;

// ┌─ :: ARCHITECTURE :: Each run writes this consumer's contracts afresh ──────────────────────────
// │ Why : Pact MERGES new interactions into an existing file: an interaction a consumer no longer
// │       uses would stay in the contract for ever and keep binding the provider. And the provider
// │       tests (another assembly, run in parallel) must never read a half-written file.
// │ How : The tests write into an empty folder of their own (bin/.../pacts). Only when EVERY test
// │       of this assembly ran and passed are the files moved into tst/contracts/pacts, each in one
// │       atomic replace; a filtered or failed run changes nothing there. CI then compares the
// │       folder with the commit (.github/workflows/ci.yml, "Contracts up to date").
// │ Ref : tst/contracts/README.md
// └────────────────────────────────────────────────────────────────────────────────────────────────
[SetUpFixture]
public sealed class PactFilesSetUp
{
    /// <summary>Where this run's Pact files are written before they replace the committed ones.</summary>
    public static readonly string WorkDirectory = Path.Combine(AppContext.BaseDirectory, "pacts");

    [OneTimeSetUp]
    public void StartEmpty()
    {
        ContractFiles.DisablePactTelemetry();
        if (Directory.Exists(WorkDirectory))
            Directory.Delete(WorkDirectory, recursive: true);
        Directory.CreateDirectory(WorkDirectory);
    }

    [OneTimeTearDown]
    public void PublishWhenComplete()
    {
        var everyTest = TestExecutionContext.CurrentContext.CurrentTest.TestCaseCount;
        var result = TestContext.CurrentContext.Result;
        if (result.Outcome.Status != TestStatus.Passed || result.PassCount != everyTest)
        {
            TestContext.Progress.WriteLine(
                $"Contracts not published: {result.PassCount} of {everyTest} tests passed ({result.Outcome.Status}). Run every test of this project to update tst/contracts/pacts.");
            return;
        }

        Directory.CreateDirectory(ContractFiles.Directory);
        foreach (var file in Directory.GetFiles(WorkDirectory, "*.json"))
        {
            var target = Path.Combine(ContractFiles.Directory, Path.GetFileName(file));
            var staged = target + ".tmp";
            // The repository's files end at their last character (no final newline).
            File.WriteAllText(staged, File.ReadAllText(file).TrimEnd());
            File.Move(staged, target, overwrite: true);
        }
    }
}