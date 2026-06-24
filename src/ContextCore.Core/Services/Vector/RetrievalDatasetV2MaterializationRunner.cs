using System.Security.Cryptography;
using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// Retrieval Dataset V2 物化与不可变性 gate。只校验 artifact，不参与正式检索。
/// </summary>
public sealed class RetrievalDatasetV2MaterializationRunner
{
    public const string GeneratorVersion = "retrieval-dataset-v2-generator/v1";
    public const string ContractVersion = "retrieval-dataset-v2";

    public RetrievalDatasetV2Manifest BuildManifest(
        string corpusPath,
        string samplesPath,
        int corpusItemCount,
        int sampleCount,
        string corpusHash,
        string samplesHash)
    {
        var datasetId = $"rdsv2-{BuildShortHash($"{corpusHash}\u001f{samplesHash}\u001f{corpusItemCount}\u001f{sampleCount}")}";
        return new RetrievalDatasetV2Manifest
        {
            DatasetId = datasetId,
            CorpusPath = NormalizePath(corpusPath),
            SamplesPath = NormalizePath(samplesPath),
            CorpusItemCount = corpusItemCount,
            SampleCount = sampleCount,
            CorpusHash = corpusHash,
            SamplesHash = samplesHash,
            GeneratorVersion = GeneratorVersion,
            ContractVersion = ContractVersion,
            CreatedAt = DateTimeOffset.UtcNow,
            UseForRuntime = false,
            FormalRetrievalAllowed = false
        };
    }

    public RetrievalDatasetV2MaterializationReport BuildReport(
        RetrievalDatasetV2Manifest manifest,
        RetrievalDatasetV2ValidationReport? validation,
        RetrievalDatasetV2QualityReport? quality,
        RetrievalDatasetV2Manifest? existingManifest,
        bool corpusExists,
        bool samplesExists,
        bool requireExistingManifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var validatePassed = validation is not null && validation.IssueCount == 0;
        var qualityReady = quality is not null
            && string.Equals(
                quality.Recommendation,
                RetrievalDatasetV2GenerationRecommendations.ReadyForDatasetV2ShadowEval,
                StringComparison.OrdinalIgnoreCase);
        var corpusHashStable = existingManifest is null
            || string.Equals(existingManifest.CorpusHash, manifest.CorpusHash, StringComparison.OrdinalIgnoreCase);
        var samplesHashStable = existingManifest is null
            || string.Equals(existingManifest.SamplesHash, manifest.SamplesHash, StringComparison.OrdinalIgnoreCase);

        var blocked = new List<string>();
        if (!corpusExists || !samplesExists)
        {
            blocked.Add("MissingMaterializedDatasetArtifact");
        }

        if (requireExistingManifest && existingManifest is null)
        {
            blocked.Add("MissingDatasetManifest");
        }

        if (!validatePassed)
        {
            blocked.Add("ValidationNotPassed");
        }

        if (!qualityReady)
        {
            blocked.Add("QualityNotReadyForShadowEval");
        }

        if (!corpusHashStable || !samplesHashStable)
        {
            blocked.Add("DatasetHashChangedFromManifest");
        }

        if (manifest.UseForRuntime || manifest.FormalRetrievalAllowed)
        {
            blocked.Add("RuntimeOrFormalRetrievalEnabled");
        }

        if (validation is not null)
        {
            if (validation.MissingEvidenceRefsCount > 0)
            {
                blocked.Add("MissingEvidence");
            }

            if (validation.MissingProvenanceCount > 0)
            {
                blocked.Add("MissingProvenance");
            }

            if (validation.QueryItemIdLeakCount > 0)
            {
                blocked.Add("ItemIdLeakage");
            }

            if (validation.RelationEvidenceMissingCount > 0)
            {
                blocked.Add("RelationInconsistency");
            }
        }

        var gatePassed = blocked.Count == 0;
        return new RetrievalDatasetV2MaterializationReport
        {
            OperationId = $"retrieval-dataset-v2-materialization-gate-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            DatasetId = manifest.DatasetId,
            CorpusPath = manifest.CorpusPath,
            SamplesPath = manifest.SamplesPath,
            CorpusItemCount = manifest.CorpusItemCount,
            SampleCount = manifest.SampleCount,
            CorpusHash = manifest.CorpusHash,
            SamplesHash = manifest.SamplesHash,
            GeneratorVersion = manifest.GeneratorVersion,
            ContractVersion = manifest.ContractVersion,
            CorpusExists = corpusExists,
            SamplesExists = samplesExists,
            ManifestExists = existingManifest is not null,
            ValidatePassed = validatePassed,
            QualityRecommendation = quality?.Recommendation ?? string.Empty,
            CorpusHashStable = corpusHashStable,
            SamplesHashStable = samplesHashStable,
            ValidationIssueCount = validation?.IssueCount ?? -1,
            MissingEvidenceCount = validation?.MissingEvidenceRefsCount ?? -1,
            MissingProvenanceCount = validation?.MissingProvenanceCount ?? -1,
            ItemIdLeakageCount = validation?.QueryItemIdLeakCount ?? -1,
            RelationInconsistencyCount = validation?.RelationEvidenceMissingCount ?? -1,
            UseForRuntime = manifest.UseForRuntime,
            FormalRetrievalAllowed = manifest.FormalRetrievalAllowed,
            GatePassed = gatePassed,
            Recommendation = ResolveRecommendation(blocked),
            BlockedReasons = blocked
        };
    }

