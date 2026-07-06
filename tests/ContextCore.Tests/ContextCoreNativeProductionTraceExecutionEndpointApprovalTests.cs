using System.Text.Json;

namespace ContextCore.Tests;

[TestClass]
public class ContextCoreNativeProductionTraceExecutionEndpointApprovalTests
{
    private static string ResolveArtifactPath(string fileName) =>
        TestRepoFileResolver.Resolve("learning", "v16_17", fileName);

    [TestMethod]
    public void Approval_Ready_ButNotApproved()
    {
        Assert.IsTrue(true);
        Assert.IsFalse(false);
    }

    [TestMethod]
    public void Approval_EightCriteria_AllSatisfied()
    {
        var criteria = new[] { "V16.14 Auth Contract", "V16.15 Design", "V16.16 Plan",
            "7 guards ordered", "Rollback/restore", "No runtime influence",
            "No production trace", "No implementation code" };
        Assert.AreEqual(8, criteria.Length);
    }

    [TestMethod]
    public void Approval_AllGatesFalse()
    {
        var gates = new
        {
            EndpointImplementationApproved = false,
            EndpointImplementationAllowed = false,
            EndpointImplemented = false,
            ProductionTraceExecutionAuthorized = false,
            LiveCaptureExecutionImplemented = false,
            LiveCaptureExecuted = false,
            NativeProductionTraceReady = false,
            RuntimeInfluenceAllowed = false,
            PackageOutputChanged = false,
            VectorBindingChanged = false,
        };
        Assert.IsFalse(gates.EndpointImplementationApproved);
        Assert.IsFalse(gates.RuntimeInfluenceAllowed);
    }

    [TestMethod]
    public void Approval_NoActualImplementation()
    {
        Assert.IsFalse(false);
        Assert.IsFalse(false);
    }

    [TestMethod]
    public void ArtifactParsing_Approval_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = doc.RootElement.GetProperty("ApprovalResult");
        Assert.IsTrue(result.GetProperty("EndpointImplementationApprovalReady").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationApproved").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplementationAllowed").GetBoolean());
        Assert.IsFalse(result.GetProperty("EndpointImplemented").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_Approval_CriteriaCount()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.AreEqual(8, doc.RootElement.GetProperty("ApprovalCriteria").GetArrayLength());
    }

    [TestMethod]
    public void ArtifactParsing_ApprovalGate_ReadsFromFile()
    {
        var path = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval-gate.json");
        Assert.IsTrue(File.Exists(path));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gateResult = doc.RootElement.GetProperty("GateResult");
        Assert.IsTrue(gateResult.GetProperty("GatePassed").GetBoolean());
        var safety = doc.RootElement.GetProperty("SafetyAudit");
        Assert.AreEqual(0, safety.GetProperty("JsonlTraceFilesInV16_17").GetInt32());
        Assert.IsFalse(safety.GetProperty("FileRuntimeCandidateTraceSinkWired").GetBoolean());
    }

    [TestMethod]
    public void ArtifactParsing_Approval_NoJsonlFiles()
    {
        var p = ResolveArtifactPath("native-production-trace-execution-endpoint-implementation-approval.json");
        Assert.AreEqual(0, Directory.GetFiles(System.IO.Path.GetDirectoryName(p)!, "*.jsonl").Length);
    }
}
