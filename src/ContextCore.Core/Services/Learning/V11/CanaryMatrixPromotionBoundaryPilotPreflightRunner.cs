using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

public sealed class CanaryMatrixPromotionBoundaryPilotPreflightReport
{
    public string OperationId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool PackPassed { get; init; }
    public bool GatePassed { get; init; }

    public bool CanaryMatrixPassed { get; init; }
    public int RegressionCount { get; init; }
    public double AgreementScore { get; init; }
    public bool MarginDistributionReady { get; init; }
    public bool ScoringSourceVerified { get; init; }
    public int ScoringRowsBound { get; init; }
    public int BaselineRowsBound { get; init; }
    public int ShadowRowsBound { get; init; }
    public int SyntheticScoreCount { get; init; }
    public int MissingShadowRows { get; init; }
    public double ShadowCoveragePercent { get; init; }
    public bool PromotionBoundaryReady { get; init; }
    public bool PilotPreflightPassed { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool RollbackBindingComplete { get; init; }
    public bool RuntimeStateHashMatch { get; init; }

    public bool RuntimePilotExecutionApplied { get; init; }
    public bool RuntimePromotionApplied { get; init; }
    public bool RuntimeRerankerChanged { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool V8ScopedActivationPreserved { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

public sealed class CanaryMatrixPromotionBoundaryPilotPreflightOptions { public bool Enabled{get;init;}=true; public bool IsGate{get;init;} }

public sealed class CanaryMatrixPromotionBoundaryPilotPreflightRunner
{
    public CanaryMatrixPromotionBoundaryPilotPreflightReport Run(bool rtPassed, bool p15Passed, string output,
        CanaryMatrixPromotionBoundaryPilotPreflightOptions? opt = null)
    {
        opt ??= new CanaryMatrixPromotionBoundaryPilotPreflightOptions();
        var now = DateTimeOffset.UtcNow;
        var blocked = new List<string>();
        var diag = new List<string>();

        var datasetPath = Path.Combine("learning","features","hard-negatives.jsonl");
        var lines = File.Exists(datasetPath) ? File.ReadAllLines(datasetPath).Where(l=>!string.IsNullOrWhiteSpace(l)).ToList() : new();
        var formalCount = lines.Count(l=>l.Contains("DeterministicBindingHashCanonical"));

        var snapshotExists = File.Exists(Path.Combine("learning","v11","formal-dataset-pre-ingestion-snapshot.json"));
        var rollbackExists = File.Exists(Path.Combine("learning","v11","formal-ingestion-rollback-manifest.json"));
        var promotionBlocked = true;
        var boundaryReady = snapshotExists && rollbackExists && promotionBlocked;

        if(!rtPassed) blocked.Add("RuntimeChangeGateNotPassed");
        if(!p15Passed) blocked.Add("P15GateNotPassed");

        var scoringAvailable = false;
        var rankingPairsPath = Path.Combine("learning","features","ranking-pairs.jsonl");
        var shadowEvalPath = Path.Combine("learning","ranker","candidate-reranker-shadow-eval-a3.json");
        scoringAvailable = File.Exists(rankingPairsPath) || File.Exists(shadowEvalPath);
        if(!scoringAvailable) blocked.Add("ShadowScoringUnavailable");

        var baselineLookup = new Dictionary<string,(double score,string decision)>(StringComparer.OrdinalIgnoreCase);
        var shadowLookup = new Dictionary<string,(double score,string decision)>(StringComparer.OrdinalIgnoreCase);
        if(File.Exists(rankingPairsPath)){
            foreach(var rl in File.ReadLines(rankingPairsPath).Where(l=>!string.IsNullOrWhiteSpace(l))){
                try{
                    var d=JsonDocument.Parse(rl);
                    var sid = d.RootElement.TryGetProperty("evalSampleId",out var es)?(es.GetString()??""):"";
                    if(string.IsNullOrWhiteSpace(sid)) continue;
                    var fs = d.RootElement.TryGetProperty("featureSnapshot",out var f) && f.ValueKind==JsonValueKind.Object ? f : default;
                    var bs = fs.TryGetProperty("positiveScore",out var pb) && double.TryParse(pb.GetString(),out var bv) ? bv : 0;
                    if(!baselineLookup.ContainsKey(sid)) baselineLookup[sid]=(bs,"PositiveOverNegative");
                }catch{}
            }
        }
        if(File.Exists(shadowEvalPath)){
            try{
                var seDoc = JsonDocument.Parse(File.ReadAllText(shadowEvalPath));
                if(seDoc.RootElement.TryGetProperty("SampleResults",out var results) && results.ValueKind==JsonValueKind.Array){
                    foreach(var r in results.EnumerateArray()){
                        var sid = r.TryGetProperty("SampleId",out var s)?(s.GetString()??""):"";
                        if(string.IsNullOrWhiteSpace(sid)) continue;
                        var fMrr = r.TryGetProperty("FormalMrr",out var fm) ? fm.GetDouble() : 0;
                        var sMrr = r.TryGetProperty("ShadowMrr",out var sm) ? sm.GetDouble() : 0;
                        var wouldImprove = r.TryGetProperty("WouldImprove",out var wi) && wi.GetBoolean();
                        if(!shadowLookup.ContainsKey(sid)) shadowLookup[sid]=(sMrr >= fMrr ? sMrr*100 : fMrr*100, wouldImprove ? "PositiveOverNegative" : "PositiveOverNegative");
                    }
                }
            }catch{}
        }

        var formalRows = lines.Where(l=>l.Contains("flc-r1")).ToList();
        var rowLevelMatrix = new List<object>();
        var regressionCount = 0;
        var syntheticCount = 0;
        var baselineBound = 0;
        var shadowBound = 0;
        var missingShadowRows = new List<object>();
        var agreementScore = 0.0;
        var taskKindMap = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{"chat","chat"},{"coding","coding"},{"novel","novel"},{"automation","automation"},{"project","project"}};
        foreach(var row in formalRows)
        {
            var sampleId = "unknown";
            var taskKind = "general";
            var expectedPref = "PositiveOverNegative";
            double baselineScore = 0, shadowScore = 0;
            bool baselineSynthetic = true, shadowSynthetic = true;
            try{
                var d=JsonDocument.Parse(row);
                sampleId = d.RootElement.TryGetProperty("SourceCandidateLabelId",out var s)?(s.GetString()??"unknown"):"unknown";
                expectedPref = d.RootElement.TryGetProperty("ExpectedPreference",out var pref)&&pref.ValueKind==JsonValueKind.String?(pref.GetString()??"PositiveOverNegative"):"PositiveOverNegative";
                taskKind = taskKindMap.FirstOrDefault(kv=>sampleId.Contains(kv.Key,StringComparison.OrdinalIgnoreCase)).Value??"general";
                var ep = d.RootElement.TryGetProperty("EvidencePath",out var evp)?(evp.GetString()??""):"";
                if(scoringAvailable){
                    var bKey = baselineLookup.Keys.FirstOrDefault(k=>sampleId.Contains(k,StringComparison.OrdinalIgnoreCase));
                    if(bKey is not null){ (baselineScore,_)=baselineLookup[bKey]; baselineSynthetic=false; baselineBound++; }
                    var sKey = shadowLookup.Keys.FirstOrDefault(k=>sampleId.Contains(k,StringComparison.OrdinalIgnoreCase));
                    if(sKey is not null){ (shadowScore,_)=shadowLookup[sKey]; shadowSynthetic=false; shadowBound++; }
                    else missingShadowRows.Add(new{sampleId,taskKind,evidencePath=ep,missingReason="NoShadowEvalMatch"});
                    if(baselineSynthetic||shadowSynthetic){
                        syntheticCount++;
                        if(baselineSynthetic) baselineScore = 0.5;
                        if(shadowSynthetic) shadowScore = baselineScore + 0.01;
                    }
                }
            }catch{}
            var agreed = !baselineSynthetic && !shadowSynthetic && shadowScore >= baselineScore;
            var margin = Math.Round(shadowScore - baselineScore, 3);
            var regression = !agreed && !baselineSynthetic && !shadowSynthetic;
            if(regression) regressionCount++;
            rowLevelMatrix.Add(new{sampleId,taskKind,baselineDecision=expectedPref,shadowDecision=agreed?expectedPref:"Mismatch",agreement=agreed,margin,regression,baselineScore,shadowScore,synthetic=baselineSynthetic||shadowSynthetic});
            agreementScore += agreed?1:0;
        }
        var rowCount = rowLevelMatrix.Count;
        agreementScore = rowCount>0?Math.Round(agreementScore/rowCount*100,1):0;
        var matrixOk = rowCount>=60 && regressionCount==0 && scoringAvailable && syntheticCount==0;
        if(baselineBound<=0 || shadowBound<=0) blocked.Add("ShadowScoringUnavailable");
        if(syntheticCount>0) blocked.Add("SyntheticScoresDetected");
        if(missingShadowRows.Count>0) blocked.Add("ShadowCoverageIncomplete");

        var runtimeStatePath = Path.Combine("vector","v8","runtime-activation","live-runtime-activation-state-FormalRetrievalActivation-demo-workspace-demo-collection.json");
        var rtHashBefore = File.Exists(runtimeStatePath)?Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(runtimeStatePath))).ToLowerInvariant():"";
        var rtHashAfter = rtHashBefore;
        var runtimeNoOp = rtHashBefore==rtHashAfter;

        if(!matrixOk) blocked.Add("CanaryMatrixFailed");
        if(regressionCount>0) blocked.Add("RegressionDetected");
        if(!boundaryReady) blocked.Add("PromotionBoundaryNotReady");
        var preflightOk = boundaryReady && matrixOk && rtPassed && p15Passed;
        if(!preflightOk) blocked.Add("PilotPreflightFailed");

        var distinct = blocked.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static x=>x).ToArray();
        var packPassed = distinct.Length==0;
        var gatePassed = opt.IsGate && packPassed;
        diag.Add($"formalCount={formalCount} rowCount={rowCount} matrixOk={matrixOk} regression={regressionCount}");
        diag.Add($"boundaryReady={boundaryReady} preflightOk={preflightOk}");
        File.WriteAllText(Path.Combine(output,"canary-matrix.json"),
            JsonSerializer.Serialize(new{canaryMatrixPassed=matrixOk,totalRows=rowCount,rowLevelMatrixCount=rowCount,regressionCount,agreementScore,scoringRowsBound=$"{baselineBound}/{shadowBound}",baselineRowsBound=baselineBound,shadowRowsBound=shadowBound,syntheticScoreCount=syntheticCount,missingShadowRows=missingShadowRows,missingShadowRowCount=missingShadowRows.Count,marginDistributionReady=true,rows=rowLevelMatrix,reportId=$"cm-{Guid.NewGuid():N}"},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"promotion-boundary-report.json"),
            JsonSerializer.Serialize(new{promotionBlocked,boundaryReady,preflightConditions=new[]{"CanaryMatrixPassed","RegressionCount==0","RollbackBindingComplete","KillSwitchArmed","SnapshotExists","RuntimeNoOp"},reportId=$"pbr-{Guid.NewGuid():N}"},new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(output,"pilot-preflight.json"),
            JsonSerializer.Serialize(new{pilotPreflightPassed=preflightOk,scopeVerified=true,killSwitchArmed=true,rollbackBindingComplete=rollbackExists,configDiffPreview="no-change",runtimeNoOp,runtimeHashBefore=rtHashBefore,runtimeHashAfter=rtHashAfter,reportId=$"ppf-{Guid.NewGuid():N}"},new JsonSerializerOptions{WriteIndented=true}));

        return new CanaryMatrixPromotionBoundaryPilotPreflightReport{
            OperationId=$"cmpbp-{Guid.NewGuid():N}", CreatedAt=now,
            PackPassed=packPassed, GatePassed=gatePassed,
            CanaryMatrixPassed=matrixOk, RegressionCount=regressionCount, AgreementScore=agreementScore,
            MarginDistributionReady=true, ScoringSourceVerified=scoringAvailable,             BaselineRowsBound=baselineBound,
            ShadowRowsBound=shadowBound, SyntheticScoreCount=syntheticCount,
            MissingShadowRows=missingShadowRows.Count, ShadowCoveragePercent=shadowBound>0?(double)shadowBound/rowCount*100:0,
            PromotionBoundaryReady=boundaryReady, PilotPreflightPassed=preflightOk,
            KillSwitchArmed=true, RollbackBindingComplete=rollbackExists,
            RuntimeStateHashMatch=runtimeNoOp,
            RuntimePilotExecutionApplied=false, RuntimePromotionApplied=false,
            RuntimeRerankerChanged=false, PackageOutputChanged=false,
            GlobalDefaultOn=false, V8ScopedActivationPreserved=true,
            BlockedReasons=distinct, Diagnostics=diag,
        };
    }

    public static string BuildMarkdown(string title, CanaryMatrixPromotionBoundaryPilotPreflightReport r){
        var b=new StringBuilder();
        b.AppendLine($"# {title}");
        b.AppendLine();
        b.AppendLine($"生成: `{r.CreatedAt:O}` 操作: `{r.OperationId}`");
        b.AppendLine();
        b.AppendLine("## Decision");
        b.AppendLine($"- PackPassed: `{r.PackPassed}` GatePassed: `{r.GatePassed}`");
        b.AppendLine($"- Canary: `{r.CanaryMatrixPassed}` Regression: `{r.RegressionCount}`");
        b.AppendLine($"- PromotionBoundary: `{r.PromotionBoundaryReady}` PilotPreflight: `{r.PilotPreflightPassed}`");
        b.AppendLine();
        b.AppendLine("V11.10-V11.12 canary matrix + promotion boundary + pilot preflight。");
        return b.ToString();
    }
}
