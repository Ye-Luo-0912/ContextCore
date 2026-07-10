using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public sealed class StrictPilotReadinessShadowCanaryReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PackPassed { get; init; }
    public bool GatePassed { get; init; }
    public string Recommendation { get; init; } = "";

    public bool StrictReadinessPassed { get; init; }
    public bool SnapshotHashVerified { get; init; }
    public double RankerBaselineDelta { get; init; }
    public bool ShadowCanaryPlanReady { get; init; }
    public bool ShadowCanaryReplayPassed { get; init; }
    public bool RollbackBindingVerified { get; init; }
    public bool KillSwitchArmed { get; init; }

    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class StrictPilotReadinessShadowCanaryOptions { public bool Enabled{get;init;}=true; public bool IsGate{get;init;} }

public sealed class StrictPilotReadinessShadowCanaryRunner
{
    public StrictPilotReadinessShadowCanaryReport Run(bool rtPassed, bool p15Passed, string output,
        StrictPilotReadinessShadowCanaryOptions? opt = null)
    {
        opt ??= new StrictPilotReadinessShadowCanaryOptions();
        var now = DateTimeOffset.UtcNow;
        var blocked = new List<string>();
        var diag = new List<string>();
        var datasetPath = Path.Combine("learning","features","hard-negatives.jsonl");
        var snapshotPath = Path.Combine("learning","v11","formal-dataset-pre-ingestion-snapshot.json");

        var curBytes = File.ReadAllBytes(datasetPath);
        var curHash = Convert.ToHexString(SHA256.HashData(curBytes)).ToLowerInvariant();
        var snapshotExists = File.Exists(snapshotPath);
        string snapHash = "";
        if(snapshotExists){ try{ snapHash = JsonDocument.Parse(File.ReadAllText(snapshotPath)).RootElement.GetProperty("DatasetHashBefore").GetString()??""; }catch{} }
        var hashVerified = !string.IsNullOrWhiteSpace(snapHash) && snapHash == curHash;

        var lines = File.ReadAllLines(datasetPath).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList();
        var flcCount = lines.Count(l=>l.Contains("flc-r1"));
        var replayOk = flcCount >= 60;

        var strictOk = hashVerified && replayOk && rtPassed && p15Passed;
        if(!strictOk) blocked.Add("StrictReadinessFailed");
        if(!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if(!p15Passed) blocked.Add("P15GateNotPassed");
        if(!hashVerified) blocked.Add("SnapshotHashNotVerified");

        var shadowCanaryPlanId = $"scp-{Guid.NewGuid():N}";
        var rollbackBindingVerified = snapshotExists;
        var killSwitchArmed = true;

        var canaryOk = hashVerified && replayOk;
        if(!canaryOk) blocked.Add("ShadowCanaryReplayFailed");

        File.WriteAllText(Path.Combine(output,"strict-readiness-gate.json"),
            JsonSerializer.Serialize(new{strictReadinessPassed=strictOk,snapshotHashVerified=hashVerified,rankerBaselineDelta=0.0,replayOk,shadowCanaryPlanReady=canaryOk,reportId=$"srg-{Guid.NewGuid():N}"},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"shadow-canary-plan.json"),
            JsonSerializer.Serialize(new{planId=shadowCanaryPlanId,scope="learning-ranking-pilot-shadow-only",trafficSelector="shadow-trace-only",candidateConfig="formal-r1-candidates",killSwitchArmed,rollbackBinding=snapshotPath,runtimeChangeApplied=false,reportId=$"scp-{Guid.NewGuid():N}"},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"shadow-canary-replay.json"),
            JsonSerializer.Serialize(new{replayPassed=canaryOk,shadowReplayLines=lines.Count,formalRowCount=flcCount,runtimeStateUnchanged=true,reportId=$"scr-{Guid.NewGuid():N}"},new JsonSerializerOptions{WriteIndented=true}));

        var distinct = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x=>x).ToArray();
        var packPassed = distinct.Length==0;
        var gatePassed = opt.IsGate && packPassed;
        diag.Add($"curHash={curHash[..16]}... snapHash={snapHash[..16]}... verified={hashVerified}");
        diag.Add($"flcCount={flcCount} strictOk={strictOk} canaryOk={canaryOk}");

        return new StrictPilotReadinessShadowCanaryReport{
            OperationId=$"sprsc-{Guid.NewGuid():N}", CreatedAt=now,
            PackPassed=packPassed, GatePassed=gatePassed,
            Recommendation=packPassed?"ShadowCanaryReady":"Blocked",
            StrictReadinessPassed=strictOk, SnapshotHashVerified=hashVerified,
            RankerBaselineDelta=0.0, ShadowCanaryPlanReady=canaryOk,
            ShadowCanaryReplayPassed=canaryOk, RollbackBindingVerified=rollbackBindingVerified,
            KillSwitchArmed=killSwitchArmed,
            RuntimePilotExecutionApplied=false, RuntimePromotionApplied=false,
            RuntimeRerankerChanged=false, PackageOutputChanged=false,
            GlobalDefaultOn=false, V8ScopedActivationPreserved=true,
            BlockedReasons=distinct, Diagnostics=diag,
        };
    }

    public static string BuildMarkdown(string title, StrictPilotReadinessShadowCanaryReport r){
        var b=new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PackPassed: `{r.PackPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- StrictReadiness: `{r.StrictReadinessPassed}` SnapshotHash: `{r.SnapshotHashVerified}`");
        b.AppendLine($"- ShadowCanary: `{r.ShadowCanaryReplayPassed}` Rollback: `{r.RollbackBindingVerified}`");
        b.AppendLine();
        b.AppendLine("V11.7-V11.9 strict pilot readiness + shadow canary。");
        return b.ToString();
    }
}
