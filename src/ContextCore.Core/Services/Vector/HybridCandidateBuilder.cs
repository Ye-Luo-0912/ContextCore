using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Core.Services;

/// <summary>
/// hybrid retrieval 候选构造共享逻辑；dense / lexical / anchor 三路统一走 eligibility policy，
/// 不读 eval 标签、不特判 sampleId/itemId、不依赖 fixture/domain 词表。
/// </summary>
internal static class HybridCandidateBuilder
{
    /// <summary>从 entry + score + eligibility 构造候选，与 VectorQueryPreviewService.ToCandidate 等价。</summary>
    public static VectorQueryPreviewCandidate Build(
        VectorIndexEntry entry,
        double score,
        int rank,
        int rawRank,
        VectorQueryProfile profile,
        VectorCandidateEligibilityPolicy eligibilityPolicy,
        IReadOnlyList<string> diagnosticTypes,
        string candidateSource)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(eligibilityPolicy);
        diagnosticTypes ??= Array.Empty<string>();

        var eligibility = eligibilityPolicy.Evaluate(profile, entry, score, diagnosticTypes);

        return new VectorQueryPreviewCandidate
        {
            CandidateId = entry.ItemId,
            EntryId = entry.EntryId,
            ItemId = entry.ItemId,
            ItemKind = entry.ItemKind,
            Layer = entry.Layer,
            Rank = rank,
            RawRank = rawRank,
            Similarity = score,
            ContentHash = entry.ContentHash,
            EmbeddingModel = entry.EmbeddingModel,
            EmbeddingProvider = entry.EmbeddingProvider,
            Dimension = entry.Dimension,
            IsDuplicate = diagnosticTypes.Contains(VectorIndexDiagnosticTypes.DuplicateVectorEntry, StringComparer.OrdinalIgnoreCase),
            IsStale = diagnosticTypes.Contains(VectorIndexDiagnosticTypes.StaleEmbedding, StringComparer.OrdinalIgnoreCase)
                      || diagnosticTypes.Contains(VectorIndexDiagnosticTypes.ContentHashMismatch, StringComparer.OrdinalIgnoreCase),
            IsOrphan = diagnosticTypes.Contains(VectorIndexDiagnosticTypes.OrphanVectorEntry, StringComparer.OrdinalIgnoreCase),
            IsLifecycleRisk = IsLifecycleRisk(entry),
            Diagnostics = diagnosticTypes,
            EligibilityStatus = eligibility.EligibilityStatus,
            BlockedReasons = eligibility.BlockedReasons,
            TargetSection = eligibility.TargetSection,
            RiskIfNormalSelected = eligibility.RiskIfNormalSelected,
            RiskAfterPolicy = eligibility.RiskAfterPolicy,
            Metadata = new Dictionary<string, string>(entry.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["candidateSource"] = candidateSource
            }
        };
    }

    /// <summary>判断 entry 是否携带 deprecated/historical/rejected/superseded 生命周期风险。</summary>
    public static bool IsLifecycleRisk(VectorIndexEntry entry)
    {
        var lifecycle = new VectorSourceLifecycleMetadataResolver().Resolve(entry);
        return lifecycle.IsDeprecated
               || lifecycle.IsRejected
               || lifecycle.IsSuperseded
               || lifecycle.IsHistorical;
    }

    /// <summary>
    /// 通用查询分词；ASCII（含 - .）长度 ≥ 2 的 token + CJK bigram。
    /// 与 RetrievalCandidatePolicy.SplitQueryTerms 等价，但不依赖 fixture/domain 词表。
    /// </summary>
    public static string[] TokenizeQuery(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return [];
        }

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = new StringBuilder();
        foreach (var ch in queryText)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '.')
            {
                current.Append(ch);
            }
            else
            {
                if (current.Length >= 2)
                {
                    terms.Add(current.ToString());
                }

                current.Clear();
            }
        }

        if (current.Length >= 2)
        {
            terms.Add(current.ToString());
        }

        for (var i = 0; i < queryText.Length - 1; i++)
        {
            if (IsCjkChar(queryText[i]) && IsCjkChar(queryText[i + 1]))
            {
                terms.Add(queryText.Substring(i, 2));
            }
        }

        return [.. terms];
    }

    private static bool IsCjkChar(char ch) => ch is >= '\u4E00' and <= '\u9FFF';
}
