using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core.Services;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Rendering;

public static partial class ServiceOperationalRenderer
{
    public static string RenderRelations(ServiceRelationsSnapshot snapshot)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Relations");
        builder.AppendLine($"时间       : {snapshot.CurrentTime:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"服务       : {snapshot.BaseUrl}");
        builder.AppendLine();
        builder.AppendLine("Relation Types");
        builder.AppendLine($"Count: {snapshot.RelationTypes.Count}");
        foreach (var type in snapshot.RelationTypes.Take(20))
        {
            builder.AppendLine($"- {type.Type} directional={type.IsDirectional} inverse={type.InverseType ?? "-"} weight={type.DefaultWeight:0.00} evidence={(type.RequiresEvidence ? "yes" : "no")} normalExpansion={(type.AllowsNormalExpansion ? "yes" : "no")}");
        }

        AppendRelationDiagnostics(builder, "Global Relation Diagnostics", snapshot.Diagnostics);

        if (!string.IsNullOrWhiteSpace(snapshot.ItemId))
        {
            builder.AppendLine();
            builder.AppendLine("Item Relations");
            builder.AppendLine($"ItemId   : {snapshot.ItemId}");
            builder.AppendLine($"Outgoing : {snapshot.Relations.Outgoing.Count}");
            foreach (var relation in snapshot.Relations.Outgoing)
            {
                builder.AppendLine($"- OUT {relation.SourceId} -> {relation.TargetId} type={relation.RelationType} weight={relation.Weight:0.00} confidence={relation.Confidence:0.00}");
            }

            builder.AppendLine($"Incoming : {snapshot.Relations.Incoming.Count}");
            foreach (var relation in snapshot.Relations.Incoming)
            {
                builder.AppendLine($"- IN  {relation.SourceId} -> {relation.TargetId} type={relation.RelationType} weight={relation.Weight:0.00} confidence={relation.Confidence:0.00}");
            }

            if (snapshot.ItemDiagnostics is not null)
            {
                AppendRelationDiagnostics(builder, "Item Relation Diagnostics", snapshot.ItemDiagnostics);
            }
        }

        return builder.ToString();
    }

