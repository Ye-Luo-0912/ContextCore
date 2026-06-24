namespace ContextCore.ControlRoom.Models;

public sealed class ControlRoomReportDescriptor
{
    public string ReportId { get; init; } = string.Empty;
    public string DisplayTitle { get; init; } = string.Empty;
    public string? PrimaryPath { get; init; }
    public string? GatePath { get; init; }
    public string? PlanningPath { get; init; }
    public string? DecisionPath { get; init; }
    public string? PhaseGroup { get; init; }
    public string? EvalGateCommand { get; init; }
    public string? EvalPlanCommand { get; init; }

    public string[] AllPaths()
    {
        var paths = new List<string>();
        if (PrimaryPath is not null) paths.Add(PrimaryPath);
        if (GatePath is not null) paths.Add(GatePath);
        if (PlanningPath is not null) paths.Add(PlanningPath);
        if (DecisionPath is not null) paths.Add(DecisionPath);
        return paths.ToArray();
    }

    public string DefaultMissingStatus() => $"No{ReportId}Report";

    public string DefaultEvalCommand() => EvalGateCommand ?? EvalPlanCommand ?? $"eval vector-{ReportId}-gate";
}
