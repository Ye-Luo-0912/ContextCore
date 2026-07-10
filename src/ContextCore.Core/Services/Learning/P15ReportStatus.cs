namespace ContextCore.Core.Services;

/// <summary>
/// P3-01：P15 评估报告状态。从 ContextCoreFoundationFreezeRunner 提取到 Core，
/// 供 FoundationStatusService 与 Evaluation 项目共用。
/// </summary>
public readonly record struct P15ReportStatus(
    bool Passed,
    int TotalSamples,
    int FailedSamples,
    int InvalidSamples,
    string Status);
