using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Retrieval Dataset V2 离线生成器。当前实现为确定性模板生成，可作为 LLM 输出落盘前后的 contract 验证基线。
/// </summary>
public sealed class RetrievalDatasetV2Generator
{
    private static readonly DateTimeOffset StableBaseTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly string[] Difficulties =
    [
        "direct_lexical",
        "paraphrase_semantic",
        "metadata_anchor",
        "relation_multi_hop",
        "lifecycle_deprecated_trap",
        "must_not_negative_constraint",
        "ambiguous_query_requiring_target_section"
    ];

    public RetrievalDatasetV2GeneratedDataset Generate(RetrievalDatasetV2GenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            return new RetrievalDatasetV2GeneratedDataset();
        }

        var corpusCount = Math.Max(8, options.TargetCorpusItemCount);
        var sampleCount = Math.Max(4, options.TargetSampleCount);
        var corpus = new List<RetrievalDatasetV2CorpusItem>(corpusCount);
        for (var i = 0; i < corpusCount; i++)
        {
            corpus.Add(BuildCorpusItem(options, i));
        }

        var relationLookup = BuildRelations(corpus);
        for (var i = 0; i < corpus.Count; i++)
        {
            var item = corpus[i];
            corpus[i] = WithRelations(item, relationLookup.GetValueOrDefault(item.ItemId) ?? Array.Empty<RetrievalDatasetV2Relation>());
        }

        var samples = new List<RetrievalDatasetV2Sample>(sampleCount);
        for (var i = 0; i < sampleCount; i++)
        {
            samples.Add(BuildSample(options, corpus, i));
        }

