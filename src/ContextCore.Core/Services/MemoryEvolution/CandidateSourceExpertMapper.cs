using ContextCore.Abstractions;

namespace ContextCore.Core.Services.MemoryEvolution;

/// <summary>
/// ContextCandidateSource → RetrievalExpert 映射。
/// GlobalContext / RelatedContext / Unknown 映射到 RetrievalExpert.Unknown
/// （这两个 source 不是 Expert，而是 的特殊候选类别）。
/// </summary>
internal static class CandidateSourceExpertMapper
{
    /// <summary>把 ContextCandidateSource 映射到 RetrievalExpert。</summary>
    public static RetrievalExpert MapToExpert(ContextCandidateSource source)
    {
        return source switch
        {
            ContextCandidateSource.Mandatory => RetrievalExpert.Mandatory,
            ContextCandidateSource.Lexical => RetrievalExpert.Lexical,
            ContextCandidateSource.Semantic => RetrievalExpert.Semantic,
            ContextCandidateSource.WorkingMemory => RetrievalExpert.WorkingMemory,
            ContextCandidateSource.StableMemory => RetrievalExpert.StableMemory,
            ContextCandidateSource.Graph => RetrievalExpert.Graph,
            ContextCandidateSource.Recency => RetrievalExpert.Recency,
            ContextCandidateSource.Constraint => RetrievalExpert.Constraint,
            // GlobalContext / RelatedContext / Unknown 不是 Expert
            _ => RetrievalExpert.Unknown
        };
    }
}
