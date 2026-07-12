namespace ContextCore.ControlRoom.Models;

/// <summary>
/// ControlRoom 报告描述符注册表。
/// P1-4 清理：删除 21 个无外部消费者的 V5/OPT descriptor，仅保留实际被
/// ControlRoomService.Storage 引用的 2 个 OPT descriptor。
/// </summary>
public static class ReportSummaryRegistry
{
    public static readonly ControlRoomReportDescriptor OPTArchitectureCleanupFreeze = new()
    {
        ReportId = "ArchitectureCleanupFreeze",
        DisplayTitle = "OPT Architecture Cleanup Freeze",
        PrimaryPath = "eval/architecture-cleanup-freeze.json",
        GatePath = "eval/architecture-cleanup-freeze-gate.json",
        PhaseGroup = "OPT",
        EvalGateCommand = "eval architecture-cleanup-freeze",
    };

    public static readonly ControlRoomReportDescriptor OPTArchitectureCleanupFreezeGate = new()
    {
        ReportId = "ArchitectureCleanupFreezeGate",
        DisplayTitle = "OPT Architecture Cleanup Freeze Gate",
        PrimaryPath = "eval/architecture-cleanup-freeze-gate.json",
        PhaseGroup = "OPT",
        EvalGateCommand = "eval architecture-cleanup-freeze-gate",
    };
}
