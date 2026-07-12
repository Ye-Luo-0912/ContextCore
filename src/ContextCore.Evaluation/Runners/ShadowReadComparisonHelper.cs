using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Runners;

/// <summary>Shared shadow-read comparison helpers: stable hashing and canonicalization for dual-write/dual-read coordinators.</summary>
public static class ShadowReadComparisonHelper
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>Canonicalizing stable hash for relation governance shadow reads (lowercase hex).</summary>
    public static string ComputeCanonicalStableHash<T>(T value)
    {
        var canonical = Canonicalize(value);
        var json = JsonSerializer.Serialize(canonical, CanonicalJsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Simple stable hash without canonicalization, using the provided JSON options.</summary>
    public static string ComputeStableHash<T>(T value, JsonSerializerOptions options, bool lowercase)
    {
        var json = JsonSerializer.Serialize(value, options);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var hex = Convert.ToHexString(bytes);
        return lowercase ? hex.ToLowerInvariant() : hex;
    }

    public static object? Canonicalize(object? value)
    {
        return value switch
        {
            null => null,
            ContextRelation relation => new
            {
                relation.Id,
                relation.WorkspaceId,
                relation.CollectionId,
                relation.SourceId,
                relation.TargetId,
                relation.RelationType,
                relation.Weight,
                relation.Confidence,
                relation.CreatedAt,
                SourceRefs = relation.SourceRefs.Order(StringComparer.Ordinal).ToArray(),
                Metadata = relation.Metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            RelationReviewRecord review => new
            {
                review.ReviewId,
                review.RelationId,
                review.WorkspaceId,
                review.CollectionId,
                review.Action,
                review.FromLifecycle,
                review.ToLifecycle,
                review.FromReviewStatus,
                review.ToReviewStatus,
                review.Reviewer,
                review.Reason,
                review.CreatedAt,
                review.ReviewedAt,
                Metadata = review.Metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            RelationDiagnosticsSnapshot diagnostic => new
            {
                diagnostic.DiagnosticId,
                diagnostic.WorkspaceId,
                diagnostic.CollectionId,
                diagnostic.RelationId,
                diagnostic.ItemId,
                diagnostic.DiagnosticKind,
                diagnostic.Severity,
                diagnostic.Message,
                diagnostic.CreatedAt,
                Metadata = diagnostic.Metadata.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).ToArray()
            },
            IEnumerable<ContextRelation> relations => relations
                .OrderBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(Canonicalize)
                .ToArray(),
            IEnumerable<RelationReviewRecord> reviews => reviews
                .OrderBy(static item => item.ReviewId, StringComparer.OrdinalIgnoreCase)
                .Select(Canonicalize)
                .ToArray(),
            IEnumerable<RelationDiagnosticsSnapshot> diagnostics => diagnostics
                .OrderBy(static item => item.DiagnosticId, StringComparer.OrdinalIgnoreCase)
                .Select(Canonicalize)
                .ToArray(),
            _ => value
        };
    }
}
