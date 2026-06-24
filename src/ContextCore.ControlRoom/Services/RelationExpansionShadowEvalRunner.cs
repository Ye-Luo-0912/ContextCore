using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Planning;
using ContextCore.Storage.InMemory;

namespace ContextCore.ControlRoom.Services;

/// <summary>在 eval 样本上运行 relation expansion profile preview，不改变正式 retrieval/package 输出。</summary>
public sealed class RelationExpansionShadowEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly string[] Categories = ["chat", "project", "novel", "automation", "coding-mode"];

    private readonly RelationExpansionProfileRegistry _profileRegistry;
    private readonly RelationTypeRegistry _relationTypeRegistry;
    private readonly RelationTypeNormalizer _typeNormalizer;
    private readonly PlanningIntentDetector _intentDetector;

    public RelationExpansionShadowEvalRunner()
        : this(new RelationExpansionProfileRegistry(), new RelationTypeRegistry(), new PlanningIntentDetector())
    {
    }

    public RelationExpansionShadowEvalRunner(
        RelationExpansionProfileRegistry profileRegistry,
        RelationTypeRegistry relationTypeRegistry,
        PlanningIntentDetector intentDetector,
        RelationTypeNormalizer? typeNormalizer = null)
    {
        _profileRegistry = profileRegistry;
        _relationTypeRegistry = relationTypeRegistry;
        _intentDetector = intentDetector;
        _typeNormalizer = typeNormalizer ?? new RelationTypeNormalizer();
    }

    public async Task<RelationExpansionShadowEvalReport> RunAsync(
        string contextsRootPath,
        string? categoryFilter = null,
        bool includeSeedBatches = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextsRootPath);

        var formalReport = await new ContextEvalRunner()
            .RunAsync(contextsRootPath, categoryFilter, includeSeedBatches)
            .ConfigureAwait(false);
        var samples = new List<RelationExpansionShadowSample>();
        var warnings = new List<string>();

        foreach (var category in Categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (categoryFilter is not null
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRootPath, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var evalSamples = await LoadCategorySamplesAsync(categoryDir, includeSeedBatches, cancellationToken)
                .ConfigureAwait(false);
            var corpus = await LoadCategoryCorpusAsync(categoryDir, includeSeedBatches, cancellationToken)
                .ConfigureAwait(false);
            var resultsById = formalReport.Results
                .Where(result => evalSamples.Any(sample => string.Equals(sample.Id, result.SampleId, StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(result => result.SampleId, StringComparer.OrdinalIgnoreCase);

            if (evalSamples.Count == 0)
            {
                continue;
            }

            var workspaceId = $"eval-{category}";
            const string collectionId = "test";
            var relationStore = new InMemoryRelationStore();
            var lifecycleByItemId = BuildLifecycleIndex(corpus);
            await SeedRelationsAsync(relationStore, corpus.Relations, workspaceId, collectionId, lifecycleByItemId, cancellationToken)
                .ConfigureAwait(false);
            var validator = new RelationExpansionPolicyValidator(_relationTypeRegistry);
            var previewService = new RelationExpansionPreviewService(relationStore, _profileRegistry, validator);

            foreach (var sample in evalSamples)
            {
                if (!resultsById.TryGetValue(sample.Id, out var formalResult))
                {
                    warnings.Add($"missing formal eval result for sample {sample.Id}");
                    continue;
                }

                samples.AddRange(await EvaluateSampleAsync(
                        formalResult,
                        sample,
                        previewService,
                        workspaceId,
                        collectionId,
                        lifecycleByItemId,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        return BuildReport(formalReport.Results.Count, includeSeedBatches, samples, warnings);
    }

    public async Task<IReadOnlyList<RelationExpansionShadowSample>> EvaluateSampleAsync(
        ContextEvalResult formalResult,
        ContextEvalSample sample,
        RelationExpansionPreviewService previewService,
        string workspaceId,
        string collectionId,
        IReadOnlyDictionary<string, string> lifecycleByItemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(formalResult);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(previewService);

        var profiles = _profileRegistry.GetAll();
        var seedItems = ResolveSeedItems(formalResult);
        var intent = ResolveIntent(sample);
        var output = new List<RelationExpansionShadowSample>();

        foreach (var profile in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accepted = new Dictionary<string, RelationExpansionPreviewRelation>(StringComparer.OrdinalIgnoreCase);
            var blocked = new Dictionary<string, RelationExpansionPreviewRelation>(StringComparer.OrdinalIgnoreCase);

            foreach (var seedItem in seedItems)
            {
                var preview = await previewService.PreviewAsync(new RelationExpansionPreviewRequest
                {
                    WorkspaceId = workspaceId,
                    CollectionId = collectionId,
                    ItemId = seedItem,
                    ProfileId = profile.ProfileId
                }, cancellationToken).ConfigureAwait(false);

                foreach (var relation in preview.AcceptedRelations)
                {
                    accepted.TryAdd(relation.RelationId, relation);
                }

                foreach (var relation in preview.BlockedRelations)
                {
                    blocked.TryAdd(relation.RelationId, relation);
                }
            }

            var selected = seedItems.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var acceptedWouldAddRelations = accepted.Values
                .Where(relation => !selected.Contains(relation.TargetId))
                .ToArray();
            var wouldAdd = acceptedWouldAddRelations
                .Select(relation => relation.TargetId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var riskIfNormalSelectedRelations = acceptedWouldAddRelations
                .Where(relation => IsRiskIfNormalSelected(sample, relation, lifecycleByItemId))
                .ToArray();
            var riskAfterSectionRoutingRelations = riskIfNormalSelectedRelations
                .Where(IsRiskAfterSectionRouting)
                .ToArray();
            var wouldAddMustHit = wouldAdd
                .Where(targetId => sample.MustHit.Any(expected => EvalIdMatches(expected, targetId)))
                .ToArray();
            var wouldAddMustNotHit = riskAfterSectionRoutingRelations
                .Where(relation => sample.MustNotHit.Any(expected => EvalIdMatches(expected, relation.TargetId)))
                .Select(relation => relation.TargetId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var wouldAddLifecycleRisk = riskAfterSectionRoutingRelations
                .Where(relation => IsLifecycleRisk(ResolveTargetLifecycle(relation, lifecycleByItemId)))
                .Select(relation => relation.TargetId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var blockedReasons = CountBlockedReasons(blocked.Values);
            var fanoutTrimmed = CountReason(blocked.Values, RelationExpansionValidationReasons.FanoutExceeded);
            var depthTrimmed = CountReason(blocked.Values, RelationExpansionValidationReasons.DepthExceeded);

            output.Add(new RelationExpansionShadowSample
            {
                SampleId = sample.Id,
                Mode = sample.Mode,
                Intent = intent,
                ProfileId = profile.ProfileId,
                SeedItems = seedItems,
                ExpandedRelations = accepted.Values.Concat(blocked.Values)
                    .OrderBy(relation => relation.RelationId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                AcceptedRelations = accepted.Values
                    .OrderBy(relation => relation.RelationId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                BlockedRelations = blocked.Values
                    .OrderBy(relation => relation.RelationId, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                WouldAddCandidates = wouldAdd,
                WouldAddMustHit = wouldAddMustHit,
                WouldAddMustNotHit = wouldAddMustNotHit,
                WouldAddLifecycleRisk = wouldAddLifecycleRisk,
                RiskIfNormalSelected = riskIfNormalSelectedRelations.Length,
                RiskAfterSectionRouting = riskAfterSectionRoutingRelations.Length,
                HistoricalAuditExpansion = accepted.Values.Count(IsHistoricalAuditExpansion),
                ConflictEvidenceExpansion = accepted.Values.Count(IsConflictEvidence),
                WrongSectionRisk = accepted.Values.Count(relation => relation.RiskAfterSectionRouting)
                    + CountReason(blocked.Values, RelationExpansionValidationReasons.BlockedByWrongSectionRisk),
                BlockedReasons = blockedReasons,
                FanoutTrimmed = fanoutTrimmed,
                DepthTrimmed = depthTrimmed,
                Recommendation = RecommendSample(
                    accepted.Count,
                    blocked.Count,
                    wouldAdd.Length,
                    wouldAddMustHit.Length,
                    wouldAddMustNotHit.Length,
                    wouldAddLifecycleRisk.Length,
                    riskIfNormalSelectedRelations.Length,
                    riskAfterSectionRoutingRelations.Length,
                    accepted.Values.Count(IsHistoricalAuditExpansion),
                    accepted.Values.Count(IsConflictEvidence),
                    accepted.Values.Count(relation => relation.RiskAfterSectionRouting)
                        + CountReason(blocked.Values, RelationExpansionValidationReasons.BlockedByWrongSectionRisk),
                    blockedReasons)
            });
        }

        return output;
    }

    public RelationExpansionShadowEvalReport BuildReport(
        int totalEvalSamples,
        bool includeSeedBatches,
        IReadOnlyList<RelationExpansionShadowSample> samples,
        IReadOnlyList<string>? warnings = null)
    {
        var profiles = samples
            .GroupBy(sample => sample.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildProfileSummary(group.Key, group.ToArray()))
            .OrderBy(summary => summary.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new RelationExpansionShadowEvalReport
        {
            CreatedAt = DateTimeOffset.UtcNow,
            IncludeSeedBatches = includeSeedBatches,
            TotalEvalSamples = totalEvalSamples,
            SampleCount = samples.Count,
            ProfileCount = profiles.Length,
            FormalOutputChanged = 0,
            SelectedSetChanged = 0,
            Profiles = profiles,
            Samples = samples
                .OrderBy(sample => sample.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(sample => sample.SampleId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Warnings = warnings?.ToArray() ?? Array.Empty<string>()
        };
    }

    public static string BuildMarkdownReport(
        RelationExpansionShadowEvalReport a3,
        RelationExpansionShadowEvalReport extended)
    {
        ArgumentNullException.ThrowIfNull(a3);
        ArgumentNullException.ThrowIfNull(extended);

        var lines = new List<string>
        {
            "# Relation Expansion Shadow Eval Report",
            string.Empty,
            $"Generated: {DateTimeOffset.UtcNow:O}",
            string.Empty
        };

        AppendReport(lines, "A3", a3);
        AppendReport(lines, "Extended", extended);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void AppendReport(
        List<string> lines,
        string title,
        RelationExpansionShadowEvalReport report)
    {
        lines.Add($"## {title} Summary");
        lines.Add(string.Empty);
        lines.Add($"- Eval samples: `{report.TotalEvalSamples}`");
        lines.Add($"- Profile/sample rows: `{report.SampleCount}`");
        lines.Add($"- Profiles: `{report.ProfileCount}`");
        lines.Add($"- Formal output changed: `{report.FormalOutputChanged}`");
        lines.Add($"- Selected set changed: `{report.SelectedSetChanged}`");
        lines.Add(string.Empty);
        lines.Add("| Profile | Samples | Accepted | Blocked | Would Add | MustHit Gain | MustNotHit Risk | Lifecycle Risk | Missing Evidence | Normal | Historical | Audit | Conflict | Diagnostics | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Recommendation |");
        lines.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var profile in report.Profiles)
        {
            lines.Add($"| {profile.ProfileId} | {profile.Samples} | {profile.AcceptedRelations} | {profile.BlockedRelations} | {profile.WouldAddCandidates} | {profile.MustHitGain} | {profile.MustNotHitRisk} | {profile.LifecycleRisk} | {profile.BlockedByMissingEvidence} | {profile.AcceptedToNormalContext} | {profile.AcceptedToHistoricalContext} | {profile.AcceptedToAuditContext} | {profile.AcceptedToConflictEvidence} | {profile.AcceptedToDiagnosticsOnly} | {profile.RiskIfNormalSelected} | {profile.RiskAfterSectionRouting} | {profile.HistoricalAuditExpansion} | {profile.ConflictEvidenceExpansion} | {profile.WrongSectionRisk} | {profile.Recommendation} |");
        }

        var notable = report.Samples
            .Where(sample => sample.WouldAddMustHit.Count > 0
                || sample.WouldAddMustNotHit.Count > 0
                || sample.WouldAddLifecycleRisk.Count > 0
                || sample.RiskIfNormalSelected > 0
                || sample.RiskAfterSectionRouting > 0
                || sample.WrongSectionRisk > 0
                || sample.BlockedReasons.Count > 0)
            .Take(40)
            .ToArray();
        lines.Add(string.Empty);
        lines.Add($"## {title} Notable Samples");
        lines.Add(string.Empty);
        lines.Add("| Sample | Mode | Intent | Profile | Seeds | Accepted | Blocked | MustHit Gain | MustNotHit Risk | Lifecycle Risk | Risk If Normal | Risk After Routing | Historical Audit | Conflict Evidence | Wrong Section | Top Block Reasons | Recommendation |");
        lines.Add("|---|---|---|---|---:|---:|---:|---|---|---|---:|---:|---:|---:|---:|---|---|");
        foreach (var sample in notable)
        {
            var reasons = sample.BlockedReasons.Count == 0
                ? "-"
                : string.Join("<br>", sample.BlockedReasons
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .Select(item => $"{item.Key}: {item.Value}"));
            lines.Add($"| {sample.SampleId} | {sample.Mode} | {sample.Intent} | {sample.ProfileId} | {sample.SeedItems.Count} | {sample.AcceptedRelations.Count} | {sample.BlockedRelations.Count} | {Ids(sample.WouldAddMustHit)} | {Ids(sample.WouldAddMustNotHit)} | {Ids(sample.WouldAddLifecycleRisk)} | {sample.RiskIfNormalSelected} | {sample.RiskAfterSectionRouting} | {sample.HistoricalAuditExpansion} | {sample.ConflictEvidenceExpansion} | {sample.WrongSectionRisk} | {reasons} | {sample.Recommendation} |");
        }

        lines.Add(string.Empty);
    }

    private static RelationExpansionShadowProfileSummary BuildProfileSummary(
        string profileId,
        IReadOnlyList<RelationExpansionShadowSample> samples)
    {
        var accepted = samples.Sum(sample => sample.AcceptedRelations.Count);
        var blocked = samples.Sum(sample => sample.BlockedRelations.Count);
        var wouldAdd = samples.Sum(sample => sample.WouldAddCandidates.Count);
        var mustHitGain = samples.Sum(sample => sample.WouldAddMustHit.Count);
        var mustNotHitRisk = samples.Sum(sample => sample.WouldAddMustNotHit.Count);
        var lifecycleRisk = samples.Sum(sample => sample.WouldAddLifecycleRisk.Count);
        var blockedByType = samples.Sum(sample =>
            GetReasonCount(sample, RelationExpansionValidationReasons.UnknownRelationType)
            + GetReasonCount(sample, RelationExpansionValidationReasons.BlockedRelationType)
            + GetReasonCount(sample, RelationExpansionValidationReasons.RelationTypeNotAllowed)
            + GetReasonCount(sample, RelationExpansionValidationReasons.AuditOnlyRelationInNormalProfile));
        var blockedByLifecycle = samples.Sum(sample => GetReasonCount(sample, RelationExpansionValidationReasons.InvalidLifecycle));
        var blockedByConfidence = samples.Sum(sample => GetReasonCount(sample, RelationExpansionValidationReasons.ConfidenceTooLow));
        var blockedByMissingEvidence = samples.Sum(sample => GetReasonCount(sample, RelationExpansionValidationReasons.MissingEvidence));
        var fanoutTrimmed = samples.Sum(sample => sample.FanoutTrimmed);
        var depthTrimmed = samples.Sum(sample => sample.DepthTrimmed);
        var blockedByBackwardReplacementTraversal = samples.Sum(sample => GetReasonCount(sample, RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked));
        var blockedByDeprecatedTarget = samples.Sum(sample => GetReasonCount(sample, RelationExpansionValidationReasons.DeprecatedTargetBlocked));
        var blockedByHistoricalTarget = samples.Sum(sample => GetReasonCount(sample, RelationExpansionValidationReasons.HistoricalTargetBlocked));
        var allowedTowardLatest = samples.Sum(sample => sample.AcceptedRelations.Count(IsTowardLatest));
        var blockedTowardHistorical = samples.Sum(sample => sample.BlockedRelations.Count(IsTowardHistorical));
        var historicalAllowedOnlyInAudit = samples.Sum(sample => sample.AcceptedRelations.Count(IsHistoricalAllowedOnlyInAudit));
        var acceptedToNormalContext = samples.Sum(sample => sample.AcceptedRelations.Count(IsNormalContext));
        var acceptedToHistoricalContext = samples.Sum(sample => sample.AcceptedRelations.Count(IsHistoricalContext));
        var acceptedToAuditContext = samples.Sum(sample => sample.AcceptedRelations.Count(IsAuditContext));
        var acceptedToConflictEvidence = samples.Sum(sample => sample.AcceptedRelations.Count(IsConflictEvidence));
        var acceptedToDiagnosticsOnly = samples.Sum(sample => sample.AcceptedRelations.Count(IsDiagnosticsOnly));
        var riskIfNormalSelected = samples.Sum(sample => sample.RiskIfNormalSelected);
        var riskAfterSectionRouting = samples.Sum(sample => sample.RiskAfterSectionRouting);
        var historicalAuditExpansion = samples.Sum(sample => sample.HistoricalAuditExpansion);
        var conflictEvidenceExpansion = samples.Sum(sample => sample.ConflictEvidenceExpansion);
        var wrongSectionRisk = samples.Sum(sample => sample.WrongSectionRisk);

        return new RelationExpansionShadowProfileSummary
        {
            ProfileId = profileId,
            Samples = samples.Count,
            AcceptedRelations = accepted,
            BlockedRelations = blocked,
            WouldAddCandidates = wouldAdd,
            MustHitGain = mustHitGain,
            MustNotHitRisk = mustNotHitRisk,
            LifecycleRisk = lifecycleRisk,
            BlockedByType = blockedByType,
            BlockedByLifecycle = blockedByLifecycle,
            BlockedByConfidence = blockedByConfidence,
            BlockedByMissingEvidence = blockedByMissingEvidence,
            FanoutTrimmed = fanoutTrimmed,
            DepthTrimmed = depthTrimmed,
            BlockedByBackwardReplacementTraversal = blockedByBackwardReplacementTraversal,
            BlockedByDeprecatedTarget = blockedByDeprecatedTarget,
            BlockedByHistoricalTarget = blockedByHistoricalTarget,
            AllowedTowardLatest = allowedTowardLatest,
            BlockedTowardHistorical = blockedTowardHistorical,
            HistoricalAllowedOnlyInAudit = historicalAllowedOnlyInAudit,
            AcceptedToNormalContext = acceptedToNormalContext,
            AcceptedToHistoricalContext = acceptedToHistoricalContext,
            AcceptedToAuditContext = acceptedToAuditContext,
            AcceptedToConflictEvidence = acceptedToConflictEvidence,
            AcceptedToDiagnosticsOnly = acceptedToDiagnosticsOnly,
            RiskIfNormalSelected = riskIfNormalSelected,
            RiskAfterSectionRouting = riskAfterSectionRouting,
            HistoricalAuditExpansion = historicalAuditExpansion,
            ConflictEvidenceExpansion = conflictEvidenceExpansion,
            WrongSectionRisk = wrongSectionRisk,
            Recommendation = RecommendProfile(
                accepted,
                blocked,
                wouldAdd,
                mustHitGain,
                mustNotHitRisk,
                lifecycleRisk,
                riskIfNormalSelected,
                riskAfterSectionRouting,
                historicalAuditExpansion,
                conflictEvidenceExpansion,
                wrongSectionRisk,
                blockedByType,
                blockedByMissingEvidence,
                blockedByBackwardReplacementTraversal,
                blockedByDeprecatedTarget,
                blockedByHistoricalTarget)
        };
    }

    private string ResolveIntent(ContextEvalSample sample)
    {
        if (sample.Metadata.TryGetValue("intent", out var intent)
            && !string.IsNullOrWhiteSpace(intent))
        {
            return intent;
        }

        var snapshot = new ContextPlanningSnapshot
        {
            WorkspaceId = "eval",
            CollectionId = "test"
        };
        return _intentDetector.Detect(snapshot, sample.Query, sample.Mode).Intent;
    }

    private static async Task<IReadOnlyList<ContextEvalSample>> LoadCategorySamplesAsync(
        string categoryDir,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        if (includeSeedBatches)
        {
            var result = await new ContextEvalSampleLoader().LoadAsync(categoryDir, cancellationToken)
                .ConfigureAwait(false);
            return result.Samples;
        }

        var samplesPath = Path.Combine(categoryDir, "seed_samples.json");
        if (!File.Exists(samplesPath))
        {
            return [];
        }

        return JsonSerializer.Deserialize<IReadOnlyList<ContextEvalSample>>(
            await File.ReadAllTextAsync(samplesPath, cancellationToken).ConfigureAwait(false),
            JsonOptions) ?? [];
    }

    private static async Task<ContextEvalCorpus> LoadCategoryCorpusAsync(
        string categoryDir,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        var memories = new Dictionary<string, ContextMemoryItem>(StringComparer.OrdinalIgnoreCase);
        var relations = new Dictionary<string, ContextRelation>(StringComparer.OrdinalIgnoreCase);
        var constraints = new Dictionary<string, ContextConstraint>(StringComparer.OrdinalIgnoreCase);
        var corpusFiles = includeSeedBatches
            ? Directory.EnumerateFiles(categoryDir, "corpus*.json", SearchOption.TopDirectoryOnly)
            : File.Exists(Path.Combine(categoryDir, "corpus.json"))
                ? [Path.Combine(categoryDir, "corpus.json")]
                : Enumerable.Empty<string>();

        foreach (var file in corpusFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var corpus = JsonSerializer.Deserialize<ContextEvalCorpus>(
                await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false),
                JsonOptions) ?? new ContextEvalCorpus();
            foreach (var item in corpus.Contexts)
            {
                contexts[item.Id] = item;
            }

            foreach (var item in corpus.Memories)
            {
                memories[item.Id] = item;
            }

            foreach (var item in corpus.Relations)
            {
                relations[item.Id] = item;
            }

            foreach (var item in corpus.Constraints)
            {
                constraints[item.Id] = item;
            }
        }

        return new ContextEvalCorpus
        {
            Contexts = contexts.Values.ToArray(),
            Memories = memories.Values.ToArray(),
            Relations = relations.Values.ToArray(),
            Constraints = constraints.Values.ToArray()
        };
    }

    private async Task SeedRelationsAsync(
        IRelationStore relationStore,
        IReadOnlyList<ContextRelation> relations,
        string workspaceId,
        string collectionId,
        IReadOnlyDictionary<string, string> lifecycleByItemId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var relation in relations)
        {
            var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase);
            if (lifecycleByItemId.TryGetValue(relation.TargetId, out var targetLifecycle))
            {
                metadata["targetLifecycle"] = targetLifecycle;
                metadata["targetExists"] = "true";
            }
            else
            {
                metadata["targetExists"] = "false";
            }

            var seededRelation = _typeNormalizer.NormalizeAndBackfillFixtureRelation(new ContextRelation
            {
                Id = relation.Id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SourceId = relation.SourceId,
                TargetId = relation.TargetId,
                RelationType = relation.RelationType,
                Weight = relation.Weight,
                Confidence = relation.Confidence,
                SourceRefs = relation.SourceRefs.ToArray(),
                Metadata = metadata,
                CreatedAt = relation.CreatedAt == default ? now : relation.CreatedAt
            }, $"relation-expansion-shadow-eval:{workspaceId}");

            await relationStore.SaveAsync(seededRelation, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyDictionary<string, string> BuildLifecycleIndex(ContextEvalCorpus corpus)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in corpus.Contexts)
        {
            result[context.Id] = ResolveLifecycle(context.Metadata, "Active");
        }

        foreach (var memory in corpus.Memories)
        {
            result[memory.Id] = ResolveLifecycle(memory.Metadata, memory.Status.ToString());
        }

        foreach (var constraint in corpus.Constraints)
        {
            result[constraint.Id] = ResolveLifecycle(constraint.Metadata, constraint.Status.ToString());
        }

        return result;
    }

    private static IReadOnlyList<string> ResolveSeedItems(ContextEvalResult result)
    {
        if (result.SelectedIds.Count > 0)
        {
            return result.SelectedIds
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return result.SelectedItemDiagnostics
            .Select(item => item.ItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, int> CountBlockedReasons(IEnumerable<RelationExpansionPreviewRelation> relations)
    {
        return relations
            .SelectMany(relation => relation.Reasons)
            .GroupBy(reason => reason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static int CountReason(IEnumerable<RelationExpansionPreviewRelation> relations, string reason)
    {
        return relations.Count(relation => relation.Reasons.Any(item => string.Equals(item, reason, StringComparison.OrdinalIgnoreCase)));
    }

    private static int GetReasonCount(RelationExpansionShadowSample sample, string reason)
    {
        return sample.BlockedReasons.TryGetValue(reason, out var count) ? count : 0;
    }

    private static string RecommendSample(
        int accepted,
        int blocked,
        int wouldAdd,
        int mustHitGain,
        int mustNotHitRisk,
        int lifecycleRisk,
        int riskIfNormalSelected,
        int riskAfterSectionRouting,
        int historicalAuditExpansion,
        int conflictEvidenceExpansion,
        int wrongSectionRisk,
        IReadOnlyDictionary<string, int> blockedReasons)
    {
        if (wrongSectionRisk > 0)
        {
            return RelationExpansionShadowRecommendations.BlockedByWrongSectionRisk;
        }

        if (mustNotHitRisk > 0 || lifecycleRisk > 0)
        {
            return RelationExpansionShadowRecommendations.BlockedByRisk;
        }

        if (accepted == 0 && blocked == 0)
        {
            return RelationExpansionShadowRecommendations.NeedsMoreRelations;
        }

        if (mustHitGain > 0 && wouldAdd > 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForShadowInRetrieval;
        }

        if (conflictEvidenceExpansion > 0 && riskAfterSectionRouting == 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForConflictShadow;
        }

        if (historicalAuditExpansion > 0 && riskAfterSectionRouting == 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForAuditShadow;
        }

        if (accepted > 0 && riskIfNormalSelected > 0 && riskAfterSectionRouting == 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForSectionAwareShadow;
        }

        if (blocked > accepted)
        {
            if (HasOnlySafeTraversalBlocks(blockedReasons))
            {
                return RelationExpansionShadowRecommendations.KeepPreviewOnly;
            }

            return RelationExpansionShadowRecommendations.NeedsPolicyTuning;
        }

        return RelationExpansionShadowRecommendations.KeepPreviewOnly;
    }

    private static string RecommendProfile(
        int accepted,
        int blocked,
        int wouldAdd,
        int mustHitGain,
        int mustNotHitRisk,
        int lifecycleRisk,
        int riskIfNormalSelected,
        int riskAfterSectionRouting,
        int historicalAuditExpansion,
        int conflictEvidenceExpansion,
        int wrongSectionRisk,
        int blockedByType,
        int blockedByMissingEvidence,
        int blockedByBackwardReplacementTraversal,
        int blockedByDeprecatedTarget,
        int blockedByHistoricalTarget)
    {
        if (wrongSectionRisk > 0)
        {
            return RelationExpansionShadowRecommendations.BlockedByWrongSectionRisk;
        }

        if (mustNotHitRisk > 0 || lifecycleRisk > 0)
        {
            return RelationExpansionShadowRecommendations.BlockedByRisk;
        }

        if (accepted == 0 && blocked == 0)
        {
            return RelationExpansionShadowRecommendations.NeedsMoreRelations;
        }

        if (mustHitGain > 0 && wouldAdd > 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForShadowInRetrieval;
        }

        if (conflictEvidenceExpansion > 0 && riskAfterSectionRouting == 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForConflictShadow;
        }

        if (historicalAuditExpansion > 0 && riskAfterSectionRouting == 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForAuditShadow;
        }

        if (accepted > 0 && riskIfNormalSelected > 0 && riskAfterSectionRouting == 0)
        {
            return RelationExpansionShadowRecommendations.ReadyForSectionAwareShadow;
        }

        if (blocked > accepted || blockedByType > 0 || blockedByMissingEvidence > 0)
        {
            if (blockedByType == 0
                && blockedByMissingEvidence == 0
                && blocked > 0
                && blocked <= blockedByBackwardReplacementTraversal + blockedByDeprecatedTarget + blockedByHistoricalTarget)
            {
                return RelationExpansionShadowRecommendations.KeepPreviewOnly;
            }

            return RelationExpansionShadowRecommendations.NeedsPolicyTuning;
        }

        return RelationExpansionShadowRecommendations.KeepPreviewOnly;
    }

    private static bool HasOnlySafeTraversalBlocks(IReadOnlyDictionary<string, int> blockedReasons)
    {
        if (blockedReasons.Count == 0)
        {
            return false;
        }

        return blockedReasons.All(reason =>
            string.Equals(reason.Key, RelationExpansionValidationReasons.BackwardReplacementTraversalBlocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason.Key, RelationExpansionValidationReasons.DeprecatedTargetBlocked, StringComparison.OrdinalIgnoreCase)
            || string.Equals(reason.Key, RelationExpansionValidationReasons.HistoricalTargetBlocked, StringComparison.OrdinalIgnoreCase));
    }

    private static bool EvalIdMatches(string expected, string candidateId)
    {
        return string.Equals(expected, candidateId, StringComparison.OrdinalIgnoreCase)
            || candidateId.EndsWith(expected, StringComparison.OrdinalIgnoreCase)
            || expected.EndsWith(candidateId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLifecycleRisk(string? lifecycle)
    {
        return string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, "Rejected", StringComparison.OrdinalIgnoreCase)
            || string.Equals(lifecycle, "Superseded", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRiskIfNormalSelected(
        ContextEvalSample sample,
        RelationExpansionPreviewRelation relation,
        IReadOnlyDictionary<string, string> lifecycleByItemId)
    {
        return relation.RiskIfNormalSelected
            || sample.MustNotHit.Any(expected => EvalIdMatches(expected, relation.TargetId))
            || IsLifecycleRisk(ResolveTargetLifecycle(relation, lifecycleByItemId));
    }

    private static bool IsRiskAfterSectionRouting(RelationExpansionPreviewRelation relation)
    {
        return relation.RiskAfterSectionRouting
            || string.Equals(relation.TargetSection, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveTargetLifecycle(
        RelationExpansionPreviewRelation relation,
        IReadOnlyDictionary<string, string> lifecycleByItemId)
    {
        if (!string.IsNullOrWhiteSpace(relation.TargetLifecycle))
        {
            return relation.TargetLifecycle;
        }

        return lifecycleByItemId.GetValueOrDefault(relation.TargetId) ?? StableMemoryLifecycle.Active;
    }

    private static bool IsTowardLatest(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TraversalDirection, RelationTraversalDirections.TowardLatest, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTowardHistorical(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TraversalDirection, RelationTraversalDirections.TowardHistorical, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalAllowedOnlyInAudit(RelationExpansionPreviewRelation relation)
    {
        return relation.Warnings.Contains(RelationExpansionValidationReasons.HistoricalAllowedOnlyInAudit, StringComparer.OrdinalIgnoreCase)
            || string.Equals(relation.TargetSection, RelationExpansionTargetSections.AuditHistorical, StringComparison.OrdinalIgnoreCase)
            && IsLifecycleRisk(relation.TargetLifecycle);
    }

    private static bool IsHistoricalAuditExpansion(RelationExpansionPreviewRelation relation)
    {
        return (IsLifecycleRisk(relation.TargetLifecycle) || relation.RiskIfNormalSelected)
            && (IsAuditContext(relation) || IsHistoricalContext(relation));
    }

    private static bool IsNormalContext(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHistoricalContext(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuditContext(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConflictEvidence(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiagnosticsOnly(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.DiagnosticsOnly, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveLifecycle(IReadOnlyDictionary<string, string> metadata, string fallback)
    {
        foreach (var key in new[] { "lifecycle", "status" })
        {
            if (metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string Ids(IReadOnlyList<string> ids)
    {
        return ids.Count == 0 ? "-" : string.Join("<br>", ids.Take(5));
    }
}
