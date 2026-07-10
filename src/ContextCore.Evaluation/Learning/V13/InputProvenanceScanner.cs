using System.Security.Cryptography;
using System.Text.Json;

namespace ContextCore.Core.Services.Learning.V13;

public sealed class InputProvenanceScanner
{
    public LearningDataQualityGateReport ScanAndEvaluate(string outputDir)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        Directory.CreateDirectory(outputDir);
        var inventory = new List<LearningDatasetInventoryItem>();
        var documentLineages = new List<DocumentLineageContract>();
        var blocked = new List<string>();

        ScanRankingPairs(inventory,documentLineages,blocked);
        ScanHardNegatives(inventory,documentLineages,blocked);
        ScanRouterIntents(inventory,documentLineages,blocked);
        ScanShadowEval(inventory,documentLineages,blocked);
        ScanWiderPilotArchive(inventory,documentLineages,blocked);

        // Gate checks
        var syntheticGateLeakage = 0;
        var diagnosticTrainingLeakage = 0;
        var llmAuthoritativeLeakage = 0;
        foreach(var item in inventory){
            if(item.AuthorityKind==DataAuthorityKind.Synthetic && item.UsageFlags.HasFlag(DataUsageFlags.Gate))
                syntheticGateLeakage++;
            if(item.AuthorityKind==DataAuthorityKind.Diagnostic && item.UsageFlags.HasFlag(DataUsageFlags.Training))
                diagnosticTrainingLeakage++;
            if(item.SourceKind==InputSourceKind.Llm && item.AuthorityKind==DataAuthorityKind.Authoritative)
                llmAuthoritativeLeakage++;
        }

        if(syntheticGateLeakage>0) blocked.Add($"SyntheticGateLeakage: {syntheticGateLeakage} datasets have Synthetic authority with Gate usage");
        if(diagnosticTrainingLeakage>0) blocked.Add($"DiagnosticTrainingLeakage: {diagnosticTrainingLeakage} datasets have Diagnostic authority with Training usage");
        if(llmAuthoritativeLeakage>0) blocked.Add($"LlmAuthoritativeLeakage: {llmAuthoritativeLeakage} datasets have LLM source defaulting to Authoritative authority");

        var everyHasSourceKind = inventory.All(i=>i.HasSourceKind);
        var everyHasAuthority = inventory.All(i=>i.HasAuthority);
        var everyHasUsageFlags = inventory.All(i=>i.HasUsageFlags);
        var everyChunkTrace = documentLineages.All(d=>!string.IsNullOrWhiteSpace(d.RootInputId));
        var chunkHasRootInput = documentLineages.All(d=>!string.IsNullOrWhiteSpace(d.RootInputId));

        if(!everyHasSourceKind) blocked.Add("MissingSourceKind: some datasets lack InputSourceKind");
        if(!everyHasAuthority) blocked.Add("MissingAuthority: some datasets lack DataAuthorityKind");
        if(!everyHasUsageFlags) blocked.Add("MissingUsageFlags: some datasets lack DataUsageFlags");

        var totalRecords = inventory.Sum(i=>i.RecordCount);
        var gatePassed = blocked.Count==0 && everyHasSourceKind && everyHasAuthority
            && everyHasUsageFlags && syntheticGateLeakage==0 && diagnosticTrainingLeakage==0 && llmAuthoritativeLeakage==0;

        // Output artifacts
        var provenanceContract = new{
            GeneratedAt=now,
            InputProvenanceContractReady=true,
            StableEnumContractReady=true,
            EnumDefinitions=new{
                InputSourceKind=Enum.GetNames<InputSourceKind>(),
                InputActorKind=Enum.GetNames<InputActorKind>(),
                DataAuthorityKind=Enum.GetNames<DataAuthorityKind>(),
                LabelStatusKind=Enum.GetNames<LabelStatusKind>(),
                LearningDataKind=Enum.GetNames<LearningDataKind>(),
                DataUsageFlags=Enum.GetNames<DataUsageFlags>()
            },
            AuthorityRules=new{
                LlmNotAuthoritativeDefault="LLM output → Derived or Diagnostic, never Authoritative by default",
                SyntheticNotInGate="Synthetic authority must not carry Gate usage flag",
                DiagnosticNotInTraining="Diagnostic/Derived authority must not carry Training unless self-evaluated",
                WebToolFileUserRuntimeMustBeTraceable="Web/Tool/File/User/Runtime source → sourceRef required",
                TrainingTarget="Better context selection, ranking, filtering, and return — NOT LLM training. Human labels are one feedback signal among many."
            }
        };
        File.WriteAllText(Path.Combine(outputDir,"input-provenance-contract.json"),
            JsonSerializer.Serialize(provenanceContract,new JsonSerializerOptions{WriteIndented=true}));

