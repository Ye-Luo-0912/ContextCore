using System.Text.Json;
using ContextCore.Abstractions.Models;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;

namespace ContextCore.ControlRoom.Services;

/// <summary>从 eval fixture corpus 文件构建 relation corpus hygiene 报告。</summary>
public sealed class RelationCorpusHygieneReportBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly RelationTypeRegistry _typeRegistry;
    private readonly RelationTypeNormalizer _typeNormalizer;

    public RelationCorpusHygieneReportBuilder()
        : this(new RelationTypeRegistry(), new RelationTypeNormalizer())
    {
    }

    public RelationCorpusHygieneReportBuilder(
        RelationTypeRegistry typeRegistry,
        RelationTypeNormalizer typeNormalizer)
    {
        _typeRegistry = typeRegistry;
        _typeNormalizer = typeNormalizer;
    }

    public async Task<RelationCorpusHygieneReport> BuildAsync(
        string contextsRootPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextsRootPath);

        var warnings = new List<string>();
        var corpusFileCount = Directory.Exists(contextsRootPath)
            ? Directory.EnumerateFiles(contextsRootPath, "corpus*.json", SearchOption.AllDirectories).Count()
            : 0;
        var relationEntries = await LoadRelationsAsync(contextsRootPath, warnings, cancellationToken)
            .ConfigureAwait(false);
        var unknownTypes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var legacyTypes = new Dictionary<string, RelationCorpusLegacyTypeSummary>(StringComparer.OrdinalIgnoreCase);
        var missingEvidence = new List<RelationCorpusHygieneFinding>();
        var missingConfidence = new List<RelationCorpusHygieneFinding>();
        var missingLifecycle = new List<RelationCorpusHygieneFinding>();
        var missingReviewStatus = new List<RelationCorpusHygieneFinding>();
        var migrations = new List<RelationCorpusMigrationCandidate>();
        var backfills = new List<RelationCorpusBackfillCandidate>();

        foreach (var entry in relationEntries)
        {
            var relation = entry.Relation;
            var normalizedType = _typeNormalizer.Normalize(relation.RelationType);
            if (!string.Equals(normalizedType, relation.RelationType, StringComparison.OrdinalIgnoreCase))
            {
                if (!legacyTypes.TryGetValue(relation.RelationType, out var summary))
                {
                    summary = new RelationCorpusLegacyTypeSummary
                    {
                        LegacyType = relation.RelationType,
                        NormalizedType = normalizedType
                    };
                }

                legacyTypes[relation.RelationType] = new RelationCorpusLegacyTypeSummary
                {
                    LegacyType = summary.LegacyType,
                    NormalizedType = summary.NormalizedType,
                    Count = summary.Count + 1
                };
                migrations.Add(new RelationCorpusMigrationCandidate
                {
                    Category = entry.Category,
                    CorpusFile = entry.CorpusFile,
                    RelationId = relation.Id,
                    LegacyType = relation.RelationType,
                    NormalizedType = normalizedType,
                    SourceId = relation.SourceId,
                    TargetId = relation.TargetId,
                    Suggestion = $"replace relationType '{relation.RelationType}' with '{normalizedType}'"
                });
            }

            if (_typeRegistry.Find(normalizedType) is null)
            {
                unknownTypes[relation.RelationType] = unknownTypes.GetValueOrDefault(relation.RelationType) + 1;
            }

            var missingFields = new List<string>();
            if (!RelationTypeNormalizer.HasEvidence(relation))
            {
                missingFields.Add("evidenceRefs/sourceRefs");
                missingEvidence.Add(Finding(entry, normalizedType, "missing evidence metadata", "backfill fixture evidence when deterministic; otherwise mark NeedsEvidence/Candidate"));
            }

            if (!RelationTypeNormalizer.HasConfidence(relation))
            {
                missingFields.Add("confidence");
                missingConfidence.Add(Finding(entry, normalizedType, "missing confidence", "set deterministic fixture confidence or keep for manual review"));
            }

            if (!RelationTypeNormalizer.HasLifecycle(relation))
            {
                missingFields.Add("lifecycle");
                missingLifecycle.Add(Finding(entry, normalizedType, "missing lifecycle", "set lifecycle=Active for deterministic fixtures"));
            }

            if (!RelationTypeNormalizer.HasReviewStatus(relation))
            {
                missingFields.Add("reviewStatus");
                missingReviewStatus.Add(Finding(entry, normalizedType, "missing reviewStatus", "set reviewStatus=Reviewed for deterministic fixtures or NeedsEvidence for incomplete relations"));
            }

            if (missingFields.Count > 0)
            {
                var canBackfillEvidence = RelationTypeNormalizer.CanBackfillDeterministicEvidence(relation);
                backfills.Add(new RelationCorpusBackfillCandidate
                {
                    Category = entry.Category,
                    CorpusFile = entry.CorpusFile,
                    RelationId = relation.Id,
                    RelationType = relation.RelationType,
                    NormalizedType = normalizedType,
                    MissingFields = missingFields.ToArray(),
                    CanBackfillEvidence = canBackfillEvidence,
                    BackfillPolicy = canBackfillEvidence ? RelationTypeNormalizer.FixtureBackfillCreatedFrom : "manual_review_required",
                    Suggestion = canBackfillEvidence
                        ? "shadow backfill evidenceRefs/sourceRefs/sourceOperationId/confidence/lifecycle/reviewStatus"
                        : "do not fabricate evidence; mark reviewStatus=NeedsEvidence and lifecycle=Candidate"
                });
            }
        }

        return new RelationCorpusHygieneReport
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ContextsRootPath = contextsRootPath,
            CorpusFileCount = corpusFileCount,
            RelationCount = relationEntries.Count,
            UnknownRelationTypes = unknownTypes,
            LegacyRelationTypes = legacyTypes,
            MissingEvidenceRelations = missingEvidence
                .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.RelationId, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            MissingConfidenceRelations = missingConfidence.ToArray(),
            MissingLifecycleRelations = missingLifecycle.ToArray(),
            MissingReviewStatusRelations = missingReviewStatus.ToArray(),
            MigrationCandidates = migrations.ToArray(),
            BackfillCandidates = backfills.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    public static string BuildMarkdownReport(RelationCorpusHygieneReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = new List<string>
        {
            "# Relation Corpus Hygiene Report",
            string.Empty,
            $"Generated: {report.CreatedAt:O}",
            string.Empty,
            "## Summary",
            string.Empty,
            $"- Corpus files: `{report.CorpusFileCount}`",
            $"- Relations: `{report.RelationCount}`",
            $"- Unknown relation types: `{report.UnknownRelationTypes.Values.Sum()}`",
            $"- Legacy relation types: `{report.LegacyRelationTypes.Values.Sum(item => item.Count)}`",
            $"- Missing evidence relations: `{report.MissingEvidenceRelations.Count}`",
            $"- Missing confidence relations: `{report.MissingConfidenceRelations.Count}`",
            $"- Missing lifecycle relations: `{report.MissingLifecycleRelations.Count}`",
            $"- Missing review status relations: `{report.MissingReviewStatusRelations.Count}`",
            $"- Migration candidates: `{report.MigrationCandidates.Count}`",
            $"- Backfill candidates: `{report.BackfillCandidates.Count}`",
            string.Empty,
            "## Legacy Relation Types",
            string.Empty,
            "| Legacy | Normalized | Count |",
            "|---|---|---:|"
        };

        foreach (var item in report.LegacyRelationTypes.Values
            .OrderBy(item => item.LegacyType, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"| {item.LegacyType} | {item.NormalizedType} | {item.Count} |");
        }

        if (report.LegacyRelationTypes.Count == 0)
        {
            lines.Add("| - | - | 0 |");
        }

        lines.AddRange(
        [
            string.Empty,
            "## Unknown Relation Types",
            string.Empty,
            "| Type | Count |",
            "|---|---:|"
        ]);
        foreach (var item in report.UnknownRelationTypes.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            lines.Add($"| {item.Key} | {item.Value} |");
        }

        if (report.UnknownRelationTypes.Count == 0)
        {
            lines.Add("| - | 0 |");
        }

        AppendFindings(lines, "Missing Evidence Relations", report.MissingEvidenceRelations);
        AppendBackfills(lines, report.BackfillCandidates);

        if (report.Warnings.Count > 0)
        {
            lines.AddRange(
            [
                string.Empty,
                "## Warnings",
                string.Empty
            ]);
            lines.AddRange(report.Warnings.Select(warning => $"- {warning}"));
        }

        lines.Add(string.Empty);
        return string.Join(Environment.NewLine, lines);
    }

    public ContextRelation NormalizeAndBackfillFixtureRelation(
        ContextRelation relation,
        string sourceOperationId)
    {
        return _typeNormalizer.NormalizeAndBackfillFixtureRelation(relation, sourceOperationId);
    }

    private static void AppendFindings(
        List<string> lines,
        string title,
        IReadOnlyList<RelationCorpusHygieneFinding> findings)
    {
        lines.AddRange(
        [
            string.Empty,
            $"## {title}",
            string.Empty,
            "| Category | Relation | Type | Normalized | Reason | Suggestion |",
            "|---|---|---|---|---|---|"
        ]);

        foreach (var finding in findings.Take(40))
        {
            lines.Add($"| {finding.Category} | {finding.RelationId} | {finding.RelationType} | {finding.NormalizedType} | {finding.Reason} | {finding.Suggestion} |");
        }

        if (findings.Count == 0)
        {
            lines.Add("| - | - | - | - | - | - |");
        }
    }

    private static void AppendBackfills(
        List<string> lines,
        IReadOnlyList<RelationCorpusBackfillCandidate> backfills)
    {
        lines.AddRange(
        [
            string.Empty,
            "## Backfill Candidates",
            string.Empty,
            "| Category | Relation | Type | Missing Fields | Can Backfill Evidence | Policy |",
            "|---|---|---|---|---|---|"
        ]);

        foreach (var item in backfills.Take(60))
        {
            lines.Add($"| {item.Category} | {item.RelationId} | {item.NormalizedType} | {string.Join(", ", item.MissingFields)} | {item.CanBackfillEvidence} | {item.BackfillPolicy} |");
        }

        if (backfills.Count == 0)
        {
            lines.Add("| - | - | - | - | - | - |");
        }
    }

    private static RelationCorpusHygieneFinding Finding(
        RelationCorpusEntry entry,
        string normalizedType,
        string reason,
        string suggestion)
    {
        return new RelationCorpusHygieneFinding
        {
            Category = entry.Category,
            CorpusFile = entry.CorpusFile,
            RelationId = entry.Relation.Id,
            SourceId = entry.Relation.SourceId,
            TargetId = entry.Relation.TargetId,
            RelationType = entry.Relation.RelationType,
            NormalizedType = normalizedType,
            Reason = reason,
            Suggestion = suggestion
        };
    }

    private static async Task<IReadOnlyList<RelationCorpusEntry>> LoadRelationsAsync(
        string contextsRootPath,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(contextsRootPath))
        {
            warnings.Add($"contexts root does not exist: {contextsRootPath}");
            return Array.Empty<RelationCorpusEntry>();
        }

        var entries = new List<RelationCorpusEntry>();
        foreach (var file in Directory.EnumerateFiles(contextsRootPath, "corpus*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var category = new DirectoryInfo(Path.GetDirectoryName(file) ?? contextsRootPath).Name;
            var corpus = JsonSerializer.Deserialize<ContextEvalCorpus>(
                await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false),
                JsonOptions) ?? new ContextEvalCorpus();
            foreach (var relation in corpus.Relations)
            {
                entries.Add(new RelationCorpusEntry(category, Path.GetFileName(file), relation));
            }
        }

        return entries;
    }

    private sealed record RelationCorpusEntry(string Category, string CorpusFile, ContextRelation Relation);
}
