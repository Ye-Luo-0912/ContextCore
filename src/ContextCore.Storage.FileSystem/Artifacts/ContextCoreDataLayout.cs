using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.Storage.FileSystem;

/// <summary>ContextCore 文件系统标准布局解析器。</summary>
public sealed class ContextCoreDataLayout : IContextPathResolver
{
    private static readonly char[] InvalidSegmentCharacters = Path.GetInvalidFileNameChars()
        .Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|'])
        .Distinct()
        .ToArray();

    private readonly string _rootPath;

    public ContextCoreDataLayout(FileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _rootPath = EnsureTrailingSeparator(options.ResolvedRootPath);
    }

    public string RootPath => _rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public string SanitizeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch) || InvalidSegmentCharacters.Contains(ch))
            {
                builder.Append('-');
                continue;
            }

            builder.Append(ch);
        }

        var sanitized = builder.ToString().Trim('.', ' ', '-');
        if (sanitized.Length == 0)
        {
            return "default";
        }

        return sanitized.Length <= 96 ? sanitized : sanitized[..96];
    }

    public string ResolveArtifactPath(ArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var relativeSegments = BuildRelativeSegments(descriptor);
        var relativePath = Path.Combine(relativeSegments.ToArray());
        var fullPath = Path.GetFullPath(Path.Combine(RootPath, relativePath));
        EnsureInsideRoot(fullPath);
        return fullPath;
    }

    public string GetRelativePath(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        var normalized = Path.GetFullPath(fullPath);
        EnsureInsideRoot(normalized);
        return Path.GetRelativePath(RootPath, normalized);
    }

    public string GetManifestPath()
    {
        var path = Path.GetFullPath(Path.Combine(RootPath, "system", "artifact-manifest.jsonl"));
        EnsureInsideRoot(path);
        return path;
    }

    public ArtifactDescriptor CreateDescriptorFromLegacyReportPath(
        string path,
        string? workspaceId,
        string? collectionId)
    {
        return new ReportArtifactDescriptorFactory().CreateSnapshot(path, workspaceId, collectionId);
    }

    public ArtifactManifestEntry CreateManifestEntry(
        ArtifactDescriptor descriptor,
        string fullPath,
        string? legacyPath = null,
        bool isLatest = false,
        bool isSnapshot = true,
        string? sourceCommand = null,
        string? policyVersion = null)
    {
        var relativePath = GetRelativePath(fullPath);
        var now = DateTimeOffset.UtcNow;
        var extension = NormalizeExtension(descriptor.Extension);
        return new ArtifactManifestEntry
        {
            ArtifactId = CreateArtifactId(descriptor),
            ArtifactKind = descriptor.Kind,
            Descriptor = descriptor,
            WorkspaceId = descriptor.WorkspaceId,
            CollectionId = descriptor.CollectionId,
            RelativePath = relativePath.Replace('\\', '/'),
            FullPath = fullPath,
            LegacyPath = string.IsNullOrWhiteSpace(legacyPath) ? null : legacyPath.Replace('\\', '/'),
            ContentType = GetContentType(extension),
            Extension = extension,
            ReportId = descriptor.ReportId,
            CapabilityId = descriptor.CapabilityId,
            ProviderId = descriptor.ProviderId,
            PolicyVersion = policyVersion,
            CreatedAt = now,
            UpdatedAt = now,
            SizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : 0,
            ContentHash = File.Exists(fullPath) ? ComputeContentHash(fullPath) : string.Empty,
            IsLatest = isLatest,
            IsSnapshot = isSnapshot,
            SourceCommand = sourceCommand
        };
    }

    public ReportLayoutDiagnostics BuildReportLayoutDiagnostics()
    {
        var manifest = ReadManifestEntries();
        var reports = manifest
            .Where(IsReportManifestEntry)
            .ToArray();
        var diagnostics = new List<string>();
        var missingStandard = reports.Count(entry => !File.Exists(entry.FullPath));
        var missingLegacy = reports.Count(entry =>
            !string.IsNullOrWhiteSpace(entry.LegacyPath)
            && !File.Exists(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, entry.LegacyPath))));
        if (missingStandard > 0)
        {
            diagnostics.Add("MissingStandardArtifacts");
        }

        if (missingLegacy > 0)
        {
            diagnostics.Add("MissingLegacyArtifacts");
        }

        var samples = new[]
        {
            new ReportArtifactDescriptorFactory().CreateSnapshot(
                "learning/feedback/learning-feedback-quality-report.json",
                "default",
                "test"),
            new ReportArtifactDescriptorFactory().CreateSnapshot(
                "learning/readiness/learning-readiness-freeze-report.json",
                "default",
                "test"),
            new ReportArtifactDescriptorFactory().CreateSnapshot(
                "eval/vector-query-shadow-eval-a3.json",
                "default",
                "test"),
            new ReportArtifactDescriptorFactory().CreateSnapshot(
                "eval/graph-expansion-optin-comparison-a3.json",
                "default",
                "test")
        };

        return new ReportLayoutDiagnostics
        {
            DataRoot = RootPath,
            ReportCountByKind = reports
                .GroupBy(entry => entry.ArtifactKind.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
            LatestReportCount = reports.Count(entry => entry.IsLatest),
            LegacyMirroredCount = reports.Count(entry => !string.IsNullOrWhiteSpace(entry.LegacyPath)),
            MissingStandardArtifactCount = missingStandard,
            MissingLegacyArtifactCount = missingLegacy,
            DuplicateContentHashCount = reports
                .Where(entry => !entry.IsLatest && !string.IsNullOrWhiteSpace(entry.ContentHash))
                .GroupBy(entry => entry.ContentHash, StringComparer.OrdinalIgnoreCase)
                .Count(group => group.Count() > 1),
            LargestReports = reports
                .OrderByDescending(entry => entry.SizeBytes)
                .Take(5)
                .ToArray(),
            ManifestCount = manifest.Count,
            ResolvedPathSamples = samples
                .Select(descriptor => CreateManifestEntry(descriptor, ResolveArtifactPath(descriptor)))
                .ToArray(),
            Diagnostics = diagnostics
        };
    }

    public MemoryLayoutDiagnostics BuildMemoryLayoutDiagnostics(string workspaceId, string collectionId)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in MemoryArtifactKinds)
        {
            var descriptor = new ArtifactDescriptor
            {
                Kind = kind,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                ReportId = GetDefaultReportId(kind),
                Extension = ".jsonl"
            };
            paths[kind.ToString()] = GetRelativePath(Path.GetDirectoryName(ResolveArtifactPath(descriptor))!);
        }

        var diagnostics = new List<string>();
        var missingDirectoryCount = paths.Values
            .Select(relativePath => Path.Combine(RootPath, relativePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count(path => !Directory.Exists(path));

        if (missingDirectoryCount > 0)
        {
            diagnostics.Add("MemoryLayoutDirectoriesMissing");
        }

        var temporalReady = paths
            .Where(item => item.Key.StartsWith("MemoryTemporal", StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.Combine(RootPath, item.Value))
            .All(Directory.Exists);

        return new MemoryLayoutDiagnostics
        {
            DataRoot = RootPath,
            WorkspaceId = SanitizeSegment(workspaceId),
            CollectionId = SanitizeSegment(collectionId),
            MemoryLayerPaths = paths,
            ShortTermArtifactCount = CountFiles(paths, "MemoryShortTerm"),
            CandidateArtifactCount = CountFiles(paths, "MemoryCandidate"),
            StableArtifactCount = CountFiles(paths, "MemoryStable"),
            TemporalPlaceholderReady = temporalReady,
            LegacyFallbackCount = CountLegacyMemoryFiles(workspaceId, collectionId),
            MissingDirectoryCount = missingDirectoryCount,
            ManifestCount = File.Exists(GetManifestPath()) ? File.ReadLines(GetManifestPath()).Count() : 0,
            Diagnostics = diagnostics
        };
    }

    public TraceLayoutDiagnostics BuildTraceLayoutDiagnostics(string workspaceId, string collectionId)
    {
        var dateShard = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kind in TraceArtifactKinds)
        {
            var descriptor = new ArtifactDescriptor
            {
                Kind = kind,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                CapabilityId = "diagnostics",
                OperationId = kind == ArtifactKind.TraceToolCall ? "operation-example" : null,
                ReportId = GetDefaultReportId(kind),
                DateShard = dateShard,
                Extension = ".jsonl"
            };
            paths[kind.ToString()] = GetRelativePath(Path.GetDirectoryName(ResolveArtifactPath(descriptor))!);
        }

        var traceRoot = Path.Combine(
            RootPath,
            "workspaces",
            SanitizeSegment(workspaceId),
            "collections",
            SanitizeSegment(collectionId),
            "traces");
        var diagnostics = new List<string>();
        if (!Directory.Exists(traceRoot))
        {
            diagnostics.Add("TraceRootMissing");
        }

        return new TraceLayoutDiagnostics
        {
            DataRoot = RootPath,
            WorkspaceId = SanitizeSegment(workspaceId),
            CollectionId = SanitizeSegment(collectionId),
            TraceRoot = GetRelativePath(traceRoot),
            TraceCategoryPaths = paths,
            RetrievalTraceCount = CountTraceFiles(traceRoot, "retrieval"),
            RouterShadowTraceCount = CountTraceFiles(traceRoot, "router-shadow"),
            RankerShadowTraceCount = CountTraceFiles(traceRoot, "ranker-shadow"),
            GraphShadowTraceCount = CountTraceFiles(traceRoot, "graph-shadow"),
            VectorShadowTraceCount = CountTraceFiles(traceRoot, "vector-shadow"),
            ToolCallPlaceholderReady = Directory.Exists(Path.Combine(traceRoot, "tool-calls")),
            LegacyFallbackCount = CountLegacyTraceFiles(workspaceId, collectionId),
            ManifestCount = File.Exists(GetManifestPath()) ? File.ReadLines(GetManifestPath()).Count() : 0,
            Diagnostics = diagnostics
        };
    }

    private IReadOnlyList<string> BuildRelativeSegments(ArtifactDescriptor descriptor)
    {
        var extension = NormalizeExtension(descriptor.Extension);
        var fileName = BuildFileName(descriptor, extension);
        var capability = SanitizeSegment(descriptor.CapabilityId ?? descriptor.Kind.ToString().ToLowerInvariant());

        if (!RequiresWorkspaceScope(descriptor.Kind)
            && string.IsNullOrWhiteSpace(descriptor.WorkspaceId)
            && string.IsNullOrWhiteSpace(descriptor.CollectionId))
            return descriptor.Kind switch
            {
                ArtifactKind.Eval => ["eval", capability, fileName],
                ArtifactKind.Report => ["reports", capability, fileName],
                ArtifactKind.Trace => ["traces", capability, fileName],
                ArtifactKind.Job => ["jobs", capability, fileName],
                _ => ["system", capability, fileName]
            };
        var workspace = SanitizeSegment(descriptor.WorkspaceId ?? "default");
        var collection = SanitizeSegment(descriptor.CollectionId ?? "default");
        var segments = new List<string>
        {
            "workspaces",
            workspace,
            "collections",
            collection
        };

        segments.AddRange(GetScopedCategorySegments(descriptor, capability));
        segments.Add(fileName);
        return segments;

    }

    private static IReadOnlyList<string> GetScopedCategorySegments(ArtifactDescriptor descriptor, string capability)
    {
        return descriptor.Kind switch
        {
            ArtifactKind.MemoryShort => ["memory", "short-term"],
            ArtifactKind.MemoryCandidate => ["memory", "candidate"],
            ArtifactKind.MemoryStable => ["memory", "stable"],
            ArtifactKind.MemoryShortTermRawEvent => ["memory", "short-term", "raw-events"],
            ArtifactKind.MemoryShortTermWorkingItem => ["memory", "short-term", "working-items"],
            ArtifactKind.MemoryShortTermArchive => ["memory", "short-term", "archive"],
            ArtifactKind.MemoryShortTermCompactionRun => ["memory", "short-term", "compaction"],
            ArtifactKind.MemoryTemporalItem => ["memory", "temporal", "items"],
            ArtifactKind.MemoryTemporalArchive => ["memory", "temporal", "archive"],
            ArtifactKind.MemoryTemporalDiagnostics => ["memory", "temporal", "diagnostics"],
            ArtifactKind.MemoryCandidateItem => ["memory", "candidate", "items"],
            ArtifactKind.MemoryCandidateReview => ["memory", "candidate", "reviews"],
            ArtifactKind.MemoryCandidateDiagnostics => ["memory", "candidate", "diagnostics"],
            ArtifactKind.MemoryCandidateEvidence => ["memory", "candidate", "evidence"],
            ArtifactKind.MemoryStableItem => ["memory", "stable", "items"],
            ArtifactKind.MemoryStableLifecycleReview => ["memory", "stable", "lifecycle-reviews"],
            ArtifactKind.MemoryStableReplacementChain => ["memory", "stable", "replacement-chains"],
            ArtifactKind.MemoryStableProvenance => ["memory", "stable", "provenance"],
            ArtifactKind.MemoryStableDiagnostics => ["memory", "stable", "diagnostics"],
            ArtifactKind.Relation => ["relations"],
            ArtifactKind.Constraint => ["constraints"],
            ArtifactKind.Vector => string.IsNullOrWhiteSpace(descriptor.ProviderId)
                ? string.IsNullOrWhiteSpace(descriptor.CapabilityId) || descriptor.CapabilityId.Equals("vector", StringComparison.OrdinalIgnoreCase)
                    ? ["vector"]
                    : ["vector", SanitizeStatic(descriptor.CapabilityId)]
                : string.IsNullOrWhiteSpace(descriptor.CapabilityId) || descriptor.CapabilityId.Equals("vector", StringComparison.OrdinalIgnoreCase)
                    ? ["vector", SanitizeStatic(descriptor.ProviderId)]
                    : ["vector", SanitizeStatic(descriptor.ProviderId), SanitizeStatic(descriptor.CapabilityId)],
            ArtifactKind.VectorLifecycleMetadataReviewCandidate => ["vector", "lifecycle-metadata-review", "candidates"],
            ArtifactKind.VectorLifecycleMetadataReviewCandidateReport => ["vector", "lifecycle-metadata-review", "reports"],
            ArtifactKind.LearningFeedback => ["learning", "feedback"],
            ArtifactKind.Router => ["learning", "router"],
            ArtifactKind.Ranker => ["learning", "ranker"],
            ArtifactKind.Graph => ["learning", "graph"],
            ArtifactKind.TraceRetrieval => ["traces", "retrieval", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TracePlanning => ["traces", "planning", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceToolCall => ["traces", "tool-calls", SanitizeStatic(descriptor.DateShard), SanitizeStatic(descriptor.OperationId)],
            ArtifactKind.TraceRouterShadow => ["traces", "router-shadow", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceRankerShadow => ["traces", "ranker-shadow", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceVectorShadow => ["traces", "vector-shadow", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceGraphShadow => ["traces", "graph-shadow", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceRelationDualWrite => ["traces", "relation-dual-write", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceRelationShadowRead => ["traces", "relation-shadow-read", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceRelationProviderSwitch => ["traces", "relation-provider-switch", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceLearningFeedbackDualWrite => ["traces", "learning-feedback-dual-write", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceLearningFeedbackShadowRead => ["traces", "learning-feedback-shadow-read", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceLearningFeedbackProviderSwitch => ["traces", "learning-feedback-provider-switch", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceJobQueueDualWrite => ["traces", "job-queue-dual-write", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceJobQueueShadowRead => ["traces", "job-queue-shadow-read", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceJobQueueScopedWorkerCanary => ["traces", "job-queue-scoped-worker-canary", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceJobQueueLimitedWorkerScopeObservation => ["traces", "job-queue-limited-worker-scope-observation", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TracePackageBuild => ["traces", "package-build", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceModelCall => ["traces", "model-calls", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.TraceError => ["traces", "errors", SanitizeStatic(descriptor.DateShard)],
            ArtifactKind.Eval => ["eval", capability],
            ArtifactKind.Trace => ["traces", capability],
            ArtifactKind.Job => ["jobs", capability],
            ArtifactKind.Report => ["reports", capability],
            _ => ["reports", capability]
        };
    }

    private static string BuildFileName(ArtifactDescriptor descriptor, string extension)
    {
        var id = FirstNonEmpty(descriptor.ReportId, descriptor.OperationId, descriptor.CapabilityId, descriptor.Kind.ToString());
        var baseName = SanitizeStatic(id);
        if (!IsTraceKind(descriptor.Kind) && !string.IsNullOrWhiteSpace(descriptor.DateShard))
        {
            baseName = $"{SanitizeStatic(descriptor.DateShard)}-{baseName}";
        }

        return baseName + extension;
    }

    private static bool RequiresWorkspaceScope(ArtifactKind kind)
        => kind is ArtifactKind.MemoryShort
            or ArtifactKind.MemoryCandidate
            or ArtifactKind.MemoryStable
            or ArtifactKind.MemoryShortTermRawEvent
            or ArtifactKind.MemoryShortTermWorkingItem
            or ArtifactKind.MemoryShortTermArchive
            or ArtifactKind.MemoryShortTermCompactionRun
            or ArtifactKind.MemoryTemporalItem
            or ArtifactKind.MemoryTemporalArchive
            or ArtifactKind.MemoryTemporalDiagnostics
            or ArtifactKind.MemoryCandidateItem
            or ArtifactKind.MemoryCandidateReview
            or ArtifactKind.MemoryCandidateDiagnostics
            or ArtifactKind.MemoryCandidateEvidence
            or ArtifactKind.MemoryStableItem
            or ArtifactKind.MemoryStableLifecycleReview
            or ArtifactKind.MemoryStableReplacementChain
            or ArtifactKind.MemoryStableProvenance
            or ArtifactKind.MemoryStableDiagnostics
            or ArtifactKind.Relation
            or ArtifactKind.Constraint
            or ArtifactKind.Vector
            or ArtifactKind.VectorLifecycleMetadataReviewCandidate
            or ArtifactKind.VectorLifecycleMetadataReviewCandidateReport
            or ArtifactKind.LearningFeedback
            or ArtifactKind.Router
            or ArtifactKind.Ranker
            or ArtifactKind.Graph
            or ArtifactKind.TraceRetrieval
            or ArtifactKind.TracePlanning
            or ArtifactKind.TraceToolCall
            or ArtifactKind.TraceRouterShadow
            or ArtifactKind.TraceRankerShadow
            or ArtifactKind.TraceVectorShadow
            or ArtifactKind.TraceGraphShadow
            or ArtifactKind.TraceRelationDualWrite
            or ArtifactKind.TraceRelationShadowRead
            or ArtifactKind.TraceRelationProviderSwitch
            or ArtifactKind.TraceLearningFeedbackDualWrite
            or ArtifactKind.TraceLearningFeedbackShadowRead
            or ArtifactKind.TraceLearningFeedbackProviderSwitch
            or ArtifactKind.TraceJobQueueDualWrite
            or ArtifactKind.TraceJobQueueShadowRead
            or ArtifactKind.TraceJobQueueScopedWorkerCanary
            or ArtifactKind.TraceJobQueueLimitedWorkerScopeObservation
            or ArtifactKind.TracePackageBuild
            or ArtifactKind.TraceModelCall
            or ArtifactKind.TraceError;

    private static ArtifactKind ResolveKindFromLegacySegments(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return ArtifactKind.Report;
        }

        if (segments[0].Equals("learning", StringComparison.OrdinalIgnoreCase) && segments.Count > 1)
        {
            return segments[1].ToLowerInvariant() switch
            {
                "feedback" => ArtifactKind.LearningFeedback,
                "readiness" => ArtifactKind.Report,
                "router" => ArtifactKind.Router,
                "ranker" => ArtifactKind.Ranker,
                _ => ArtifactKind.Report
            };
        }

        if (segments[0].Equals("vector", StringComparison.OrdinalIgnoreCase))
        {
            return ArtifactKind.Vector;
        }

        if (segments[0].Equals("eval", StringComparison.OrdinalIgnoreCase) && segments.Count > 1)
        {
            if (segments[1].StartsWith("vector", StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactKind.Vector;
            }

            if (segments[1].StartsWith("graph", StringComparison.OrdinalIgnoreCase)
                || segments[1].StartsWith("relation-expansion", StringComparison.OrdinalIgnoreCase))
            {
                return ArtifactKind.Graph;
            }

            return ArtifactKind.Eval;
        }

        return ArtifactKind.Report;
    }

    private static readonly ArtifactKind[] MemoryArtifactKinds =
    [
        ArtifactKind.MemoryShortTermRawEvent,
        ArtifactKind.MemoryShortTermWorkingItem,
        ArtifactKind.MemoryShortTermArchive,
        ArtifactKind.MemoryShortTermCompactionRun,
        ArtifactKind.MemoryTemporalItem,
        ArtifactKind.MemoryTemporalArchive,
        ArtifactKind.MemoryTemporalDiagnostics,
        ArtifactKind.MemoryCandidateItem,
        ArtifactKind.MemoryCandidateReview,
        ArtifactKind.MemoryCandidateDiagnostics,
        ArtifactKind.MemoryCandidateEvidence,
        ArtifactKind.MemoryStableItem,
        ArtifactKind.MemoryStableLifecycleReview,
        ArtifactKind.MemoryStableReplacementChain,
        ArtifactKind.MemoryStableProvenance,
        ArtifactKind.MemoryStableDiagnostics
    ];

    private static readonly ArtifactKind[] TraceArtifactKinds =
    [
        ArtifactKind.TraceRetrieval,
        ArtifactKind.TracePlanning,
        ArtifactKind.TraceToolCall,
        ArtifactKind.TraceRouterShadow,
        ArtifactKind.TraceRankerShadow,
        ArtifactKind.TraceVectorShadow,
        ArtifactKind.TraceGraphShadow,
        ArtifactKind.TraceRelationDualWrite,
        ArtifactKind.TraceRelationShadowRead,
        ArtifactKind.TraceRelationProviderSwitch,
        ArtifactKind.TraceLearningFeedbackDualWrite,
        ArtifactKind.TraceLearningFeedbackShadowRead,
        ArtifactKind.TraceLearningFeedbackProviderSwitch,
        ArtifactKind.TraceJobQueueDualWrite,
        ArtifactKind.TraceJobQueueShadowRead,
        ArtifactKind.TraceJobQueueScopedWorkerCanary,
        ArtifactKind.TraceJobQueueLimitedWorkerScopeObservation,
        ArtifactKind.TracePackageBuild,
        ArtifactKind.TraceModelCall,
        ArtifactKind.TraceError
    ];

    private int CountFiles(IReadOnlyDictionary<string, string> paths, string keyPrefix)
        => paths
            .Where(item => item.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(item => Path.Combine(RootPath, item.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .Sum(path => Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Count());

    private int CountLegacyMemoryFiles(string workspaceId, string collectionId)
    {
        var collectionRoot = Path.Combine(
            RootPath,
            "workspaces",
            SanitizeSegment(workspaceId),
            "collections",
            SanitizeSegment(collectionId));
        var legacyPaths = new[]
        {
            Path.Combine(collectionRoot, "short-term", "raw-events.jsonl"),
            Path.Combine(collectionRoot, "short-term", "working-items.jsonl"),
            Path.Combine(collectionRoot, "short-term", "archive", "raw-events.jsonl"),
            Path.Combine(collectionRoot, "short-term", "archive", "working-items.jsonl"),
            Path.Combine(collectionRoot, "short-term", "compact-runs.jsonl"),
            Path.Combine(collectionRoot, "memory", "candidate-memory.jsonl"),
            Path.Combine(collectionRoot, "memory", "candidate-memory-reviews.jsonl"),
            Path.Combine(collectionRoot, "memory", "stable.jsonl"),
            Path.Combine(collectionRoot, "memory", "stable-lifecycle-reviews.jsonl")
        };

        return legacyPaths.Count(File.Exists);
    }

    private static int CountTraceFiles(string traceRoot, string category)
    {
        var root = Path.Combine(traceRoot, category);
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).Count()
            : 0;
    }

    private int CountLegacyTraceFiles(string workspaceId, string collectionId)
    {
        var collectionRoot = Path.Combine(
            RootPath,
            "workspaces",
            SanitizeSegment(workspaceId),
            "collections",
            SanitizeSegment(collectionId));
        var legacyPaths = new[]
        {
            Path.Combine(collectionRoot, "retrieval", "traces.jsonl"),
            Path.Combine(collectionRoot, "packages", "build-traces.jsonl"),
            Path.Combine(collectionRoot, "learning", "router-shadow-traces.jsonl")
        };

        return legacyPaths.Count(File.Exists);
    }

    private IReadOnlyList<ArtifactManifestEntry> ReadManifestEntries()
    {
        var path = GetManifestPath();
        if (!File.Exists(path))
        {
            return Array.Empty<ArtifactManifestEntry>();
        }

        var serializer = new FileFormatSerializer();
        return new FileJsonLineStore(serializer)
            .ReadAsync<ArtifactManifestEntry>(path)
            .GetAwaiter()
            .GetResult();
    }

    private static bool IsReportManifestEntry(ArtifactManifestEntry entry)
        => entry.ArtifactKind is ArtifactKind.Report
            or ArtifactKind.Eval
            or ArtifactKind.Vector
            or ArtifactKind.Graph
            or ArtifactKind.Router
            or ArtifactKind.Ranker
            or ArtifactKind.LearningFeedback
            || entry.Descriptor.Kind is ArtifactKind.Report
                or ArtifactKind.Eval
                or ArtifactKind.Vector
                or ArtifactKind.Graph
                or ArtifactKind.Router
                or ArtifactKind.Ranker
                or ArtifactKind.LearningFeedback;

    private static string GetDefaultReportId(ArtifactKind kind)
        => kind switch
        {
            ArtifactKind.MemoryShortTermRawEvent => "raw-events",
            ArtifactKind.MemoryShortTermWorkingItem => "working-items",
            ArtifactKind.MemoryShortTermArchive => "archive",
            ArtifactKind.MemoryShortTermCompactionRun => "compaction-runs",
            ArtifactKind.MemoryTemporalItem => "temporal-items",
            ArtifactKind.MemoryTemporalArchive => "temporal-archive",
            ArtifactKind.MemoryTemporalDiagnostics => "temporal-diagnostics",
            ArtifactKind.MemoryCandidateItem => "candidate-memory",
            ArtifactKind.MemoryCandidateReview => "candidate-memory-reviews",
            ArtifactKind.MemoryCandidateDiagnostics => "candidate-diagnostics",
            ArtifactKind.MemoryCandidateEvidence => "candidate-evidence",
            ArtifactKind.MemoryStableItem => "stable-memory",
            ArtifactKind.MemoryStableLifecycleReview => "stable-lifecycle-reviews",
            ArtifactKind.MemoryStableReplacementChain => "replacement-chains",
            ArtifactKind.MemoryStableProvenance => "stable-provenance",
            ArtifactKind.MemoryStableDiagnostics => "stable-diagnostics",
            ArtifactKind.VectorLifecycleMetadataReviewCandidate => "lifecycle-metadata-review-candidates",
            ArtifactKind.VectorLifecycleMetadataReviewCandidateReport => "lifecycle-metadata-review-candidate-report",
            ArtifactKind.TraceRetrieval => "retrieval-traces",
            ArtifactKind.TracePlanning => "planning-traces",
            ArtifactKind.TraceToolCall => "tool-call-trace",
            ArtifactKind.TraceRouterShadow => "router-shadow-traces",
            ArtifactKind.TraceRankerShadow => "ranker-shadow-traces",
            ArtifactKind.TraceVectorShadow => "vector-shadow-traces",
            ArtifactKind.TraceGraphShadow => "graph-shadow-traces",
            ArtifactKind.TraceRelationDualWrite => "relation-dual-write-traces",
            ArtifactKind.TraceRelationShadowRead => "relation-shadow-read-traces",
            ArtifactKind.TraceRelationProviderSwitch => "relation-provider-switch-traces",
            ArtifactKind.TraceLearningFeedbackDualWrite => "learning-feedback-dual-write-traces",
            ArtifactKind.TraceLearningFeedbackShadowRead => "learning-feedback-shadow-read-traces",
            ArtifactKind.TraceLearningFeedbackProviderSwitch => "learning-feedback-provider-switch-traces",
            ArtifactKind.TracePackageBuild => "package-build-traces",
            ArtifactKind.TraceModelCall => "model-call-traces",
            ArtifactKind.TraceError => "error-traces",
            _ => kind.ToString()
        };

    private static bool IsTraceKind(ArtifactKind kind)
        => kind is ArtifactKind.TraceRetrieval
            or ArtifactKind.TracePlanning
            or ArtifactKind.TraceToolCall
            or ArtifactKind.TraceRouterShadow
            or ArtifactKind.TraceRankerShadow
            or ArtifactKind.TraceVectorShadow
            or ArtifactKind.TraceGraphShadow
            or ArtifactKind.TraceRelationDualWrite
            or ArtifactKind.TraceRelationShadowRead
            or ArtifactKind.TraceRelationProviderSwitch
            or ArtifactKind.TraceLearningFeedbackDualWrite
            or ArtifactKind.TraceLearningFeedbackShadowRead
            or ArtifactKind.TraceLearningFeedbackProviderSwitch
            or ArtifactKind.TracePackageBuild
            or ArtifactKind.TraceModelCall
            or ArtifactKind.TraceError;

    private static string ResolveCapabilityFromLegacySegments(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0)
        {
            return "reports";
        }

        if (segments[0].Equals("learning", StringComparison.OrdinalIgnoreCase) && segments.Count > 1)
        {
            return segments[1].ToLowerInvariant();
        }

        if (segments[0].Equals("vector", StringComparison.OrdinalIgnoreCase))
        {
            return segments.Count > 1 ? segments[1].ToLowerInvariant() : "vector";
        }

        if (segments[0].Equals("eval", StringComparison.OrdinalIgnoreCase) && segments.Count > 1)
        {
            var file = Path.GetFileNameWithoutExtension(segments[^1]);
            if (file.Contains("vector", StringComparison.OrdinalIgnoreCase))
            {
                return "vector";
            }

            if (file.Contains("graph", StringComparison.OrdinalIgnoreCase)
                || file.Contains("relation-expansion", StringComparison.OrdinalIgnoreCase))
            {
                return "graph";
            }

            if (file.Contains("p15", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("eval-report", StringComparison.OrdinalIgnoreCase)
                || file.StartsWith("extended-failure-triage", StringComparison.OrdinalIgnoreCase))
            {
                return "p15";
            }

            return "eval";
        }

        return segments[0].ToLowerInvariant();
    }

    private static string? ResolveProviderFromLegacySegments(IReadOnlyList<string> segments)
        => segments.Any(segment => segment.Contains("onnx", StringComparison.OrdinalIgnoreCase))
            ? "onnx-local"
            : null;

    private static string CreateArtifactId(ArtifactDescriptor descriptor)
    {
        var raw = string.Join(
            "|",
            descriptor.Kind,
            descriptor.WorkspaceId,
            descriptor.CollectionId,
            descriptor.MemoryLayer,
            descriptor.CapabilityId,
            descriptor.ProviderId,
            descriptor.OperationId,
            descriptor.ReportId,
            descriptor.DateShard,
            NormalizeExtension(descriptor.Extension));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "artifact";

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return ".json";
        }

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension.ToLowerInvariant()
            : "." + extension.ToLowerInvariant();
    }

    private static string GetContentType(string extension)
        => extension.ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".jsonl" => "application/x-jsonlines",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };

    private static string ComputeContentHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeStatic(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "default";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            builder.Append(char.IsControl(ch) || InvalidSegmentCharacters.Contains(ch) ? '-' : ch);
        }

        var sanitized = builder.ToString().Trim('.', ' ', '-');
        if (sanitized.Length == 0)
        {
            return "default";
        }

        return sanitized.Length <= 96 ? sanitized : sanitized[..96];
    }

    private void EnsureInsideRoot(string fullPath)
    {
        var normalized = Path.GetFullPath(fullPath);
        if (!EnsureTrailingSeparator(normalized).StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(normalized, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"artifact path escaped data root: {fullPath}");
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.EndsWith(Path.DirectorySeparatorChar)
            ? fullPath
            : fullPath + Path.DirectorySeparatorChar;
    }
}