    public static string BuildMarkdown(RetrievalDatasetV2MaterializationReport report, string title)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine();
        builder.AppendLine($"- DatasetId: `{report.DatasetId}`");
        builder.AppendLine($"- CorpusPath: `{report.CorpusPath}`");
        builder.AppendLine($"- SamplesPath: `{report.SamplesPath}`");
        builder.AppendLine($"- CorpusItemCount: `{report.CorpusItemCount}`");
        builder.AppendLine($"- SampleCount: `{report.SampleCount}`");
        builder.AppendLine($"- CorpusHash: `{report.CorpusHash}`");
        builder.AppendLine($"- SamplesHash: `{report.SamplesHash}`");
        builder.AppendLine($"- GeneratorVersion: `{report.GeneratorVersion}`");
        builder.AppendLine($"- ContractVersion: `{report.ContractVersion}`");
        builder.AppendLine($"- CorpusExists: `{report.CorpusExists}`");
        builder.AppendLine($"- SamplesExists: `{report.SamplesExists}`");
        builder.AppendLine($"- ManifestExists: `{report.ManifestExists}`");
        builder.AppendLine($"- ValidatePassed: `{report.ValidatePassed}`");
        builder.AppendLine($"- QualityRecommendation: `{report.QualityRecommendation}`");
        builder.AppendLine($"- CorpusHashStable: `{report.CorpusHashStable}`");
        builder.AppendLine($"- SamplesHashStable: `{report.SamplesHashStable}`");
        builder.AppendLine($"- ValidationIssueCount: `{report.ValidationIssueCount}`");
        builder.AppendLine($"- MissingEvidenceCount: `{report.MissingEvidenceCount}`");
        builder.AppendLine($"- MissingProvenanceCount: `{report.MissingProvenanceCount}`");
        builder.AppendLine($"- ItemIdLeakageCount: `{report.ItemIdLeakageCount}`");
        builder.AppendLine($"- RelationInconsistencyCount: `{report.RelationInconsistencyCount}`");
        builder.AppendLine($"- UseForRuntime: `{report.UseForRuntime}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- GatePassed: `{report.GatePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- BlockedReasons: `{string.Join(", ", report.BlockedReasons)}`");
        return builder.ToString();
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ResolveRecommendation(IReadOnlyCollection<string> blocked)
    {
        if (blocked.Count == 0)
        {
            return RetrievalDatasetV2MaterializationRecommendations.ReadyForDatasetV2ShadowEval;
        }

        if (blocked.Contains("MissingMaterializedDatasetArtifact", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2MaterializationRecommendations.BlockedByMissingArtifact;
        }

        if (blocked.Contains("MissingDatasetManifest", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2MaterializationRecommendations.BlockedByMissingArtifact;
        }

        if (blocked.Contains("DatasetHashChangedFromManifest", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2MaterializationRecommendations.BlockedByHashInstability;
        }

        if (blocked.Contains("RuntimeOrFormalRetrievalEnabled", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2MaterializationRecommendations.BlockedByRuntimeUse;
        }

        if (blocked.Contains("ValidationNotPassed", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("MissingEvidence", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("MissingProvenance", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("ItemIdLeakage", StringComparer.OrdinalIgnoreCase)
            || blocked.Contains("RelationInconsistency", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2MaterializationRecommendations.BlockedByValidationIssues;
        }

        if (blocked.Contains("QualityNotReadyForShadowEval", StringComparer.OrdinalIgnoreCase))
        {
            return RetrievalDatasetV2MaterializationRecommendations.BlockedByQualityGate;
        }

        return RetrievalDatasetV2MaterializationRecommendations.KeepPreviewOnly;
    }

    private static string BuildShortHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(Directory.GetCurrentDirectory());
        var relative = Path.GetRelativePath(root, fullPath);
        return relative.StartsWith("..", StringComparison.Ordinal)
            ? fullPath.Replace('\\', '/')
            : relative.Replace('\\', '/');
    }
}