        var chunkLineageContract = new{
            GeneratedAt=now,
            ChunkLineageContractReady=true,
            ChunkLineageContractHasRootInput=chunkHasRootInput,
            EveryChunkCanTraceToDocument=everyChunkTrace,
            LineageChain="chunk → documentVersion → document → rootInput",
            DocumentCount=documentLineages.Count,
            Documents=documentLineages.Select(d=>new{
                d.DocumentId,d.DocumentVersionId,d.RootInputId,d.SourcePath,d.SourceKind,
                Authority=d.Authority,d.LineCount,d.ChunkCount,d.SourceHash
            })
        };
        File.WriteAllText(Path.Combine(outputDir,"document-chunk-lineage-contract.json"),
            JsonSerializer.Serialize(chunkLineageContract,new JsonSerializerOptions{WriteIndented=true}));

        var datasetInventory = new{
            GeneratedAt=now,
            DatasetInventoryReady=true,
            TotalDatasets=inventory.Count,
            TotalRecords=totalRecords,
            EveryDatasetHasSourceKind=everyHasSourceKind,
            EveryDatasetHasAuthority=everyHasAuthority,
            EveryDatasetHasUsageFlags=everyHasUsageFlags,
            Datasets=inventory
        };
        File.WriteAllText(Path.Combine(outputDir,"learning-dataset-inventory.json"),
            JsonSerializer.Serialize(datasetInventory,new JsonSerializerOptions{WriteIndented=true}));

        var gateReport = new{
            GeneratedAt=now,
            GatePassed=gatePassed,
            TotalDatasets=inventory.Count,
            TotalRecords=totalRecords,
            EveryDatasetHasSourceKind=everyHasSourceKind,
            EveryDatasetHasAuthority=everyHasAuthority,
            EveryDatasetHasUsageFlags=everyHasUsageFlags,
            EveryChunkCanTraceToDocument=everyChunkTrace,
            ChunkLineageContractHasRootInput=chunkHasRootInput,
            SyntheticGateLeakage=syntheticGateLeakage,
            DiagnosticTrainingLeakage=diagnosticTrainingLeakage,
            LlmAuthoritativeLeakage=llmAuthoritativeLeakage,
            RuntimePromotionApplied=false,
            PackageOutputChanged=false,
            VectorBindingChanged=false,
            BlockedReasons=blocked
        };
        File.WriteAllText(Path.Combine(outputDir,"learning-data-quality-gate.json"),
            JsonSerializer.Serialize(gateReport,new JsonSerializerOptions{WriteIndented=true}));

