namespace ContextCore.Abstractions.Models;

/// <summary>
/// R13.2 #4：包构建读路径的查询计划，记录各 store 的实际调用次数与去重命中。
/// 用于验证 R13.2 #1（merged constraint 去重）与 #3（current_task 并行）的效果，
/// 并为后续 R13.3 Store Capability Model 与 R13-F Cache Canary Freeze 验收提供可观察指标。
/// </summary>
public sealed class PackageReadPlan
{
    /// <summary>
    /// 按 store kind + 用途分组的调用次数。Key 形如 "ContextStore.Query"、"ConstraintStore.Query(Hard)"、"MemoryStore.Query(Working)"。
    /// Value 为该调用的执行次数（已去重后的实际执行次数，不含被复用结果跳过的查询）。
    /// </summary>
    public IReadOnlyDictionary<string, int> StoreCallCounts { get; init; } = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// 去重命中的查询数：因 R13.2 #1 合并 section 与 merged section 的 Hard/Soft 查询而跳过的冗余调用次数。
    /// </summary>
    public int DedupHits { get; init; }

    /// <summary>
    /// 所有 store 调用次数总和（StoreCallCounts.Values 的和）。
    /// </summary>
    public int TotalStoreCalls
    {
        get
        {
            var sum = 0;
            foreach (var value in StoreCallCounts.Values)
            {
                sum += value;
            }
            return sum;
        }
    }
}
