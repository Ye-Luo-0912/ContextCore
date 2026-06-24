using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services.Graph;

/// <summary>图扩展 guarded apply 策略；只生成辅助 section 贡献，不修改正式 selected set。</summary>
public sealed class GraphExpansionApplyPolicy
{
    public const string SourceMarker = "graph_expansion_guarded";

    private static readonly string[] AllowedApplyProfiles = ["audit-v1", "conflict-v1"];

    private readonly RelationExpansionPreviewService _previewService;
    private readonly IContextStore _contextStore;
    private readonly IMemoryStore? _memoryStore;
    private readonly IConstraintStore? _constraintStore;

    public GraphExpansionApplyPolicy(
        RelationExpansionPreviewService previewService,
        IContextStore contextStore,
        IMemoryStore? memoryStore = null,
        IConstraintStore? constraintStore = null)
    {
        _previewService = previewService;
        _contextStore = contextStore;
        _memoryStore = memoryStore;
        _constraintStore = constraintStore;
    }

    public async Task<GraphExpansionSectionContribution> BuildContributionAsync(
        ContextPackageRequest request,
        IReadOnlyList<ContextPackageDecision> selectedItems,
        GraphExpansionApplyOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selectedItems);
        ArgumentNullException.ThrowIfNull(options);

        var mode = NormalizeMode(options.Mode);
        if (string.Equals(mode, GraphExpansionApplyOptions.OffMode, StringComparison.OrdinalIgnoreCase))
        {
            return new GraphExpansionSectionContribution
            {
                Mode = GraphExpansionApplyOptions.OffMode
            };
        }

        var profiles = ResolveProfiles(options).ToArray();
        if (profiles.Length == 0)
        {
            return new GraphExpansionSectionContribution
            {
                Mode = mode,
                FallbackUsed = string.Equals(mode, GraphExpansionApplyOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase),
                FallbackReason = "no_opt_in_profiles"
            };
        }

