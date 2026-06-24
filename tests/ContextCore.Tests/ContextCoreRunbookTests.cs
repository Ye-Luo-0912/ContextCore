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

    [TestMethod]
    public void GraphShadowTraceCollectionRunbook_ShouldExist()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "docs", "graph-shadow-trace-collection-runbook.md");

        Assert.IsTrue(File.Exists(path), $"Missing runbook: {path}");
        var text = File.ReadAllText(path);

        StringAssert.Contains(text, "Graph:ExpansionShadow:TraceCollectionEnabled");
        StringAssert.Contains(text, "Profiles");
        StringAssert.Contains(text, "MaxRelationsPerTrace");
        StringAssert.Contains(text, "eval graph-expansion-shadow-trace-quality");
        StringAssert.Contains(text, "TraceCount >= 30");
        StringAssert.Contains(text, "ReadyForGuardedOptIn");
    }

    [TestMethod]
    public void GraphShadowTraceCollectionScript_ShouldSupportDryRunAndParameterValidation()
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(root, "scripts", "collect-graph-expansion-shadow-traces.ps1");

        Assert.IsTrue(File.Exists(path), $"Missing script: {path}");
        var text = File.ReadAllText(path);

        StringAssert.Contains(text, "[switch]$Execute");
        StringAssert.Contains(text, "Dry run only");
        StringAssert.Contains(text, "Assert-Parameter");
        StringAssert.Contains(text, "/api/status");
        StringAssert.Contains(text, "/api/health/ready");
        StringAssert.Contains(text, "/api/context/retrieve");
        StringAssert.Contains(text, "/api/context/query");
        StringAssert.Contains(text, "/api/package/build-detailed");
        StringAssert.Contains(text, "/api/learning/graph-expansion-shadow/traces");

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        });

        Assert.IsNotNull(process);
        Assert.IsTrue(process.WaitForExit(30_000), "Graph shadow dry-run script timed out.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.AreEqual(0, process.ExitCode, error);
        StringAssert.Contains(output, "Dry run only");
        StringAssert.Contains(output, "Graph:ExpansionShadow:TraceCollectionEnabled=true");
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