    private static string FormatTargetSections(IReadOnlyDictionary<string, int> sections)
    {
        return sections.Count == 0
            ? "-"
            : string.Join(", ", sections
                .OrderBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static item => $"{item.Key}={item.Value}"));
    }

    public static string RenderRelationExplain(RelationExplainResponse explain)
    {
        var relation = explain.Relation;
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Relation Explain");
        builder.AppendLine($"RelationId : {explain.RelationId}");
        builder.AppendLine($"Type       : {relation?.RelationType ?? explain.TypeDefinition?.Type ?? "-"}");
        builder.AppendLine($"Source     : {relation?.SourceId ?? "-"} ({explain.SourceItem?.Kind ?? "unknown"}, lifecycle={explain.SourceItem?.Lifecycle ?? "-"})");
        builder.AppendLine($"Target     : {relation?.TargetId ?? "-"} ({explain.TargetItem?.Kind ?? "unknown"}, lifecycle={explain.TargetItem?.Lifecycle ?? "-"})");
        builder.AppendLine($"Inverse    : {explain.InverseRelation?.Id ?? "-"}");
        builder.AppendLine($"Confidence : {explain.Confidence:0.00} reason={BlankDash(explain.ConfidenceReason)}");
        builder.AppendLine($"Lifecycle  : {BlankDash(explain.Lifecycle)}");
        builder.AppendLine($"Review     : {BlankDash(explain.ReviewStatus)}");
        builder.AppendLine();
        builder.AppendLine("Evidence");
        builder.AppendLine($"EvidenceRefs: {string.Join(", ", explain.EvidenceRefs.DefaultIfEmpty("-"))}");
        builder.AppendLine($"SourceRefs  : {string.Join(", ", explain.SourceRefs.DefaultIfEmpty("-"))}");
        foreach (var evidence in explain.Evidence.Take(10))
        {
            builder.AppendLine($"- {evidence.EvidenceId} kind={BlankDash(evidence.EvidenceKind)} sourceOperation={BlankDash(evidence.SourceOperationId)} sourceItem={BlankDash(evidence.SourceItemId)}");
            if (!string.IsNullOrWhiteSpace(evidence.EvidenceText))
            {
                builder.AppendLine($"  text: {evidence.EvidenceText}");
            }
        }

        if (explain.TypeDefinition is not null)
        {
            builder.AppendLine();
            builder.AppendLine("Type Definition");
            builder.AppendLine($"- directional={explain.TypeDefinition.IsDirectional} inverse={explain.TypeDefinition.InverseType ?? "-"} requiresEvidence={explain.TypeDefinition.RequiresEvidence} normalExpansion={explain.TypeDefinition.AllowsNormalExpansion}");
        }

        if (explain.Diagnostics.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Diagnostics");
            foreach (var diagnostic in explain.Diagnostics.Take(20))
            {
                builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] {diagnostic.Reason}");
            }
        }

        if (explain.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", explain.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderRelationExpansionProfiles(IReadOnlyList<RelationExpansionProfile> profiles)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Relation Expansion Profiles");
        builder.AppendLine($"Count: {profiles.Count}");
        foreach (var profile in profiles.OrderBy(item => item.ProfileId, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- {profile.ProfileId} mode={profile.Mode} intent={profile.Intent} depth={profile.MaxDepth} fanout={profile.MaxFanout} minConfidence={profile.MinConfidence:0.00}");
            builder.AppendLine($"  allowed={string.Join(", ", profile.AllowedRelationTypes.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  blocked={string.Join(", ", profile.BlockedRelationTypes.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  lifecycle={BlankDash(profile.LifecyclePolicy)} candidate={profile.AllowCandidateRelations} deprecated={profile.AllowDeprecatedRelations} rejected={profile.AllowRejectedRelations} requireEvidence={profile.RequireEvidence}");
        }

        return builder.ToString();
    }

    public static string RenderRelationExpansionPreview(RelationExpansionPreviewResponse preview)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Relation Expansion Preview");
        builder.AppendLine($"Operation : {preview.OperationId}");
        builder.AppendLine($"ItemId    : {preview.ItemId}");
        builder.AppendLine($"Profile   : {preview.Profile.ProfileId} ({preview.Profile.Mode}/{preview.Profile.Intent})");
        builder.AppendLine($"Accepted  : {preview.AcceptedCount}");
        builder.AppendLine($"Blocked   : {preview.BlockedCount}");
        builder.AppendLine();
        builder.AppendLine("Accepted Relations");
        foreach (var relation in preview.AcceptedRelations.Take(20))
        {
            builder.AppendLine($"- {relation.RelationId} depth={relation.Depth} {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} confidence={relation.Confidence:0.00} weight={relation.Weight:0.00}");
            builder.AppendLine($"  section={BlankDash(relation.TargetSection)} reason={BlankDash(relation.SectionReason)} riskNormal={relation.RiskIfNormalSelected} riskAfterRouting={relation.RiskAfterSectionRouting}");
        }

        if (preview.AcceptedRelations.Count == 0)
        {
            builder.AppendLine("- none");
        }

        builder.AppendLine();
        builder.AppendLine("Blocked Relations");
        foreach (var relation in preview.BlockedRelations.Take(30))
        {
            builder.AppendLine($"- {relation.RelationId} depth={relation.Depth} {relation.SourceId} --{relation.RelationType}--> {relation.TargetId} reasons={string.Join(",", relation.Reasons.DefaultIfEmpty("-"))}");
            builder.AppendLine($"  section={BlankDash(relation.TargetSection)} reason={BlankDash(relation.SectionReason)} riskNormal={relation.RiskIfNormalSelected} riskAfterRouting={relation.RiskAfterSectionRouting}");
        }

        if (preview.BlockedRelations.Count == 0)
        {
            builder.AppendLine("- none");
        }

        if (preview.Warnings.Count > 0)
        {
            builder.AppendLine();
            AppendStringSection(builder, "Warnings", preview.Warnings);
        }

        return builder.ToString();
    }

    public static string RenderRelationReviewResult(RelationReviewResult result)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Relation Review Result");
        builder.AppendLine($"Operation  : {result.OperationId}");
        builder.AppendLine($"RelationId : {result.RelationId}");
        builder.AppendLine($"Action     : {result.Action}");
        builder.AppendLine($"Lifecycle  : {BlankDash(result.FromLifecycle)} -> {BlankDash(result.ToLifecycle)}");
        builder.AppendLine($"Review     : {BlankDash(result.FromReviewStatus)} -> {BlankDash(result.ToReviewStatus)}");
        builder.AppendLine($"Reviewer   : {BlankDash(result.Reviewer)}");
        builder.AppendLine($"Reason     : {BlankDash(result.Reason)}");
        builder.AppendLine($"ReviewedAt : {result.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Relation   : {result.Relation.SourceId} --{result.Relation.RelationType}--> {result.Relation.TargetId}");
        AppendStringSection(builder, "Warnings", result.Warnings);

        AppendStringSection(builder, "Errors", result.Errors);

        return builder.ToString();
    }

    public static string RenderRelationReviews(IReadOnlyList<RelationReviewRecord> reviews)
    {
        var builder = new StringBuilder();
        AppendHeader(builder, "Service Relation Review History");
        builder.AppendLine($"Count: {reviews.Count}");
        foreach (var review in reviews.Take(20))
        {
            builder.AppendLine($"- {review.ReviewId} [{review.Action}] {BlankDash(review.FromLifecycle)}->{BlankDash(review.ToLifecycle)} review={BlankDash(review.FromReviewStatus)}->{BlankDash(review.ToReviewStatus)}");
            builder.AppendLine($"  relation={review.RelationId} {review.SourceId} --{review.RelationType}--> {review.TargetId}");
            builder.AppendLine($"  reviewer={BlankDash(review.Reviewer)} reason={Compact(review.Reason, 160)} at={review.ReviewedAt:yyyy-MM-dd HH:mm:ss}");
        }

        return builder.ToString();
    }

    private static void AppendRelationDiagnostics(
        StringBuilder builder,
        string title,
        RelationGraphDiagnosticsReport report)
    {
        builder.AppendLine();
        builder.AppendLine(title);
        builder.AppendLine($"Relations={report.RelationCount} Diagnostics={report.DiagnosticCount}");
        foreach (var diagnostic in report.Diagnostics.Take(20))
        {
            builder.AppendLine($"- {diagnostic.DiagnosticType} [{diagnostic.Severity}] relation={diagnostic.RelationId ?? "-"} {diagnostic.SourceId ?? "-"} --{diagnostic.RelationType ?? "-"}--> {diagnostic.TargetId ?? "-"}");
            builder.AppendLine($"  reason: {diagnostic.Reason}");
            if (diagnostic.RelatedItemIds.Count > 0)
            {
                builder.AppendLine($"  items : {string.Join(", ", diagnostic.RelatedItemIds)}");
            }
        }

        AppendStringSection(builder, "Warnings", report.Warnings);
    }
}
