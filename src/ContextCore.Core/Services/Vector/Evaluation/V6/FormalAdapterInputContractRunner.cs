using System.Reflection;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Formal Adapter Input Contract Enforcement。
/// 固定未来 formal adapter 可读取的 runtime 输入，并把 Dataset/Eval、gold label、
/// shadow artifact 与 sample metadata 字段隔离在正式输入合同之外。
/// </summary>
public sealed class FormalAdapterInputContractRunner
{
    public const string ContractVersion = "formal-adapter-input-contract-v1";

    private static readonly string[] RuntimeInputTypes =
    [
        nameof(FormalAdapterRuntimeInputEnvelope),
        nameof(FormalAdapterRuntimePackageContext),
        nameof(FormalAdapterRuntimeCandidateInput),
        nameof(FormalAdapterRuntimeProvenanceInput),
        nameof(FormalAdapterRuntimeRelationEvidenceInput)
    ];

    public FormalAdapterInputContractReport BuildContract(
        ShadowFormalRetrievalAdapterPlanReport? planGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        FormalAdapterInputContractSourceScan? sourceScan,
        FormalAdapterInputContractOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(planGate, outputPolicyGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: false);

    public FormalAdapterInputContractReport BuildGate(
        ShadowFormalRetrievalAdapterPlanReport? planGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        FormalAdapterInputContractSourceScan? sourceScan,
        FormalAdapterInputContractOptions? options = null,
        IReadOnlyDictionary<string, string>? sourceReports = null)
        => Build(planGate, outputPolicyGate, runtimeChangeGate, sourceScan, options, sourceReports, gateMode: true);

    public static IReadOnlyList<FormalAdapterInputContractField> BuildAllowedFields()
        =>
        [
            Field("request.requestId", "Request", "formal request envelope", "trace correlation only", required: true),
            Field("request.workspaceId", "Scope", "runtime request scope", "allowlist and provider scope isolation", required: true),
            Field("request.collectionId", "Scope", "runtime request scope", "allowlist and provider scope isolation", required: true),
            Field("request.queryText", "Query", "runtime user query", "query tokenization and candidate scoring", required: true),
            Field("request.queryAnchors", "Query", "runtime-derived anchors", "optional source-aware scoring signals", required: false),
            Field("package.baselinePackageId", "PackageContext", "current package snapshot", "shadow comparison identity only", required: false),
            Field("package.baselineCandidateIds", "PackageContext", "current formal selected set snapshot", "selected-set preserving comparison", required: false),
            Field("package.sectionTokenBudgets", "PackageContext", "runtime package constraints", "token budget shadow validation", required: true),
            Field("package.sectionOccupancy", "PackageContext", "runtime package snapshot", "section occupancy comparison", required: false),
            Field("package.totalTokenBudget", "PackageContext", "runtime package constraints", "budget guard", required: true),
            Field("candidate.candidateId", "Candidate", "candidate provider output", "identity and stable tie-break", required: true),
            Field("candidate.itemId", "Candidate", "candidate provider output", "identity and stable tie-break only; no business special case", required: true),
            Field("candidate.sourceId", "Candidate", "source metadata", "source trace and dedupe", required: false),
            Field("candidate.content", "Candidate", "runtime item content", "dense/lexical scoring", required: true),
            Field("candidate.itemKind", "CandidateMetadata", "runtime item metadata", "generic source-aware scoring and filtering", required: true),
            Field("candidate.sourceKind", "CandidateMetadata", "runtime source metadata", "generic source-aware scoring and filtering", required: true),
            Field("candidate.layer", "CandidateMetadata", "runtime item metadata", "eligibility and routing gate", required: false),
            Field("candidate.lifecycle", "Eligibility", "runtime lifecycle metadata", "lifecycle gate", required: true),
            Field("candidate.reviewStatus", "Eligibility", "runtime review metadata", "eligibility gate", required: true),
            Field("candidate.replacementState", "Eligibility", "runtime replacement metadata", "superseded guard", required: true),
            Field("candidate.targetSection", "Routing", "runtime item metadata", "section routing and risk gate", required: true),
            Field("candidate.tags", "CandidateMetadata", "runtime item metadata", "generic anchor/source scoring", required: false),
            Field("candidate.anchors", "CandidateMetadata", "runtime item metadata", "generic anchor/source scoring", required: false),
            Field("candidate.sourceRefs", "Evidence", "runtime item metadata", "source evidence projection", required: false),
            Field("candidate.evidenceRefs", "Evidence", "runtime item metadata", "evidence projection", required: false),
            Field("candidate.provenance.recordId", "Provenance", "runtime provenance metadata", "trace and audit", required: false),
            Field("candidate.provenance.sourceFingerprint", "Provenance", "runtime provenance metadata", "source identity and trace", required: false),
            Field("candidate.provenance.ingestionBatchId", "Provenance", "runtime provenance metadata", "trace and audit", required: false),
            Field("candidate.relations", "RelationEvidence", "read-only relation evidence", "graph expansion and confidence gate", required: false),
            Field("candidate.estimatedTokens", "PackageContext", "runtime package estimator", "shadow token budget validation", required: false),
            Field("candidate.score", "Candidate", "candidate provider output", "pre-fusion score only", required: false),
            Field("candidate.denseRank", "Candidate", "candidate provider output", "dense winner preservation", required: false),
            Field("candidate.lexicalRank", "Candidate", "candidate provider output", "source contribution diagnostics", required: false),
            Field("candidate.anchorRank", "Candidate", "candidate provider output", "source contribution diagnostics", required: false)
        ];

    public static IReadOnlyList<FormalAdapterDeniedInputField> BuildDeniedFields()
        =>
        [
            Denied("RetrievalDatasetV2Sample", "DatasetEvalField", "formal adapter must not accept eval sample DTOs"),
            Denied("RetrievalDatasetV2GeneratedDataset", "DatasetEvalField", "formal adapter must not accept generated dataset containers"),
            Denied("SampleId", "SampleMetadata", "sample identity is an eval artifact, not runtime input"),
            Denied("SourceEvalSet", "SampleMetadata", "eval set identity must not affect runtime retrieval"),
            Denied("Split", "SampleMetadata", "train/dev/test/holdout split is eval-only"),
            Denied("Difficulty", "SampleMetadata", "difficulty labels are eval-only"),
            Denied("TaskKind", "SampleMetadata", "task kind labels from generated samples are eval-only"),
            Denied("Intent", "SampleMetadata", "sample intent labels are eval-only unless produced by runtime router contract"),
            Denied("Rationale", "SampleMetadata", "rationale must not enter indexed/runtime scoring text"),
            Denied("MustHitItemIds", "GoldLabel", "must-hit labels are gold labels"),
            Denied("MustNotHitItemIds", "GoldLabel", "must-not labels are gold labels"),
            Denied("NegativeDistractorIds", "GoldLabel", "negative distractor labels are gold labels"),
            Denied("ExpectedTargetSection", "GoldLabel", "expected section is an eval label"),
            Denied("RequiredRelations", "EvalAnnotation", "required relations from sample labels are eval-only"),
            Denied("sample.SourceRefs", "EvalAnnotation", "source refs from samples are eval annotations; use item/source metadata"),
            Denied("sample.EvidenceRefs", "EvalAnnotation", "evidence refs from samples are eval annotations; use item/source metadata"),
            Denied("sample.Metadata", "SampleMetadata", "free-form sample metadata must not be runtime adapter input"),
            Denied("ShadowFormalRetrievalAdapterReport", "ShadowArtifact", "shadow reports are eval artifacts"),
            Denied("FormalAdapterPackageShadowComparisonReport", "ShadowArtifact", "package shadow reports are eval artifacts"),
            Denied("OutputTokenPriorityShadowGateReport", "ShadowArtifact", "output-token shadow reports are eval artifacts"),
            Denied("SourceAwareRankingRepairReport", "ShadowArtifact", "source-aware repair reports are eval artifacts"),
            Denied("RetrievalEvalProtocolGateReport", "ShadowArtifact", "eval protocol reports are not runtime input"),
            Denied("BlindHoldout", "ShadowArtifact", "blind holdout artifacts are eval-only"),
            Denied("GatePassed", "ShadowArtifact", "gate result fields must not drive runtime scoring"),
            Denied("Recommendation", "ShadowArtifact", "recommendation fields must not drive runtime scoring")
        ];

    public static string BuildMarkdown(string title, FormalAdapterInputContractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.CreatedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine($"- ContractPassed: `{report.ContractPassed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ContractVersion: `{report.ContractVersion}`");
        builder.AppendLine($"- AllowedMode: `{report.AllowedMode}`");
        builder.AppendLine($"- RequiredNextPhase: `{report.RequiredNextPhase}`");
        builder.AppendLine($"- RuntimeInputTypeCount: `{report.RuntimeInputTypeCount}`");
        builder.AppendLine($"- RuntimeInputFieldCount: `{report.RuntimeInputFieldCount}`");
        builder.AppendLine($"- DeniedFieldCount: `{report.DeniedFieldCount}`");
        builder.AppendLine($"- ContractForbiddenPropertyCount: `{report.ContractForbiddenPropertyCount}`");
        builder.AppendLine($"- FormalSourceForbiddenReadCount: `{report.FormalSourceForbiddenReadCount}`");
        builder.AppendLine($"- EvalOnlyForbiddenReadCount: `{report.EvalOnlyForbiddenReadCount}`");
        builder.AppendLine($"- DatasetEvalFieldsBlocked: `{report.DatasetEvalFieldsBlocked}`");
        builder.AppendLine($"- GoldLabelsBlocked: `{report.GoldLabelsBlocked}`");
        builder.AppendLine($"- SampleMetadataBlocked: `{report.SampleMetadataBlocked}`");
        builder.AppendLine($"- ShadowArtifactFieldsBlocked: `{report.ShadowArtifactFieldsBlocked}`");
        builder.AppendLine($"- CurrentShadowAdapterEvalOnly: `{report.CurrentShadowAdapterEvalOnly}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine($"- VectorStoreBindingChanged: `{report.VectorStoreBindingChanged}`");
        AppendList(builder, "Runtime Input Types", report.RuntimeInputTypes);
        AppendAllowed(builder, report.AllowedRuntimeInputs);
        AppendDenied(builder, report.DeniedInputs);
        AppendSourceScan(builder, report.SourceScan);
        AppendList(builder, "Blocked Reasons", report.BlockedReasons);
        AppendMap(builder, "Source Reports", report.SourceReports);
        builder.AppendLine();
        builder.AppendLine("This is a contract/enforcement artifact only. Existing Dataset V2 and shadow reports may remain in eval runners, but they are not allowed as future formal adapter runtime inputs.");
        return builder.ToString();
    }

    private static FormalAdapterInputContractReport Build(
        ShadowFormalRetrievalAdapterPlanReport? planGate,
        OutputTokenPriorityShadowGateReport? outputPolicyGate,
        LearningRuntimeChangeReadinessGateReport? runtimeChangeGate,
        FormalAdapterInputContractSourceScan? sourceScan,
        FormalAdapterInputContractOptions? options,
        IReadOnlyDictionary<string, string>? sourceReports,
        bool gateMode)
    {
        options ??= new FormalAdapterInputContractOptions();
        var blocked = new List<string>();
        if (options.RequireV51PlanGatePassed && (planGate is null || !planGate.PlanPassed))
        {
            blocked.Add("V51AdapterPlanGateMissingOrNotPassed");
        }

        if (options.RequireV515OutputPolicyGatePassed && (outputPolicyGate is null || !outputPolicyGate.GatePassed))
        {
            blocked.Add("V515OutputPolicyGateMissingOrNotPassed");
        }

        if (options.RequireRuntimeChangeGate && (runtimeChangeGate is null || !runtimeChangeGate.Passed))
        {
            blocked.Add("RuntimeChangeReadinessGateNotPassed");
        }

        if (options.RequireSourceScan && (sourceScan is null || !sourceScan.ScanPerformed))
        {
            blocked.Add("SourceScanMissing");
        }

        sourceScan ??= new FormalAdapterInputContractSourceScan();
        var forbiddenContractProperties = FindForbiddenRuntimeContractProperties();
        if (forbiddenContractProperties.Count > 0)
        {
            blocked.Add("RuntimeInputContractContainsForbiddenField");
        }

        if (sourceScan.FormalSourceForbiddenReadCount > 0)
        {
            blocked.Add("FormalAdapterSourceReadsForbiddenField");
        }

        if (options.UseForRuntime
            || options.FormalRetrievalAllowed
            || options.RuntimeSwitchAllowed
            || options.ReadyForRuntimeSwitch
            || options.WriteFormalPackage
            || options.MutatePackingPolicy
            || options.MutatePackageOutput
            || options.MutateVectorStoreBinding)
        {
            blocked.Add("RuntimeOrFormalMutationAttempt");
        }

        AddBoundaryBlocks(blocked, "V51Plan", planGate);
        AddBoundaryBlocks(blocked, "V515OutputPolicy", outputPolicyGate);

        var denied = BuildDeniedFields();
        var allowed = BuildAllowedFields();
        var distinctBlocked = blocked
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var passed = distinctBlocked.Length == 0;

        return new FormalAdapterInputContractReport
        {
            OperationId = (gateMode ? "formal-adapter-input-contract-gate-" : "formal-adapter-input-contract-")
                + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ContractPassed = passed,
            GatePassed = gateMode && passed,
            Recommendation = ResolveRecommendation(passed, distinctBlocked),
            ContractVersion = ContractVersion,
            AllowedMode = "ContractOnly",
            RequiredNextPhase = "FormalAdapterImplementationPreflight",
            RuntimeInputTypes = RuntimeInputTypes,
            RuntimeInputTypeCount = RuntimeInputTypes.Length,
            RuntimeInputFieldCount = allowed.Count,
            DeniedFieldCount = denied.Count,
            AllowedRuntimeInputs = allowed,
            DeniedInputs = denied,
            ContractForbiddenPropertyCount = forbiddenContractProperties.Count,
            ContractForbiddenProperties = forbiddenContractProperties,
            FormalSourceForbiddenReadCount = sourceScan.FormalSourceForbiddenReadCount,
            EvalOnlyForbiddenReadCount = sourceScan.EvalOnlyForbiddenReadCount,
            DatasetEvalFieldsBlocked = denied.Any(static field => field.Category == "DatasetEvalField"),
            GoldLabelsBlocked = denied.Any(static field => field.Category == "GoldLabel"),
            SampleMetadataBlocked = denied.Any(static field => field.Category == "SampleMetadata"),
            ShadowArtifactFieldsBlocked = denied.Any(static field => field.Category == "ShadowArtifact"),
            CurrentShadowAdapterEvalOnly = true,
            V51PlanGatePassed = planGate?.PlanPassed ?? false,
            V515OutputPolicyGatePassed = outputPolicyGate?.GatePassed ?? false,
            RuntimeChangeGatePassed = runtimeChangeGate?.Passed ?? false,
            SourceScan = sourceScan,
            FormalOutputChanged = 0,
            FormalSelectedSetChanged = false,
            FormalPackageWritten = false,
            PackageOutputChanged = false,
            PackingPolicyChanged = false,
            RuntimeMutated = false,
            VectorStoreBindingChanged = false,
            FormalRetrievalAllowed = false,
            RuntimeSwitchAllowed = false,
            ReadyForRuntimeSwitch = false,
            UseForRuntime = false,
            SourceReports = sourceReports ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BlockedReasons = distinctBlocked
        };
    }

    private static FormalAdapterInputContractField Field(
        string fieldId,
        string category,
        string source,
        string usage,
        bool required)
        => new()
        {
            FieldId = fieldId,
            Category = category,
            RuntimeSource = source,
            AllowedUsage = usage,
            Required = required
        };

    private static FormalAdapterDeniedInputField Denied(string fieldId, string category, string reason)
        => new()
        {
            FieldId = fieldId,
            Category = category,
            Reason = reason
        };

    private static IReadOnlyList<string> FindForbiddenRuntimeContractProperties()
    {
        var deniedNames = BuildDeniedFields()
            .Select(static field => field.FieldId)
            .Where(static value => !value.Contains('.', StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var properties = typeof(FormalAdapterRuntimeInputEnvelope).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Concat(typeof(FormalAdapterRuntimePackageContext).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Concat(typeof(FormalAdapterRuntimeCandidateInput).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Concat(typeof(FormalAdapterRuntimeProvenanceInput).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Concat(typeof(FormalAdapterRuntimeRelationEvidenceInput).GetProperties(BindingFlags.Public | BindingFlags.Instance));
        return properties
            .Select(static property => property.Name)
            .Where(name => deniedNames.Contains(name))
            .Where(static name => !string.Equals(name, "ItemId", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddBoundaryBlocks(
        List<string> blocked,
        string prefix,
        ShadowFormalRetrievalAdapterPlanReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed) blocked.Add(prefix + "FormalRetrievalAllowed");
        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch || report.UseForRuntime) blocked.Add(prefix + "RuntimeSwitchAllowed");
        if (report.FormalPackageWritten) blocked.Add(prefix + "FormalPackageWritten");
        if (report.PackageOutputChanged) blocked.Add(prefix + "PackageOutputChanged");
        if (report.PackingPolicyChanged) blocked.Add(prefix + "PackingPolicyChanged");
        if (report.VectorStoreBindingChanged) blocked.Add(prefix + "VectorStoreBindingChanged");
    }

    private static void AddBoundaryBlocks(
        List<string> blocked,
        string prefix,
        OutputTokenPriorityShadowGateReport? report)
    {
        if (report is null)
        {
            return;
        }

        if (report.FormalRetrievalAllowed) blocked.Add(prefix + "FormalRetrievalAllowed");
        if (report.RuntimeSwitchAllowed || report.ReadyForRuntimeSwitch || report.UseForRuntime) blocked.Add(prefix + "RuntimeSwitchAllowed");
        if (report.FormalPackageWritten) blocked.Add(prefix + "FormalPackageWritten");
        if (report.PackageOutputChanged) blocked.Add(prefix + "PackageOutputChanged");
        if (report.PackingPolicyChanged) blocked.Add(prefix + "PackingPolicyChanged");
        if (report.RuntimeMutated) blocked.Add(prefix + "RuntimeMutated");
        if (report.VectorStoreBindingChanged) blocked.Add(prefix + "VectorStoreBindingChanged");
    }

    private static string ResolveRecommendation(bool passed, IReadOnlyList<string> blocked)
    {
        if (passed)
        {
            return FormalAdapterInputContractRecommendations.ReadyForFormalAdapterInputContractFreeze;
        }

        if (blocked.Contains("RuntimeInputContractContainsForbiddenField", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterInputContractRecommendations.BlockedByContractForbiddenField;
        }

        if (blocked.Contains("FormalAdapterSourceReadsForbiddenField", StringComparer.OrdinalIgnoreCase))
        {
            return FormalAdapterInputContractRecommendations.BlockedByFormalSourceForbiddenRead;
        }

        if (blocked.Any(static reason => reason.Contains("Missing", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("NotPassed", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("RuntimeChange", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterInputContractRecommendations.BlockedByMissingPrerequisiteGate;
        }

        if (blocked.Any(static reason => reason.Contains("Runtime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Package", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("PackingPolicy", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("VectorStoreBinding", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("FormalRetrieval", StringComparison.OrdinalIgnoreCase)))
        {
            return FormalAdapterInputContractRecommendations.BlockedByRuntimeInvariant;
        }

        return FormalAdapterInputContractRecommendations.KeepPreviewOnly;
    }

    private static void AppendAllowed(StringBuilder builder, IReadOnlyList<FormalAdapterInputContractField> values)
    {
        builder.AppendLine();
        builder.AppendLine("## Allowed Runtime Inputs");
        foreach (var item in values)
        {
            builder.AppendLine($"- `{item.FieldId}` ({item.Category}) source=`{item.RuntimeSource}` usage=`{item.AllowedUsage}` required=`{item.Required}`");
        }
    }

    private static void AppendDenied(StringBuilder builder, IReadOnlyList<FormalAdapterDeniedInputField> values)
    {
        builder.AppendLine();
        builder.AppendLine("## Denied Inputs");
        foreach (var item in values)
        {
            builder.AppendLine($"- `{item.FieldId}` ({item.Category}) reason=`{item.Reason}`");
        }
    }

    private static void AppendSourceScan(StringBuilder builder, FormalAdapterInputContractSourceScan scan)
    {
        builder.AppendLine();
        builder.AppendLine("## Source Scan");
        builder.AppendLine($"- ScanPerformed: `{scan.ScanPerformed}`");
        builder.AppendLine($"- FormalSourceFileCount: `{scan.FormalSourceFileCount}`");
        builder.AppendLine($"- EvalOnlySourceFileCount: `{scan.EvalOnlySourceFileCount}`");
        builder.AppendLine($"- FormalSourceForbiddenReadCount: `{scan.FormalSourceForbiddenReadCount}`");
        builder.AppendLine($"- EvalOnlyForbiddenReadCount: `{scan.EvalOnlyForbiddenReadCount}`");
        foreach (var hit in scan.Hits.Take(40))
        {
            builder.AppendLine($"- `{hit.FilePath}` token=`{hit.Token}` category=`{hit.Category}` formal=`{hit.IsFormalSource}`");
        }
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }

    private static void AppendMap(StringBuilder builder, string title, IReadOnlyDictionary<string, string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var item in values.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {item.Key}: `{item.Value}`");
        }
    }
}

public sealed class FormalAdapterInputContractOptions
{
    public bool RequireV51PlanGatePassed { get; init; } = true;

    public bool RequireV515OutputPolicyGatePassed { get; init; } = true;

    public bool RequireRuntimeChangeGate { get; init; } = true;

    public bool RequireSourceScan { get; init; } = true;

    public bool UseForRuntime { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool WriteFormalPackage { get; init; }

    public bool MutatePackingPolicy { get; init; }

    public bool MutatePackageOutput { get; init; }

    public bool MutateVectorStoreBinding { get; init; }
}

public sealed class FormalAdapterInputContractField
{
    public string FieldId { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string RuntimeSource { get; init; } = string.Empty;

    public string AllowedUsage { get; init; } = string.Empty;

    public bool Required { get; init; }
}

public sealed class FormalAdapterDeniedInputField
{
    public string FieldId { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;
}

public sealed class FormalAdapterInputContractSourceHit
{
    public string FilePath { get; init; } = string.Empty;

    public string Token { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public bool IsFormalSource { get; init; }
}

public sealed class FormalAdapterInputContractSourceScan
{
    public bool ScanPerformed { get; init; }

    public int FormalSourceFileCount { get; init; }

    public int EvalOnlySourceFileCount { get; init; }

    public int FormalSourceForbiddenReadCount { get; init; }

    public int EvalOnlyForbiddenReadCount { get; init; }

    public IReadOnlyList<string> FormalSourceFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> EvalOnlySourceFiles { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FormalAdapterInputContractSourceHit> Hits { get; init; } =
        Array.Empty<FormalAdapterInputContractSourceHit>();
}

public sealed class FormalAdapterInputContractReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool ContractPassed { get; init; }

    public bool GatePassed { get; init; }

    public string Recommendation { get; init; } = FormalAdapterInputContractRecommendations.KeepPreviewOnly;

    public string ContractVersion { get; init; } = FormalAdapterInputContractRunner.ContractVersion;

    public string AllowedMode { get; init; } = "ContractOnly";

    public string RequiredNextPhase { get; init; } = "FormalAdapterImplementationPreflight";

    public IReadOnlyList<string> RuntimeInputTypes { get; init; } = Array.Empty<string>();

    public int RuntimeInputTypeCount { get; init; }

    public int RuntimeInputFieldCount { get; init; }

    public int DeniedFieldCount { get; init; }

    public IReadOnlyList<FormalAdapterInputContractField> AllowedRuntimeInputs { get; init; } =
        Array.Empty<FormalAdapterInputContractField>();

    public IReadOnlyList<FormalAdapterDeniedInputField> DeniedInputs { get; init; } =
        Array.Empty<FormalAdapterDeniedInputField>();

    public int ContractForbiddenPropertyCount { get; init; }

    public IReadOnlyList<string> ContractForbiddenProperties { get; init; } = Array.Empty<string>();

    public int FormalSourceForbiddenReadCount { get; init; }

    public int EvalOnlyForbiddenReadCount { get; init; }

    public bool DatasetEvalFieldsBlocked { get; init; }

    public bool GoldLabelsBlocked { get; init; }

    public bool SampleMetadataBlocked { get; init; }

    public bool ShadowArtifactFieldsBlocked { get; init; }

    public bool CurrentShadowAdapterEvalOnly { get; init; }

    public bool V51PlanGatePassed { get; init; }

    public bool V515OutputPolicyGatePassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public FormalAdapterInputContractSourceScan SourceScan { get; init; } = new();

    public int FormalOutputChanged { get; init; }

    public bool FormalSelectedSetChanged { get; init; }

    public bool FormalPackageWritten { get; init; }

    public bool PackageOutputChanged { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool VectorStoreBindingChanged { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool ReadyForRuntimeSwitch { get; init; }

    public bool UseForRuntime { get; init; }

    public IReadOnlyDictionary<string, string> SourceReports { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class FormalAdapterInputContractRecommendations
{
    public const string ReadyForFormalAdapterInputContractFreeze = nameof(ReadyForFormalAdapterInputContractFreeze);
    public const string KeepPreviewOnly = nameof(KeepPreviewOnly);
    public const string BlockedByContractForbiddenField = nameof(BlockedByContractForbiddenField);
    public const string BlockedByFormalSourceForbiddenRead = nameof(BlockedByFormalSourceForbiddenRead);
    public const string BlockedByMissingPrerequisiteGate = nameof(BlockedByMissingPrerequisiteGate);
    public const string BlockedByRuntimeInvariant = nameof(BlockedByRuntimeInvariant);
}
