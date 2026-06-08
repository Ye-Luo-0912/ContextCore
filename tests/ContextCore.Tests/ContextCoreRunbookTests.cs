namespace ContextCore.Tests;

[TestClass]
public sealed class ContextCoreRunbookTests
{
    [TestMethod]
    public void RankerShadowTraceCollectionRunbook_ShouldExist()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "docs", "ranker-shadow-trace-collection-runbook.md");

        Assert.IsTrue(File.Exists(path), $"Missing runbook: {path}");
        var text = File.ReadAllText(path);

        StringAssert.Contains(text, "Learning:RankerShadow:TraceCollectionEnabled");
        StringAssert.Contains(text, "MaxCandidatesPerTrace");
        StringAssert.Contains(text, "eval ranker-shadow-trace-quality");
        StringAssert.Contains(text, "TraceCount >= 30");
        StringAssert.Contains(text, "ReadyForGuardedOptIn");
    }

    [TestMethod]
    public void RankerShadowTraceCollectionScript_ShouldSupportDryRunAndParameterValidation()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "scripts", "collect-ranker-shadow-traces.ps1");

        Assert.IsTrue(File.Exists(path), $"Missing script: {path}");
        var text = File.ReadAllText(path);

        StringAssert.Contains(text, "[switch]$Execute");
        StringAssert.Contains(text, "Dry run only");
        StringAssert.Contains(text, "Assert-Parameter");
        StringAssert.Contains(text, "/api/status");
        StringAssert.Contains(text, "/api/health/ready");
        StringAssert.Contains(text, "/api/context/retrieve");
        StringAssert.Contains(text, "/api/package/build-detailed");
        StringAssert.Contains(text, "/api/retrieval/ranker-shadow/debug");
        StringAssert.Contains(text, "/api/learning/ranker-shadow/traces");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextCore.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate ContextCore.sln.");
    }
}