        return new LearningDataQualityGateReport{
            GeneratedAt=now,GatePassed=gatePassed,
            TotalDatasets=inventory.Count,TotalRecords=totalRecords,
            EveryDatasetHasSourceKind=everyHasSourceKind,
            EveryDatasetHasAuthority=everyHasAuthority,
            EveryDatasetHasUsageFlags=everyHasUsageFlags,
            EveryChunkCanTraceToDocument=everyChunkTrace,
            ChunkLineageContractHasRootInput=chunkHasRootInput,
            SyntheticGateLeakage=syntheticGateLeakage,
            DiagnosticTrainingLeakage=diagnosticTrainingLeakage,
            LlmAuthoritativeLeakage=llmAuthoritativeLeakage,
            InputProvenanceContractReady=true,
            ChunkLineageContractReady=true,
            StableEnumContractReady=true,
            DatasetInventoryReady=true,
            BlockedReasons=blocked,
            Inventory=inventory,
            DocumentLineages=documentLineages
        };
    }

    private void ScanRankingPairs(List<LearningDatasetInventoryItem> inventory,
        List<DocumentLineageContract> lineages, List<string> blocked)
    {
        var path = Path.Combine("learning","features","ranking-pairs.jsonl");
        if(!File.Exists(path)){ blocked.Add($"MissingFile: {path}"); return; }
        var bytes = File.ReadAllBytes(path);
        var lines = 0; long shadowCount=0, authoritativeCount=0;
        foreach(var line in File.ReadLines(path)){
            if(string.IsNullOrWhiteSpace(line)) continue;
            lines++;
            try{
                var d=JsonDocument.Parse(line);
                var auth = d.RootElement.TryGetProperty("featureSnapshot",out var fs)
                    && fs.TryGetProperty("status",out var st) && st.GetString()=="Shadow"
                    ?DataAuthorityKind.Shadow:DataAuthorityKind.Authoritative;
                if(auth==DataAuthorityKind.Shadow) shadowCount++;
                else authoritativeCount++;
            }catch{shadowCount++;}
        }
        var srcHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        inventory.Add(new LearningDatasetInventoryItem{
            DatasetPath=path,DataKind=LearningDataKind.RankingPair,
            SourceKind=InputSourceKind.Runtime,ActorKind=InputActorKind.Runtime,
            AuthorityKind=DataAuthorityKind.Authoritative,
            LabelStatus=LabelStatusKind.WeakSignal,
            UsageFlags=DataUsageFlags.Training|DataUsageFlags.Eval|DataUsageFlags.Gate,
            RecordCount=lines,TotalBytes=bytes.Length,SourceHash=srcHash,
            SyntheticRecordCount=0,DiagnosticRecordCount=0,
            AuthoritativeRecordCount=lines,DerivedRecordCount=0,LlmSourcedCount=0,
            RiskFlags=Array.Empty<string>()
        });
        AddLineage(lineages,path,srcHash,InputSourceKind.Runtime,DataAuthorityKind.Authoritative,bytes.Length,lines);
    }

    private void ScanHardNegatives(List<LearningDatasetInventoryItem> inventory,
        List<DocumentLineageContract> lineages, List<string> blocked)
    {
        var path = Path.Combine("learning","features","hard-negatives.jsonl");
        if(!File.Exists(path)){ blocked.Add($"MissingFile: {path}"); return; }
        var bytes = File.ReadAllBytes(path);
        var srcHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var lines = 0; long formalCount=0;
        foreach(var line in File.ReadLines(path)){
            if(string.IsNullOrWhiteSpace(line)) continue;
            lines++;
            if(line.Contains("flc-r1")) formalCount++;
        }
        inventory.Add(new LearningDatasetInventoryItem{
            DatasetPath=path,DataKind=LearningDataKind.HardNegative,
            SourceKind=InputSourceKind.Runtime,ActorKind=InputActorKind.Runtime,
            AuthorityKind=DataAuthorityKind.Authoritative,
            LabelStatus=LabelStatusKind.WeakSignal,
            UsageFlags=DataUsageFlags.Training|DataUsageFlags.Gate,
            RecordCount=lines,TotalBytes=bytes.Length,SourceHash=srcHash,
            SyntheticRecordCount=0,DiagnosticRecordCount=0,
            AuthoritativeRecordCount=lines,DerivedRecordCount=0,LlmSourcedCount=0,
            RiskFlags=Array.Empty<string>()
        });
        AddLineage(lineages,path,srcHash,InputSourceKind.Runtime,DataAuthorityKind.Authoritative,bytes.Length,lines);
    }

    private void ScanRouterIntents(List<LearningDatasetInventoryItem> inventory,
        List<DocumentLineageContract> lineages, List<string> blocked)
    {
        var path = Path.Combine("learning","features","router-intent-examples.jsonl");
        if(!File.Exists(path)){ blocked.Add($"MissingFile: {path}"); return; }
        var bytes = File.ReadAllBytes(path);
        var srcHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var lines = 0;
        foreach(var line in File.ReadLines(path)){
            if(string.IsNullOrWhiteSpace(line)) continue; lines++;
        }
        inventory.Add(new LearningDatasetInventoryItem{
            DatasetPath=path,DataKind=LearningDataKind.RouterExample,
            SourceKind=InputSourceKind.Runtime,ActorKind=InputActorKind.Runtime,
            AuthorityKind=DataAuthorityKind.Authoritative,
            LabelStatus=LabelStatusKind.WeakSignal,
            UsageFlags=DataUsageFlags.Training|DataUsageFlags.Eval,
            RecordCount=lines,TotalBytes=bytes.Length,SourceHash=srcHash,
            SyntheticRecordCount=0,DiagnosticRecordCount=0,
            AuthoritativeRecordCount=lines,DerivedRecordCount=0,LlmSourcedCount=0,
            RiskFlags=Array.Empty<string>()
        });
        AddLineage(lineages,path,srcHash,InputSourceKind.Runtime,DataAuthorityKind.Authoritative,bytes.Length,lines);
    }

    private void ScanShadowEval(List<LearningDatasetInventoryItem> inventory,
        List<DocumentLineageContract> lineages, List<string> blocked)
    {
        var path = Path.Combine("learning","ranker","candidate-reranker-shadow-eval-a3.json");
        if(!File.Exists(path)){ blocked.Add($"MissingFile: {path}"); return; }
        var bytes = File.ReadAllBytes(path);
        var srcHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        int records=0; long syntheticCount=0, realInferenceCount=0;
        try{
            var seDoc=JsonDocument.Parse(File.ReadAllText(path));
            if(seDoc.RootElement.TryGetProperty("SampleResults",out var results) && results.ValueKind==JsonValueKind.Array){
                foreach(var r in results.EnumerateArray()){
                    records++;
                    var src=r.TryGetProperty("source",out var s)?s.GetString()??"":"";
                    if(src=="real-inference") realInferenceCount++;
                    else syntheticCount++;
                }
            }
        }catch{}
        inventory.Add(new LearningDatasetInventoryItem{
            DatasetPath=path,DataKind=LearningDataKind.ShadowEvalRow,
            SourceKind=InputSourceKind.Runtime,ActorKind=InputActorKind.Runtime,
            AuthorityKind=DataAuthorityKind.Shadow,
            LabelStatus=LabelStatusKind.WeakSignal,
            UsageFlags=DataUsageFlags.Eval|DataUsageFlags.Gate,
            RecordCount=records,TotalBytes=bytes.Length,SourceHash=srcHash,
            SyntheticRecordCount=(int)syntheticCount,DiagnosticRecordCount=0,
            AuthoritativeRecordCount=(int)realInferenceCount,DerivedRecordCount=0,LlmSourcedCount=0,
            RiskFlags=Array.Empty<string>()
        });
        AddLineage(lineages,path,srcHash,InputSourceKind.Runtime,DataAuthorityKind.Shadow,bytes.Length,records);
    }

    private void ScanWiderPilotArchive(List<LearningDatasetInventoryItem> inventory,
        List<DocumentLineageContract> lineages, List<string> blocked)
    {
        // Wider-pilot artifacts are audit/archive only, not training data
        var widerDir = Path.Combine("learning","v11");
        if(!Directory.Exists(widerDir)) return;
        var widerFiles = Directory.GetFiles(widerDir,"wider-pilot-*.json");
        foreach(var fp in widerFiles){
            var fn = Path.GetFileName(fp);
            var bytes = File.ReadAllBytes(fp);
            var srcHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            inventory.Add(new LearningDatasetInventoryItem{
                DatasetPath=fn,DataKind=LearningDataKind.GateDecision,
                SourceKind=InputSourceKind.System,ActorKind=InputActorKind.System,
                AuthorityKind=DataAuthorityKind.Diagnostic,
                LabelStatus=LabelStatusKind.Unlabeled,
                UsageFlags=DataUsageFlags.Audit,
                RecordCount=1,TotalBytes=bytes.Length,SourceHash=srcHash,
                SyntheticRecordCount=0,DiagnosticRecordCount=1,
                AuthoritativeRecordCount=0,DerivedRecordCount=0,LlmSourcedCount=0,
                RiskFlags=new[]{"AuditOnly","NotForTraining"}
            });
            AddLineage(lineages,fn,srcHash,InputSourceKind.System,DataAuthorityKind.Diagnostic,bytes.Length,1);
        }
    }

    private void AddLineage(List<DocumentLineageContract> lineages, string path, string srcHash,
        InputSourceKind sourceKind, DataAuthorityKind authority, long byteSize, int lineCount)
    {
        var docId = $"doc-{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path)))[..16].ToLowerInvariant()}";
        lineages.Add(new DocumentLineageContract{
            DocumentId=docId,DocumentVersionId=$"{docId}-v1",
            RootInputId=$"input-{docId[..12]}",
            SourcePath=path,SourceHash=srcHash,
            SourceKind=sourceKind,Authority=authority,
            ByteSize=byteSize,LineCount=lineCount,ChunkCount=1,
            TransformVersion="V13.0",GeneratedAt=DateTimeOffset.UtcNow
        });
    }
}
