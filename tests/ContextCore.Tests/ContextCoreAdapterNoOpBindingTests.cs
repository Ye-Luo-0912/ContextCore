using ContextCore.Abstractions;
using ContextCore.Core.Infrastructure;
using ContextCore.Core.Services;
using ContextCore.Abstractions.Models;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreAdapterNoOpBindingTests
{
    [TestMethod]
    public void NoOpAdapter_ReturnsEmptyResult()
    {
        var adapter = new NoOpContextRetrievalAdapter();
        var result = adapter.ExecuteAsync(new RetrievalAdapterRequest
        {
            OperationId = "test-1", WorkspaceId = "ws", CollectionId = "col",
            QueryText = "test", BaselineCandidateIds = new[] { "a", "b" }
        }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Applied);
        Assert.AreEqual(0, result.AddedCandidateIds.Count);
        Assert.AreEqual(0, result.RemovedCandidateIds.Count);
        Assert.AreEqual("NoOp", adapter.Name);
    }

    [TestMethod]
    public void ShadowAdapter_WritesTraceAndReturnsEmpty()
    {
        var traceDir = Path.Combine(Path.GetTempPath(), "adapter-smoke-" + Guid.NewGuid().ToString("N"));
        var adapter = new NoOpShadowRetrievalAdapter(traceDir);
        var result = adapter.ExecuteAsync(new RetrievalAdapterRequest
        {
            OperationId = "smoke-test", WorkspaceId = "ws", CollectionId = "col",
            QueryText = "smoke query", BaselineCandidateIds = new[] { "x", "y", "z" }
        }).GetAwaiter().GetResult();

        Assert.IsFalse(result.Applied);
        Assert.AreEqual(0, result.AddedCandidateIds.Count);
        Assert.AreEqual(0, result.RemovedCandidateIds.Count);
        Assert.IsFalse(string.IsNullOrEmpty(result.TracePath));
        Assert.IsTrue(File.Exists(result.TracePath));

        var trace = File.ReadAllText(result.TracePath);
        Assert.IsTrue(trace.Contains("smoke-test"), $"trace should contain operation id; content={trace}");
        Assert.IsTrue(trace.Contains("smoke query"), "trace should contain query text");

        // clean up
        try { File.Delete(result.TracePath); Directory.Delete(traceDir, true); } catch { }
    }

    [TestMethod]
    public void SmokeRunner_WithFreezeGate_Passes()
    {
        var freeze = new FormalRetrievalIntegrationFreezeReport { FreezePassed = true };
        var report = new AdapterNoOpBindingSmokeRunner().RunSmoke(freeze);
        Assert.IsTrue(report.SmokePassed);
        Assert.AreEqual(2, report.InvocationCount);
        Assert.AreEqual(0, report.AddCount);
        Assert.AreEqual(0, report.RemoveCount);
        Assert.IsFalse(report.FormalSelectedSetChanged);
        Assert.IsFalse(report.RuntimeMutated);
    }

    [TestMethod]
    public void SmokeRunner_WithoutFreezeGate_Blocks()
    {
        var report = new AdapterNoOpBindingSmokeRunner().RunSmoke(null);
        Assert.IsFalse(report.SmokePassed);
        Assert.IsTrue(report.BlockedReasons.Contains("FreezeGateNotPassed"));
    }
}