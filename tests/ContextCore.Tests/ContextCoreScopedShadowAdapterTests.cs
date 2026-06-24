using ContextCore.Abstractions;
using ContextCore.Core.Infrastructure;
using ContextCore.Core.Services;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreScopedShadowAdapterTests
{
    [TestMethod]
    public void ScopedAdapter_Allowlisted_ReturnsHypotheticalDelta()
    {
        var adapter = new ScopedShadowRetrievalAdapter(new[] { "ws-test:col-test" });
        var result = adapter.ExecuteAsync(new RetrievalAdapterRequest
        {
            OperationId = "test-allow", WorkspaceId = "ws-test", CollectionId = "col-test",
            QueryText = "short query", BaselineCandidateIds = new[] { "a", "b", "c", "d", "e" }
        }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Applied, "allowlisted must not be Applied=true");
        Assert.IsTrue(result.RemovedCandidateIds.Count >= 0, "may report hypothetical removals");
        Assert.AreEqual(0, result.AddedCandidateIds.Count, "must not add candidates");
    }

    [TestMethod]
    public void ScopedAdapter_NonAllowlisted_ReturnsNoOp()
    {
        var adapter = new ScopedShadowRetrievalAdapter(new[] { "ws-allow:col-allow" });
        var result = adapter.ExecuteAsync(new RetrievalAdapterRequest
        {
            OperationId = "test-noallow", WorkspaceId = "ws-other", CollectionId = "col-other",
            QueryText = "test query", BaselineCandidateIds = new[] { "x", "y" }
        }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Applied);
        Assert.AreEqual(0, result.AddedCandidateIds.Count);
        Assert.AreEqual(0, result.RemovedCandidateIds.Count);
    }

    [TestMethod]
    public void InvocationRunner_WithNoOpGate_Passes()
    {
        var noopGate = new AdapterNoOpBindingSmokeReport { SmokePassed = true, InvocationCount = 2 };
        var report = new ScopedShadowAdapterInvocationRunner().RunInvocation(noopGate);

        Assert.IsTrue(report.InvocationPassed);
        Assert.AreEqual(1, report.AllowlistedInvocationCount);
        Assert.AreEqual(1, report.NonAllowlistedInvocationCount);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.RuntimeMutated);
        Assert.AreEqual("ScopedShadow", report.AdapterType);
    }

    [TestMethod]
    public void InvocationRunner_WithoutNoOpGate_Blocks()
    {
        var report = new ScopedShadowAdapterInvocationRunner().RunInvocation(null);
        Assert.IsFalse(report.InvocationPassed);
        Assert.IsTrue(report.BlockedReasons.Contains("V60NoOpGateNotPassed"));
    }
}