        var illegalProfiles = profiles
            .Where(profile => !AllowedApplyProfiles.Contains(profile, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (illegalProfiles.Length > 0)
        {
            return new GraphExpansionSectionContribution
            {
                Mode = mode,
                Profiles = profiles,
                FallbackUsed = string.Equals(mode, GraphExpansionApplyOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase),
                FallbackReason = $"profile_not_allowed:{string.Join(",", illegalProfiles)}",
                Warnings = illegalProfiles
                    .Select(profile => $"profile {profile} is not allowed for guarded graph expansion apply.")
                    .ToArray()
            };
        }

        var seedIds = selectedItems
            .Select(item => item.ItemId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (seedIds.Length == 0)
        {
            return new GraphExpansionSectionContribution
            {
                Mode = mode,
                Profiles = profiles,
                FallbackUsed = false,
                FallbackReason = string.Empty
            };
        }

        var accepted = new Dictionary<string, RelationExpansionPreviewRelation>(StringComparer.OrdinalIgnoreCase);
        var blocked = new Dictionary<string, RelationExpansionPreviewRelation>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var profile in profiles)
        {
            foreach (var seedId in seedIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operationId = request.Metadata.TryGetValue("operationId", out var metadataOperationId)
                    && !string.IsNullOrWhiteSpace(metadataOperationId)
                    ? metadataOperationId
                    : $"graph-apply-{Guid.NewGuid():N}";
                var preview = await _previewService.PreviewAsync(new RelationExpansionPreviewRequest
                {
                    OperationId = $"{operationId}:graph:{profile}:{seedId}",
                    WorkspaceId = request.WorkspaceId,
                    CollectionId = request.CollectionId,
                    ItemId = seedId,
                    ProfileId = profile
                }, cancellationToken).ConfigureAwait(false);

                foreach (var relation in preview.AcceptedRelations)
                {
                    accepted.TryAdd($"{profile}:{relation.RelationId}", WithProfile(relation, profile));
                }

                foreach (var relation in preview.BlockedRelations)
                {
                    blocked.TryAdd($"{profile}:{relation.RelationId}", WithProfile(relation, profile));
                }

                warnings.AddRange(preview.Warnings);
            }
        }

        var riskChecks = BuildRiskChecks(accepted.Values, blocked.Values, options);
        if (options.FallbackOnRisk && riskChecks.HasRisk)
        {
            return new GraphExpansionSectionContribution
            {
                Mode = mode,
                Profiles = profiles,
                FallbackUsed = string.Equals(mode, GraphExpansionApplyOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase),
                FallbackReason = BuildRiskFallbackReason(riskChecks),
                RiskChecks = riskChecks,
                Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        if (string.Equals(mode, GraphExpansionApplyOptions.ShadowMode, StringComparison.OrdinalIgnoreCase))
        {
            return new GraphExpansionSectionContribution
            {
                Mode = mode,
                Profiles = profiles,
                Applied = false,
                TargetSections = accepted.Values
                    .Select(relation => relation.TargetSection)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                RiskChecks = riskChecks,
                Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        var maxAdded = options.MaxAddedItemsPerPackage > 0
            ? options.MaxAddedItemsPerPackage
            : 20;
        var contributionItems = new List<GraphExpansionSectionContributionItem>();
        var addedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in accepted.Values
            .OrderBy(item => ResolveProfileOrder(item.Metadata.GetValueOrDefault("graphExpansionProfile")))
            .ThenBy(item => item.TargetSection, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.TargetId, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (contributionItems.Count >= maxAdded)
            {
                warnings.Add("graph expansion guarded apply reached MaxAddedItemsPerPackage.");
                break;
            }

            if (!IsAllowedSection(relation.TargetSection, options))
            {
                continue;
            }

            var dedupeKey = $"{relation.TargetSection}:{relation.TargetId}";
            if (!addedTargets.Add(dedupeKey))
            {
                continue;
            }

            var target = await ResolveTargetAsync(
                    request.WorkspaceId,
                    request.CollectionId,
                    relation.TargetId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (target is null)
            {
                warnings.Add($"graph expansion target missing: {relation.TargetId}");
                continue;
            }

            contributionItems.Add(BuildContributionItem(relation, target));
        }

        return new GraphExpansionSectionContribution
        {
            Mode = mode,
            Applied = contributionItems.Count > 0,
            Profiles = profiles,
            AddedItems = contributionItems.ToArray(),
            TargetSections = contributionItems
                .Select(item => item.TargetSection)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            RiskChecks = riskChecks,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static RelationExpansionPreviewRelation WithProfile(
        RelationExpansionPreviewRelation relation,
        string profile)
    {
        var metadata = new Dictionary<string, string>(relation.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["graphExpansionProfile"] = profile
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
            Reasons = relation.Reasons,
            Warnings = relation.Warnings,
            Metadata = metadata
        };
    }

    private static GraphExpansionSectionContributionItem BuildContributionItem(
        RelationExpansionPreviewRelation relation,
        GraphExpansionResolvedTarget target)
    {
        var sourceRefs = relation.Metadata.TryGetValue("evidenceRefs", out var evidenceRefs)
            ? SplitCsv(evidenceRefs).Concat(target.SourceRefs).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : target.SourceRefs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var profile = relation.Metadata.GetValueOrDefault("graphExpansionProfile") ?? string.Empty;
        var content = BuildSectionContent(relation, target, profile);

        return new GraphExpansionSectionContributionItem
        {
            ItemId = target.Id,
            RelationId = relation.RelationId,
            ProfileId = profile,
            TargetSection = relation.TargetSection,
            SectionReason = relation.SectionReason,
            Content = content,
            SourceRefs = sourceRefs,
            ItemRefs = [target.Id],
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = SourceMarker,
                ["profileId"] = profile,
                ["relationId"] = relation.RelationId,
                ["relationType"] = relation.RelationType,
                ["targetSection"] = relation.TargetSection,
                ["sectionReason"] = relation.SectionReason,
                ["riskIfNormalSelected"] = relation.RiskIfNormalSelected ? "true" : "false",
                ["riskAfterSectionRouting"] = relation.RiskAfterSectionRouting ? "true" : "false"
            }
        };
    }

    private static string BuildSectionContent(
        RelationExpansionPreviewRelation relation,
        GraphExpansionResolvedTarget target,
        string profile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"### {ResolveTitle(target)}");
        builder.AppendLine($"- source={SourceMarker}");
        builder.AppendLine($"- profile={profile}");
        builder.AppendLine($"- relation={relation.RelationId} ({relation.RelationType})");
        builder.AppendLine($"- targetSection={relation.TargetSection}");
        builder.AppendLine($"- sectionReason={relation.SectionReason}");
        builder.AppendLine();
        builder.AppendLine(target.Content);
        return builder.ToString().Trim();
    }

    private static GraphExpansionApplyRiskChecks BuildRiskChecks(
        IEnumerable<RelationExpansionPreviewRelation> acceptedRelations,
        IEnumerable<RelationExpansionPreviewRelation> blockedRelations,
        GraphExpansionApplyOptions options)
    {
        var accepted = acceptedRelations.ToArray();
        var blocked = blockedRelations.ToArray();
        return new GraphExpansionApplyRiskChecks
        {
            RiskAfterRoutingCount = accepted.Count(relation => relation.RiskAfterSectionRouting),
            WrongSectionRiskCount =
                accepted.Count(relation => IsWrongSectionRisk(relation, options))
                + blocked.Count(relation => relation.Reasons.Contains(
                    RelationExpansionValidationReasons.BlockedByWrongSectionRisk,
                    StringComparer.OrdinalIgnoreCase)),
            MustNotHitRiskCount = accepted.Count(IsMustNotHitRisk),
            LifecycleRiskCount = accepted.Count(IsLifecycleRisk),
            MissingEvidenceCount = accepted.Count(relation => relation.Reasons.Contains(
                RelationExpansionValidationReasons.MissingEvidence,
                StringComparer.OrdinalIgnoreCase))
        };
    }

    private static bool IsWrongSectionRisk(
        RelationExpansionPreviewRelation relation,
        GraphExpansionApplyOptions options)
    {
        if (options.DisallowNormalContextInjection
            && string.Equals(relation.TargetSection, GraphExpansionTargetSection.NormalContext, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !IsAllowedSection(relation.TargetSection, options);
    }

    private static bool IsAllowedSection(
        string section,
        GraphExpansionApplyOptions options)
    {
        return !string.IsNullOrWhiteSpace(section) && options.AllowedTargetSections.Contains(section, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsMustNotHitRisk(RelationExpansionPreviewRelation relation)
    {
        return IsTrue(relation.Metadata.GetValueOrDefault("mustNotHitRisk"))
            || IsTrue(relation.Metadata.GetValueOrDefault("wouldAddMustNotHit"));
    }

    private static bool IsLifecycleRisk(RelationExpansionPreviewRelation relation)
    {
        return relation.RiskAfterSectionRouting
            || IsTrue(relation.Metadata.GetValueOrDefault("lifecycleRisk"))
            || string.Equals(relation.TargetLifecycle, StableMemoryLifecycle.Rejected, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRiskFallbackReason(GraphExpansionApplyRiskChecks riskChecks)
    {
        var parts = new List<string>();
        if (riskChecks.RiskAfterRoutingCount > 0) parts.Add($"riskAfterRouting={riskChecks.RiskAfterRoutingCount}");
        if (riskChecks.WrongSectionRiskCount > 0) parts.Add($"wrongSection={riskChecks.WrongSectionRiskCount}");
        if (riskChecks.MustNotHitRiskCount > 0) parts.Add($"mustNotHit={riskChecks.MustNotHitRiskCount}");
        if (riskChecks.LifecycleRiskCount > 0) parts.Add($"lifecycle={riskChecks.LifecycleRiskCount}");
        if (riskChecks.MissingEvidenceCount > 0) parts.Add($"missingEvidence={riskChecks.MissingEvidenceCount}");
        return parts.Count == 0 ? string.Empty : string.Join(";", parts);
    }

    private static IEnumerable<string> ResolveProfiles(GraphExpansionApplyOptions options)
    {
        return options.OptInProfiles
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Select(profile => profile.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeMode(string? mode)
    {
        if (string.Equals(mode, GraphExpansionApplyOptions.ShadowMode, StringComparison.OrdinalIgnoreCase))
        {
            return GraphExpansionApplyOptions.ShadowMode;
        }

        if (string.Equals(mode, GraphExpansionApplyOptions.ApplyGuardedMode, StringComparison.OrdinalIgnoreCase))
        {
            return GraphExpansionApplyOptions.ApplyGuardedMode;
        }

        return GraphExpansionApplyOptions.OffMode;
    }

    private static int ResolveProfileOrder(string? profile)
    {
        if (string.Equals(profile, "audit-v1", StringComparison.OrdinalIgnoreCase)) return 0;
        return string.Equals(profile, "conflict-v1", StringComparison.OrdinalIgnoreCase) ? 1 : 99;
    }

    private async Task<GraphExpansionResolvedTarget?> ResolveTargetAsync(
        string workspaceId,
        string collectionId,
        string targetId,
        CancellationToken cancellationToken)
    {
        var contextItem = await _contextStore
            .GetAsync(workspaceId, collectionId, targetId, cancellationToken)
            .ConfigureAwait(false);
        if (contextItem is not null)
        {
            return new GraphExpansionResolvedTarget(
                contextItem.Id,
                string.IsNullOrWhiteSpace(contextItem.Title) ? contextItem.Id : contextItem.Title!,
                contextItem.Content,
                contextItem.ContentFormat,
                contextItem.SourceRefs);
        }

        if (_memoryStore is not null)
        {
            var memory = await _memoryStore
                .GetAsync(workspaceId, collectionId, targetId, cancellationToken)
                .ConfigureAwait(false);
            if (memory is not null)
            {
                return new GraphExpansionResolvedTarget(
                    memory.Id,
                    string.IsNullOrWhiteSpace(memory.Type) ? memory.Id : $"{memory.Layer}:{memory.Type}",
                    memory.Content,
                    memory.ContentFormat,
                    memory.SourceRefs);
            }
        }

        if (_constraintStore is not null)
        {
            var constraint = await _constraintStore
                .GetAsync(targetId, cancellationToken)
                .ConfigureAwait(false);
            if (constraint is not null
                && string.Equals(constraint.WorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(constraint.CollectionId)
                    || string.Equals(constraint.CollectionId, collectionId, StringComparison.OrdinalIgnoreCase)))
            {
                return new GraphExpansionResolvedTarget(
                    constraint.Id,
                    $"constraint:{constraint.Level}",
                    constraint.Content,
                    ContextContentFormat.Markdown,
                    constraint.SourceRefs);
            }
        }

        return null;
    }

    private static string ResolveTitle(GraphExpansionResolvedTarget item)
    {
        return string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title;
    }

    private static IReadOnlyList<string> SplitCsv(string value)
    {
        return value.Split([',', ';', '；', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 判断给定的字符串值是否表示真。
    /// </summary>
    /// <param name="value">要判断的字符串值。</param>
    /// <returns>如果字符串值等于 "true", "1" 或 "yes"（不区分大小写），则返回 true；否则返回 false。</returns>
    private static bool IsTrue(string? value)
    {
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>表示图扩展解析后的目标项，包含了解析后的内容及其相关信息。</summary>
    /// <param name="Id">目标项的唯一标识符。</param>
    /// <param name="Title">目标项的标题，若无则使用 Id 作为标题。</param>
    /// <param name="Content">目标项的具体内容。</param>
    /// <param name="ContentFormat">目标项内容的格式。</param>
    /// <param name="SourceRefs">引用来源列表，指示了该目标项所关联的数据源。</param>
    private sealed record GraphExpansionResolvedTarget(
        string Id,
        string Title,
        string Content,
        ContextContentFormat ContentFormat,
        IReadOnlyList<string> SourceRefs);
}
