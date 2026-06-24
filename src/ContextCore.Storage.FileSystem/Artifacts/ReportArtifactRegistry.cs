using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>Report artifact 的可治理路由表，只按报告目录和能力分类。</summary>
public static class ReportArtifactRegistry
{
    public static bool TryClassify(
        string legacyPath,
        out ArtifactKind kind,
        out string capabilityId,
        out string? providerId)
    {
        var normalized = NormalizePath(legacyPath);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        kind = ArtifactKind.Report;
        capabilityId = "reports";
        providerId = null;

        if (segments.Length == 0)
        {
            return false;
        }

        if (segments[0].Equals("learning", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
        {
            capabilityId = segments[1].ToLowerInvariant();
            kind = capabilityId switch
            {
                "feedback" => ArtifactKind.LearningFeedback,
                "router" => ArtifactKind.Router,
                "ranker" => ArtifactKind.Ranker,
                "readiness" => ArtifactKind.Report,
                _ => ArtifactKind.Report
            };
            return IsFirstBatchLearningCapability(capabilityId);
        }

        if (segments[0].Equals("vector", StringComparison.OrdinalIgnoreCase))
        {
            capabilityId = segments.Length > 1 ? segments[1].ToLowerInvariant() : "vector";
            kind = ArtifactKind.Vector;
            providerId = TryResolveProvider(normalized);
            return capabilityId.Equals("reindex", StringComparison.OrdinalIgnoreCase)
                || capabilityId.Equals("query", StringComparison.OrdinalIgnoreCase)
                || capabilityId.Equals("vector", StringComparison.OrdinalIgnoreCase);
        }

        if (segments[0].Equals("eval", StringComparison.OrdinalIgnoreCase))
        {
            var file = Path.GetFileNameWithoutExtension(normalized);
            if (file.StartsWith("vector-", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArtifactKind.Eval;
                capabilityId = "vector";
                providerId = TryResolveProvider(normalized);
                return true;
            }

            if (file.StartsWith("graph-", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("relation-expansion", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("relation-corpus", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArtifactKind.Eval;
                capabilityId = "graph";
                return true;
            }

            if (file.StartsWith("eval-report-p15", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("extended-failure-triage", StringComparison.OrdinalIgnoreCase))
            {
                kind = ArtifactKind.Eval;
                capabilityId = "p15";
                return true;
            }
        }

        return false;
    }

    public static bool ShouldMirror(string legacyPath)
        => TryClassify(legacyPath, out _, out _, out _);

    private static bool IsFirstBatchLearningCapability(string capabilityId)
        => capabilityId.Equals("feedback", StringComparison.OrdinalIgnoreCase)
            || capabilityId.Equals("readiness", StringComparison.OrdinalIgnoreCase)
            || capabilityId.Equals("router", StringComparison.OrdinalIgnoreCase)
            || capabilityId.Equals("ranker", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static string? TryResolveProvider(string normalizedPath)
        => normalizedPath.Contains("onnx", StringComparison.OrdinalIgnoreCase)
            ? "onnx-local"
            : null;
}