        return new RetrievalDatasetV2GeneratedDataset
        {
            CorpusItems = corpus,
            Samples = samples
        };
    }

    public RetrievalDatasetV2GenerationReport BuildGenerationReport(
        RetrievalDatasetV2GenerationOptions options,
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation,
        int judgeWarningCount)
    {
        return new RetrievalDatasetV2GenerationReport
        {
            OperationId = $"retrieval-dataset-v2-generate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            Options = options,
            CorpusItemCount = dataset.CorpusItems.Count,
            SampleCount = dataset.Samples.Count,
            DifficultyBreakdown = CountBy(dataset.Samples.Select(static sample => sample.Difficulty)),
            SplitBreakdown = CountBy(dataset.Samples.Select(static sample => sample.Split)),
            ValidationIssueCount = validation.IssueCount,
            MissingEvidenceCount = validation.MissingEvidenceRefsCount,
            MissingProvenanceCount = validation.MissingProvenanceCount,
            MustHitMissingCount = validation.MustHitMissingFromCorpusCount,
            MustNotOverlapCount = validation.MustHitMustNotOverlapCount,
            ItemIdLeakageCount = validation.QueryItemIdLeakCount,
            RelationInconsistencyCount = validation.RelationEvidenceMissingCount,
            JudgeWarningCount = judgeWarningCount,
            FormalRetrievalAllowed = false,
            UseForRuntime = options.UseForRuntime,
            Recommendation = ResolveRecommendation(validation, judgeWarningCount, options),
            PromptTemplates = BuildPromptTemplates()
        };
    }

    public RetrievalDatasetV2QualityReport BuildQualityReport(
        RetrievalDatasetV2GeneratedDataset dataset,
        RetrievalDatasetV2ValidationReport validation,
        int judgeWarningCount)
    {
        return new RetrievalDatasetV2QualityReport
        {
            OperationId = $"retrieval-dataset-v2-quality-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            CorpusItemCount = dataset.CorpusItems.Count,
            SampleCount = dataset.Samples.Count,
            DifficultyBreakdown = CountBy(dataset.Samples.Select(static sample => sample.Difficulty)),
            SplitBreakdown = CountBy(dataset.Samples.Select(static sample => sample.Split)),
            ValidationIssueCount = validation.IssueCount,
            MissingEvidenceCount = validation.MissingEvidenceRefsCount,
            MissingProvenanceCount = validation.MissingProvenanceCount,
            MustHitMissingCount = validation.MustHitMissingFromCorpusCount,
            MustNotOverlapCount = validation.MustHitMustNotOverlapCount,
            ItemIdLeakageCount = validation.QueryItemIdLeakCount,
            RelationInconsistencyCount = validation.RelationEvidenceMissingCount,
            JudgeWarningCount = judgeWarningCount,
            FormalRetrievalAllowed = false,
            UseForRuntime = false,
            Recommendation = ResolveRecommendation(validation, judgeWarningCount, new RetrievalDatasetV2GenerationOptions { Enabled = dataset.CorpusItems.Count > 0 })
        };
    }

    public RetrievalDatasetV2ValidationReport Validate(RetrievalDatasetV2GeneratedDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        return new RetrievalDatasetV2MetadataContractRunner().Validate(
            ToSourceItems(dataset.CorpusItems),
            ToSamples(dataset.Samples),
            ToRelations(dataset.CorpusItems));
    }

    public int Judge(RetrievalDatasetV2GeneratedDataset dataset)
    {
        var warnings = 0;
        var corpusById = dataset.CorpusItems.ToDictionary(static item => item.ItemId, StringComparer.OrdinalIgnoreCase);
        foreach (var sample in dataset.Samples)
        {
            if (sample.QueryText.Length < 12 || sample.Rationale.Length < 20)
            {
                warnings++;
            }

            foreach (var itemId in sample.MustHitItemIds.Concat(sample.MustNotHitItemIds))
            {
                if (!corpusById.ContainsKey(itemId))
                {
                    warnings++;
                }
            }
        }

        return warnings;
    }

    public static IReadOnlyList<VectorReindexSourceItem> ToSourceItems(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        return corpus.Select(static item =>
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["sourceKind"] = item.SourceKind,
                ["sourceRefs"] = string.Join('|', item.SourceRefs),
                ["evidenceRefs"] = string.Join('|', item.EvidenceRefs),
                ["provenanceRecordId"] = item.Provenance.RecordId,
                ["sourceFingerprint"] = item.SourceFingerprint,
                ["lifecycle"] = item.Lifecycle,
                ["reviewStatus"] = item.ReviewStatus,
                ["replacementState"] = item.ReplacementState,
                ["targetSection"] = item.TargetSection,
                ["split"] = item.Split,
                ["sourceTags"] = string.Join(',', item.Tags),
                ["anchors"] = string.Join(',', item.Anchors)
            };
            foreach (var pair in item.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            return new VectorReindexSourceItem
            {
                ItemId = item.ItemId,
                ItemKind = item.ItemKind,
                Layer = item.Layer,
                Text = item.Content,
                UpdatedAt = item.CreatedAt,
                Metadata = metadata
            };
        }).ToArray();
    }

    public static IReadOnlyList<ContextEvalSample> ToSamples(IReadOnlyList<RetrievalDatasetV2Sample> samples)
    {
        return samples.Select(static sample =>
        {
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["split"] = sample.Split,
                ["sourceRefs"] = string.Join('|', sample.SourceRefs),
                ["evidenceRefs"] = string.Join('|', sample.EvidenceRefs),
                ["provenanceRecordId"] = sample.Provenance.RecordId,
                ["intent"] = sample.Intent,
                ["taskKind"] = sample.TaskKind,
                ["difficulty"] = sample.Difficulty,
                ["expectedTargetSection"] = sample.ExpectedTargetSection
            };
            foreach (var pair in sample.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }

            return new ContextEvalSample
            {
                Id = sample.SampleId,
                Query = sample.QueryText,
                Mode = sample.Intent,
                MustHit = sample.MustHitItemIds,
                MustNotHit = sample.MustNotHitItemIds,
                GoldenNotes = sample.Rationale,
                Metadata = metadata
            };
        }).ToArray();
    }

    public static IReadOnlyList<ContextRelation> ToRelations(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        return corpus
            .SelectMany(static item => item.Relations)
            .GroupBy(static relation => relation.RelationId, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Select(static relation =>
            {
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["evidenceRefs"] = string.Join('|', relation.EvidenceRefs),
                    ["sourceRefs"] = string.Join('|', relation.SourceRefs)
                };
                return new ContextRelation
                {
                    Id = relation.RelationId,
                    SourceId = relation.SourceItemId,
                    TargetId = relation.TargetItemId,
                    RelationType = relation.RelationType,
                    SourceRefs = relation.SourceRefs,
                    Metadata = metadata,
                    Confidence = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                };
            }).ToArray();
    }

    public static string BuildGenerationMarkdown(RetrievalDatasetV2GenerationReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Generation Report");
        AppendCommon(builder, report.CorpusItemCount, report.SampleCount, report.DifficultyBreakdown, report.SplitBreakdown, report.ValidationIssueCount, report.MissingEvidenceCount, report.MissingProvenanceCount, report.MustHitMissingCount, report.MustNotOverlapCount, report.ItemIdLeakageCount, report.RelationInconsistencyCount, report.JudgeWarningCount, report.FormalRetrievalAllowed, report.UseForRuntime, report.Recommendation);
        builder.AppendLine();
        builder.AppendLine("## Prompt Templates");
        foreach (var template in report.PromptTemplates)
        {
            builder.AppendLine($"- {template}");
        }

        return builder.ToString();
    }

    public static string BuildQualityMarkdown(RetrievalDatasetV2QualityReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Retrieval Dataset V2 Quality Report");
        AppendCommon(builder, report.CorpusItemCount, report.SampleCount, report.DifficultyBreakdown, report.SplitBreakdown, report.ValidationIssueCount, report.MissingEvidenceCount, report.MissingProvenanceCount, report.MustHitMissingCount, report.MustNotOverlapCount, report.ItemIdLeakageCount, report.RelationInconsistencyCount, report.JudgeWarningCount, report.FormalRetrievalAllowed, report.UseForRuntime, report.Recommendation);
        return builder.ToString();
    }

    private static RetrievalDatasetV2CorpusItem BuildCorpusItem(RetrievalDatasetV2GenerationOptions options, int index)
    {
        var split = SplitFor(index);
        var itemId = $"rdsv2-{split}-item-{index + 1:0000}";
        var topic = TopicFor(index);
        var lifecycleTrap = index % 7 == 4;
        var lifecycle = lifecycleTrap ? "Deprecated" : index % 5 == 0 ? "Current" : "Stable";
        var targetSection = lifecycleTrap ? VectorQueryTargetSections.HistoricalContext : VectorQueryTargetSections.NormalContext;
        var replacementState = lifecycleTrap ? "superseded" : "current";
        var sourceRef = $"src-{split}-{index + 1:0000}";
        var evidenceRef = $"ev-{split}-{index + 1:0000}";
        var provenance = BuildProvenance(options, itemId, index);
        var uniqueAnchor = $"rdsv2-anchor-{split}-{index + 1:0000}";
        var tags = topic.Tags.Concat([split, uniqueAnchor]).ToArray();
        var anchors = topic.Anchors.Concat([uniqueAnchor]).ToArray();
        var content = $"{topic.Title} guidance. {topic.Content} Metadata anchor {uniqueAnchor}. Evidence is captured from controlled source {sourceRef} with lifecycle {lifecycle} and target section {targetSection}.";

        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = itemId,
            ItemKind = topic.ItemKind,
            SourceKind = topic.SourceKind,
            Layer = "context",
            Lifecycle = lifecycle,
            ReviewStatus = lifecycleTrap ? "DeprecatedReviewed" : "Approved",
            ReplacementState = replacementState,
            TargetSection = targetSection,
            SourceRefs = [sourceRef],
            EvidenceRefs = [evidenceRef],
            Provenance = provenance,
            SourceFingerprint = provenance.SourceFingerprint,
            CreatedAt = StableCreatedAt(options, index),
            Tags = tags,
            Anchors = anchors,
            Content = content,
            Split = split,
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "retrieval-dataset-v2-generator/v1",
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false"
            }
        };
    }

    private static RetrievalDatasetV2CorpusItem WithRelations(RetrievalDatasetV2CorpusItem item, IReadOnlyList<RetrievalDatasetV2Relation> relations)
    {
        return new RetrievalDatasetV2CorpusItem
        {
            ItemId = item.ItemId,
            ItemKind = item.ItemKind,
            SourceKind = item.SourceKind,
            Layer = item.Layer,
            Lifecycle = item.Lifecycle,
            ReviewStatus = item.ReviewStatus,
            ReplacementState = item.ReplacementState,
            TargetSection = item.TargetSection,
            SourceRefs = item.SourceRefs,
            EvidenceRefs = item.EvidenceRefs,
            Provenance = item.Provenance,
            SourceFingerprint = item.SourceFingerprint,
            CreatedAt = item.CreatedAt,
            Relations = relations,
            Tags = item.Tags,
            Anchors = item.Anchors,
            Content = item.Content,
            Split = item.Split,
            Metadata = item.Metadata
        };
    }

    private static RetrievalDatasetV2Sample BuildSample(
        RetrievalDatasetV2GenerationOptions options,
        IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus,
        int index)
    {
        var split = SplitFor(index);
        var splitCorpus = corpus.Where(item => string.Equals(item.Split, split, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (splitCorpus.Length < 2)
        {
            splitCorpus = corpus.ToArray();
        }

        var difficulty = Difficulties[index % Difficulties.Length];
        var mustHit = SelectMustHit(splitCorpus, difficulty, index);
        var mustNot = SelectMustNot(splitCorpus, mustHit.ItemId, index);
        var sampleId = $"rdsv2-{split}-sample-{index + 1:0000}";
        var query = QueryFor(difficulty, mustHit, mustNot);
        var sourceRef = $"sample-src-{split}-{index + 1:0000}";
        var evidenceRef = $"sample-ev-{split}-{index + 1:0000}";

        return new RetrievalDatasetV2Sample
        {
            SampleId = sampleId,
            TaskKind = "retrieval",
            Intent = difficulty.Contains("lifecycle", StringComparison.OrdinalIgnoreCase) ? "AuditRetrieval" : "ContextRetrieval",
            QueryText = query,
            Difficulty = difficulty,
            ExpectedTargetSection = mustHit.TargetSection,
            MustHitItemIds = [mustHit.ItemId],
            MustNotHitItemIds = [mustNot.ItemId],
            Rationale = $"The expected item matches the requested topic, lifecycle, and target section; the negative item is a distractor with different anchors or lifecycle.",
            NegativeDistractorIds = [mustNot.ItemId],
            RequiredRelations = mustHit.Relations.Select(static relation => relation.RelationId).ToArray(),
            ExpectedLifecycleBehavior = mustHit.TargetSection == VectorQueryTargetSections.NormalContext
                ? "active_or_stable_only"
                : "route_to_non_normal_context",
            Split = split,
            SourceRefs = [sourceRef],
            EvidenceRefs = [evidenceRef],
            Provenance = BuildProvenance(options, sampleId, index + 10000),
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["generatedBy"] = "retrieval-dataset-v2-generator/v1",
                ["useForRuntime"] = "false",
                ["rationaleIndexed"] = "false"
            }
        };
    }

    private static Dictionary<string, IReadOnlyList<RetrievalDatasetV2Relation>> BuildRelations(IReadOnlyList<RetrievalDatasetV2CorpusItem> corpus)
    {
        var result = new Dictionary<string, List<RetrievalDatasetV2Relation>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < corpus.Count; i++)
        {
            var item = corpus[i];
            if (!string.Equals(item.ReplacementState, "superseded", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var replacement = corpus.FirstOrDefault(candidate =>
                string.Equals(candidate.Split, item.Split, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase));
            if (replacement is null)
            {
                continue;
            }

            var relationId = $"rdsv2-rel-{i + 1:0000}";
            var relation = new RetrievalDatasetV2Relation
            {
                RelationId = relationId,
                SourceItemId = item.ItemId,
                TargetItemId = replacement.ItemId,
                RelationType = "superseded_by",
                SourceRefs = item.SourceRefs.Concat(replacement.SourceRefs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                EvidenceRefs = item.EvidenceRefs.Concat(replacement.EvidenceRefs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
            AddRelation(result, item.ItemId, relation);
            AddRelation(result, replacement.ItemId, relation);
        }

        return result.ToDictionary(static item => item.Key, static item => (IReadOnlyList<RetrievalDatasetV2Relation>)item.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddRelation(Dictionary<string, List<RetrievalDatasetV2Relation>> values, string itemId, RetrievalDatasetV2Relation relation)
    {
        if (!values.TryGetValue(itemId, out var list))
        {
            list = [];
            values[itemId] = list;
        }

        list.Add(relation);
    }

    private static RetrievalDatasetV2CorpusItem SelectMustHit(IReadOnlyList<RetrievalDatasetV2CorpusItem> splitCorpus, string difficulty, int index)
    {
        if (difficulty.Contains("lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            var lifecycleItem = splitCorpus.FirstOrDefault(static item => !string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase));
            if (lifecycleItem is not null)
            {
                return lifecycleItem;
            }
        }

        return splitCorpus[index % splitCorpus.Count];
    }

    private static RetrievalDatasetV2CorpusItem SelectMustNot(IReadOnlyList<RetrievalDatasetV2CorpusItem> splitCorpus, string mustHitId, int index)
    {
        return splitCorpus.First(item => !string.Equals(item.ItemId, mustHitId, StringComparison.OrdinalIgnoreCase)
                                         && (index % 2 == 0 || string.Equals(item.TargetSection, VectorQueryTargetSections.NormalContext, StringComparison.OrdinalIgnoreCase)));
    }

    private static string QueryFor(string difficulty, RetrievalDatasetV2CorpusItem mustHit, RetrievalDatasetV2CorpusItem mustNot)
    {
        var anchor = mustHit.Anchors.LastOrDefault() ?? mustHit.Anchors.FirstOrDefault() ?? "context";
        var negativeAnchor = mustNot.Anchors.LastOrDefault() ?? mustNot.Anchors.FirstOrDefault() ?? "other context";
        return difficulty switch
        {
            "direct_lexical" => $"Find the current guidance with metadata anchor {anchor} and summarize the approved context; avoid metadata anchor {negativeAnchor}.",
            "paraphrase_semantic" => $"Which approved note explains how to handle the practice linked to metadata anchor {anchor}; avoid metadata anchor {negativeAnchor}.",
            "metadata_anchor" => $"Retrieve the context tagged with metadata anchor {anchor} for the requested target section; avoid metadata anchor {negativeAnchor}.",
            "relation_multi_hop" => $"Use replacement evidence to identify the context linked to metadata anchor {anchor}; avoid metadata anchor {negativeAnchor}.",
            "lifecycle_deprecated_trap" => $"Show the historical or audit context for metadata anchor {anchor} without using it as normal guidance; avoid metadata anchor {negativeAnchor}.",
            "must_not_negative_constraint" => $"Find the relevant approved guidance for metadata anchor {anchor} while excluding distractors about metadata anchor {negativeAnchor}.",
            _ => $"Resolve the ambiguous request into the correct target section for metadata anchor {anchor}; avoid metadata anchor {negativeAnchor}."
        };
    }

    private static string SplitFor(int index)
    {
        return (index % 10) switch
        {
            0 or 1 or 2 or 3 or 4 or 5 => "train",
            6 or 7 => "dev",
            _ => "test"
        };
    }

    private static RetrievalDatasetV2Provenance BuildProvenance(RetrievalDatasetV2GenerationOptions options, string id, int index)
    {
        var fingerprint = Fingerprint($"{options.Seed}|{options.WorkspaceId}|{options.CollectionId}|{id}|{index}");
        return new RetrievalDatasetV2Provenance
        {
            RecordId = $"prov-{fingerprint[..16]}",
            SourceFingerprint = fingerprint,
            IngestionBatchId = $"rdsv2-{options.Seed}",
            CreatedAt = StableCreatedAt(options, index)
        };
    }

    private static DateTimeOffset StableCreatedAt(RetrievalDatasetV2GenerationOptions options, int index)
    {
        var offset = Math.Abs(options.Seed % 100_000) + Math.Max(0, index);
        return StableBaseTime.AddMinutes(offset);
    }

    private static (string Title, string Content, string ItemKind, string SourceKind, string[] Tags, string[] Anchors) TopicFor(int index)
    {
        var topic = index % 8;
        return topic switch
        {
            0 => ("Recovery checkpoint", "The latest checkpoint must be preferred over stale recovery notes.", "runbook", "operational-note", ["recovery", "checkpoint"], ["checkpoint"]),
            1 => ("Schema migration guard", "Schema changes require preview, explicit confirmation, and rollback notes.", "policy", "engineering-note", ["schema", "migration"], ["migration"]),
            2 => ("Tool timeout handling", "Timeouts should record retry state and avoid duplicate execution.", "diagnostic", "operational-note", ["timeout", "retry"], ["timeout"]),
            3 => ("Review evidence rule", "Lifecycle repair requires provenance, source references, and evidence references.", "policy", "governance-note", ["evidence", "review"], ["evidence"]),
            4 => ("Historical design note", "Older design notes may be useful only in audit or historical context.", "historical-note", "archive-note", ["historical", "design"], ["historical"]),
            5 => ("Scope isolation note", "Workspace and collection scope must be preserved in retrieval samples.", "policy", "governance-note", ["scope", "isolation"], ["scope"]),
            6 => ("Negative constraint note", "Negative distractors must be represented with explicit must-not labels.", "test-note", "evaluation-note", ["negative", "constraint"], ["constraint"]),
            _ => ("Target section routing", "Ambiguous queries should route to normal, audit, historical, or diagnostics sections based on metadata.", "routing-note", "governance-note", ["routing", "section"], ["section"])
        };
    }

    private static IReadOnlyList<string> BuildPromptTemplates()
    {
        return
        [
            "Generate corpus items with sourceRefs, evidenceRefs, provenance, lifecycle, reviewStatus, replacementState, targetSection, relations, tags, anchors, and content.",
            "Generate retrieval samples whose queryText never contains itemId values; rationale must not be indexed text.",
            "Choose mustHit and mustNot only from the generated corpus and explain why the positive is correct and the negative is wrong.",
            "Lifecycle traps require relation evidence and must route deprecated or superseded items away from normal_context."
        ];
    }

    private static string ResolveRecommendation(
        RetrievalDatasetV2ValidationReport validation,
        int judgeWarningCount,
        RetrievalDatasetV2GenerationOptions options)
    {
        if (!options.Enabled)
        {
            return RetrievalDatasetV2GenerationRecommendations.NotConfigured;
        }

        if (validation.QueryItemIdLeakCount > 0)
        {
            return RetrievalDatasetV2GenerationRecommendations.BlockedByLeakage;
        }

        if (validation.MissingEvidenceRefsCount > 0 || validation.MissingProvenanceCount > 0)
        {
            return RetrievalDatasetV2GenerationRecommendations.BlockedByMissingEvidence;
        }

        if (validation.IssueCount > 0)
        {
            return RetrievalDatasetV2GenerationRecommendations.BlockedByValidationIssues;
        }

        if (judgeWarningCount > 0)
        {
            return RetrievalDatasetV2GenerationRecommendations.NeedsGenerationRepair;
        }

        return RetrievalDatasetV2GenerationRecommendations.ReadyForDatasetV2ShadowEval;
    }

    private static Dictionary<string, int> CountBy(IEnumerable<string> values)
    {
        return values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);
    }

    private static void AppendCommon(
        StringBuilder builder,
        int corpusCount,
        int sampleCount,
        IReadOnlyDictionary<string, int> difficultyBreakdown,
        IReadOnlyDictionary<string, int> splitBreakdown,
        int validationIssueCount,
        int missingEvidenceCount,
        int missingProvenanceCount,
        int mustHitMissingCount,
        int mustNotOverlapCount,
        int itemIdLeakageCount,
        int relationInconsistencyCount,
        int judgeWarningCount,
        bool formalRetrievalAllowed,
        bool useForRuntime,
        string recommendation)
    {
        builder.AppendLine();
        builder.AppendLine($"- CorpusItemCount: `{corpusCount}`");
        builder.AppendLine($"- SampleCount: `{sampleCount}`");
        builder.AppendLine($"- ValidationIssueCount: `{validationIssueCount}`");
        builder.AppendLine($"- MissingEvidenceCount: `{missingEvidenceCount}`");
        builder.AppendLine($"- MissingProvenanceCount: `{missingProvenanceCount}`");
        builder.AppendLine($"- MustHitMissingCount: `{mustHitMissingCount}`");
        builder.AppendLine($"- MustNotOverlapCount: `{mustNotOverlapCount}`");
        builder.AppendLine($"- ItemIdLeakageCount: `{itemIdLeakageCount}`");
        builder.AppendLine($"- RelationInconsistencyCount: `{relationInconsistencyCount}`");
        builder.AppendLine($"- JudgeWarningCount: `{judgeWarningCount}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{formalRetrievalAllowed}`");
        builder.AppendLine($"- UseForRuntime: `{useForRuntime}`");
        builder.AppendLine($"- Recommendation: `{recommendation}`");
        AppendBreakdown(builder, "Difficulty Breakdown", difficultyBreakdown);
        AppendBreakdown(builder, "Split Breakdown", splitBreakdown);
    }

    private static void AppendBreakdown(StringBuilder builder, string title, IReadOnlyDictionary<string, int> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine("| Key | Count |");
        builder.AppendLine("|---|---:|");
        foreach (var value in values.OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {value.Key} | {value.Value} |");
        }
    }

    private static string Fingerprint(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
