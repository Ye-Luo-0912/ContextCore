using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>从正式 selected item 周边生成图扩展 shadow trace；只读采集，不改变正式输出。</summary>
public sealed class GraphExpansionShadowTraceBuilder
{
    private static readonly IReadOnlyList<string> DefaultProfiles = ["audit-v1", "conflict-v1"];
    private readonly RelationExpansionPreviewService _previewService;

    public GraphExpansionShadowTraceBuilder(RelationExpansionPreviewService previewService)
    {
        _previewService = previewService;
    }

    public async Task<GraphExpansionShadowTrace> BuildAsync(
        ContextRetrievalRequest request,
        IReadOnlyList<ContextRetrievalCandidate> selectedCandidates,
        GraphExpansionShadowOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectedCandidates);
        ArgumentNullException.ThrowIfNull(options);

        var profiles = NormalizeProfiles(options.Profiles);
        var maxRelations = options.MaxRelationsPerTrace > 0 ? options.MaxRelationsPerTrace : 50;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["formalOutputChanged"] = "false",
            ["selectedSetChanged"] = "false",
            ["packageSectionsChanged"] = "false",
            ["maxRelationsPerTrace"] = maxRelations.ToString()
        };

        if (!options.Enabled || !options.TraceCollectionEnabled)
        {
            return new GraphExpansionShadowTrace
            {
                GraphExpansionShadowEnabled = false,
                GraphExpansionProfiles = profiles,
                Metadata = metadata
            };
        }

        var accepted = new List<RelationExpansionPreviewRelation>();
        var blocked = new List<RelationExpansionPreviewRelation>();
        var seedIds = selectedCandidates
            .Select(static candidate => candidate.SourceId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var profileId in profiles)
        {
            foreach (var seedId in seedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var preview = await _previewService
                    .PreviewAsync(
                        new RelationExpansionPreviewRequest
                        {
                            OperationId = $"graph-shadow-{Guid.NewGuid():N}",
                            WorkspaceId = request.WorkspaceId,
                            CollectionId = request.CollectionId,
                            ItemId = seedId,
                            ProfileId = profileId
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                accepted.AddRange(preview.AcceptedRelations.Select(relation => CloneForTrace(relation, profileId, seedId)));
                blocked.AddRange(preview.BlockedRelations.Select(relation => CloneForTrace(relation, profileId, seedId)));
            }
        }

        var acceptedLimited = accepted
            .DistinctBy(static relation => $"{relation.Metadata.GetValueOrDefault("graphExpansionProfile")}\u001f{relation.RelationId}\u001f{relation.SourceId}\u001f{relation.TargetId}")
            .Take(maxRelations)
            .ToArray();
        var blockedLimited = blocked
            .DistinctBy(static relation => $"{relation.Metadata.GetValueOrDefault("graphExpansionProfile")}\u001f{relation.RelationId}\u001f{relation.SourceId}\u001f{relation.TargetId}")
            .Take(Math.Max(0, maxRelations - acceptedLimited.Length))
            .ToArray();

        var targetSections = acceptedLimited
            .GroupBy(static relation => NormalizeSection(relation.TargetSection), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var wrongSectionRisk = acceptedLimited.Count(static relation => relation.RiskAfterSectionRouting)
            + blockedLimited.Count(static relation => relation.Reasons.Any(reason =>
                string.Equals(reason, RelationExpansionValidationReasons.BlockedByWrongSectionRisk, StringComparison.OrdinalIgnoreCase)));

        metadata["seedCount"] = seedIds.Length.ToString();
        metadata["acceptedRelationCount"] = acceptedLimited.Length.ToString();
        metadata["blockedRelationCount"] = blockedLimited.Length.ToString();
        metadata["traceSignature"] = BuildTraceSignature(request, profiles, acceptedLimited, blockedLimited);

        return new GraphExpansionShadowTrace
        {
            GraphExpansionShadowEnabled = true,
            GraphExpansionProfiles = profiles,
            AcceptedRelations = acceptedLimited,
            BlockedRelations = blockedLimited,
            TargetSections = targetSections,
            RiskIfNormal = acceptedLimited.Count(static relation => relation.RiskIfNormalSelected),
            RiskAfterRouting = acceptedLimited.Count(static relation => relation.RiskAfterSectionRouting),
            HistoricalAuditCount = acceptedLimited.Count(IsHistoricalAuditRelation),
            ConflictEvidenceCount = acceptedLimited.Count(static relation =>
                string.Equals(relation.TargetSection, GraphExpansionTargetSection.ConflictEvidence, StringComparison.OrdinalIgnoreCase)),
            WrongSectionRisk = wrongSectionRisk,
            Metadata = metadata
        };
    }

    private static IReadOnlyList<string> NormalizeProfiles(IReadOnlyList<string>? profiles)
    {
        var materialized = profiles?
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return materialized is { Length: > 0 } ? materialized : DefaultProfiles;
    }

    private static RelationExpansionPreviewRelation CloneForTrace(
        RelationExpansionPreviewRelation relation,
        string profileId,
        string seedId)
    {
        var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["graphExpansionProfile"] = profileId,
            ["graphExpansionSeedItemId"] = seedId
        };

        return new RelationExpansionPreviewRelation
        {
            RelationId = relation.RelationId,
            SourceId = relation.SourceId,
            TargetId = relation.TargetId,
            RelationType = relation.RelationType,
            TraversalDirection = relation.TraversalDirection,
            Depth = relation.Depth,
            Confidence = relation.Confidence,
            Weight = relation.Weight,
            Lifecycle = relation.Lifecycle,
            ReviewStatus = relation.ReviewStatus,
            TargetLifecycle = relation.TargetLifecycle,
            TargetSection = relation.TargetSection,
            SectionReason = relation.SectionReason,
            RiskIfNormalSelected = relation.RiskIfNormalSelected,
            RiskAfterSectionRouting = relation.RiskAfterSectionRouting,
            Path = relation.Path,
            Reasons = relation.Reasons.ToArray(),
            Warnings = relation.Warnings.ToArray(),
            Metadata = metadata
        };
    }

    private static string NormalizeSection(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? GraphExpansionTargetSection.Excluded : value.Trim();
    }

    private static bool IsHistoricalAuditRelation(RelationExpansionPreviewRelation relation)
    {
        return string.Equals(relation.TargetSection, GraphExpansionTargetSection.AuditContext, StringComparison.OrdinalIgnoreCase)
            || string.Equals(relation.TargetSection, GraphExpansionTargetSection.HistoricalContext, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTraceSignature(
        ContextRetrievalRequest request,
        IReadOnlyList<string> profiles,
        IReadOnlyList<RelationExpansionPreviewRelation> accepted,
        IReadOnlyList<RelationExpansionPreviewRelation> blocked)
    {
        var builder = new StringBuilder();
        builder.Append("query=").Append(NormalizeSignaturePart(request.QueryText)).Append('\n');
        builder.Append("profiles=").Append(string.Join(",", profiles.Order(StringComparer.OrdinalIgnoreCase))).Append('\n');
        AppendRelations(builder, "accepted", accepted);
        AppendRelations(builder, "blocked", blocked);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AppendRelations(
        StringBuilder builder,
        string prefix,
        IReadOnlyList<RelationExpansionPreviewRelation> relations)
    {
        foreach (var relation in relations
            .Select(relation => string.Join(
                '\u001f',
                NormalizeSignaturePart(relation.Metadata.GetValueOrDefault("graphExpansionProfile")),
                NormalizeSignaturePart(relation.RelationId),
                NormalizeSignaturePart(relation.SourceId),
                NormalizeSignaturePart(relation.TargetId),
                NormalizeSignaturePart(relation.RelationType),
                NormalizeSignaturePart(relation.TargetSection),
                NormalizeSignaturePart(relation.TargetLifecycle),
                string.Join("|", relation.Reasons.Order(StringComparer.OrdinalIgnoreCase))))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(prefix).Append('=').Append(relation).Append('\n');
        }
    }

    private static string NormalizeSignaturePart(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();
    }
}
