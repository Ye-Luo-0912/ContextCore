namespace ContextCore.ControlRoom.Services;

/// <summary>控制室用户输入动作的枚举类型。</summary>
public enum ControlRoomActionKind
{
    Refresh,
    ToggleAutoRefresh,
    Workspace,
    Collection,
    OpenContextExplorer,
    OpenMemoryLayers,
    OpenPackagePreview,
    OpenJobs,
    OpenRelations,
    OpenConstraints,
    OpenIndexSearch,
    OpenRetrievalDebug,
    OpenServiceDashboard,
    OpenServiceIngest,
    OpenServiceQuery,
    OpenServicePackage,
    OpenServiceJobs,
    OpenServiceModel,
    OpenServiceAdminRuntime,
    OpenServiceMemory,
    OpenServiceConstraints,
    OpenServiceRelations,
    OpenServicePolicies,
    OpenServiceShortTermMemory,
    OpenServicePromotionCandidates,
    OpenServiceStableReviewCandidates,
    OpenServiceLearning,
    OpenServicePolicyFeedback,
    OpenServiceLearningFeatures,
    OpenServicePlanningSnapshot,
    OpenServicePlanningProposal,
    OpenServiceConstraintGaps,
    OpenServiceCandidateConstraints,
    OpenServiceRankerShadowDebug,
    OpenServiceCandidateMemory,
    OpenServiceStableMemory,
    OpenModelStatus,
    OpenReports,
    OpenPolicies,
    OpenEvalReport,
    Back,
    Quit,
    Unknown,
    Value
}

/// <summary>表示一条经解析的用户输入动作。</summary>
public sealed class ControlRoomInputAction
{
    public ControlRoomActionKind Kind { get; init; }

    public string? Value { get; init; }
}

/// <summary>提供将控制台输入解析为 <see cref="ControlRoomInputAction"/> 的静态工具方法。</summary>
public static class ControlRoomInteraction
{
    public static ControlRoomInputAction InterpretDashboardInput(string? input)
    {
        var normalized = Normalize(input);
        return normalized switch
        {
            "" => Action(ControlRoomActionKind.Refresh),
            "r" => Action(ControlRoomActionKind.Refresh),
            "a" => Action(ControlRoomActionKind.ToggleAutoRefresh),
            "w" => Action(ControlRoomActionKind.Workspace),
            "c" => Action(ControlRoomActionKind.Collection),
            "1" => Action(ControlRoomActionKind.OpenContextExplorer),
            "2" => Action(ControlRoomActionKind.OpenMemoryLayers),
            "3" => Action(ControlRoomActionKind.OpenPackagePreview),
            "4" => Action(ControlRoomActionKind.OpenJobs),
            "5" => Action(ControlRoomActionKind.OpenRelations),
            "6" => Action(ControlRoomActionKind.OpenConstraints),
            "7" => Action(ControlRoomActionKind.OpenIndexSearch),
            "d" => Action(ControlRoomActionKind.OpenRetrievalDebug),
            "8" => Action(ControlRoomActionKind.OpenRetrievalDebug),
            "s" => Action(ControlRoomActionKind.OpenServiceDashboard),
            "13" => Action(ControlRoomActionKind.OpenServiceDashboard),
            "i" => Action(ControlRoomActionKind.OpenServiceIngest),
            "14" => Action(ControlRoomActionKind.OpenServiceIngest),
            "g" => Action(ControlRoomActionKind.OpenServiceQuery),
            "15" => Action(ControlRoomActionKind.OpenServiceQuery),
            "v" => Action(ControlRoomActionKind.OpenServicePackage),
            "16" => Action(ControlRoomActionKind.OpenServicePackage),
            "j" => Action(ControlRoomActionKind.OpenServiceJobs),
            "17" => Action(ControlRoomActionKind.OpenServiceJobs),
            "m" => Action(ControlRoomActionKind.OpenServiceModel),
            "18" => Action(ControlRoomActionKind.OpenServiceModel),
            "u" => Action(ControlRoomActionKind.OpenServiceAdminRuntime),
            "19" => Action(ControlRoomActionKind.OpenServiceAdminRuntime),
            "y" => Action(ControlRoomActionKind.OpenServiceMemory),
            "20" => Action(ControlRoomActionKind.OpenServiceMemory),
            "k" => Action(ControlRoomActionKind.OpenServiceConstraints),
            "21" => Action(ControlRoomActionKind.OpenServiceConstraints),
            "l" => Action(ControlRoomActionKind.OpenServiceRelations),
            "22" => Action(ControlRoomActionKind.OpenServiceRelations),
            "o" => Action(ControlRoomActionKind.OpenServicePolicies),
            "23" => Action(ControlRoomActionKind.OpenServicePolicies),
            "t" => Action(ControlRoomActionKind.OpenServiceShortTermMemory),
            "24" => Action(ControlRoomActionKind.OpenServiceShortTermMemory),
            "n" => Action(ControlRoomActionKind.OpenServicePromotionCandidates),
            "25" => Action(ControlRoomActionKind.OpenServicePromotionCandidates),
            "z" => Action(ControlRoomActionKind.OpenServiceStableReviewCandidates),
            "27" => Action(ControlRoomActionKind.OpenServiceStableReviewCandidates),
            "h" => Action(ControlRoomActionKind.OpenServiceLearning),
            "26" => Action(ControlRoomActionKind.OpenServiceLearning),
            "32" => Action(ControlRoomActionKind.OpenServicePolicyFeedback),
            "33" => Action(ControlRoomActionKind.OpenServiceLearningFeatures),
            "x" => Action(ControlRoomActionKind.OpenServicePlanningSnapshot),
            "28" => Action(ControlRoomActionKind.OpenServicePlanningSnapshot),
            "f" => Action(ControlRoomActionKind.OpenServicePlanningProposal),
            "29" => Action(ControlRoomActionKind.OpenServicePlanningProposal),
            "30" => Action(ControlRoomActionKind.OpenServiceConstraintGaps),
            "31" => Action(ControlRoomActionKind.OpenServiceCandidateConstraints),
            "34" => Action(ControlRoomActionKind.OpenServiceRankerShadowDebug),
            "35" => Action(ControlRoomActionKind.OpenServiceCandidateMemory),
            "36" => Action(ControlRoomActionKind.OpenServiceStableMemory),
            "9" => Action(ControlRoomActionKind.OpenModelStatus),
            "10" => Action(ControlRoomActionKind.OpenReports),
            "p" => Action(ControlRoomActionKind.OpenPolicies),
            "11" => Action(ControlRoomActionKind.OpenPolicies),
            "12" => Action(ControlRoomActionKind.OpenEvalReport),
            "e" => Action(ControlRoomActionKind.OpenEvalReport),
            "q" => Action(ControlRoomActionKind.Quit),
            _ => Action(ControlRoomActionKind.Unknown, input)
        };
    }

    public static ControlRoomInputAction InterpretDetailInput(string? input)
    {
        var normalized = Normalize(input);
        return normalized switch
        {
            "" => Action(ControlRoomActionKind.Refresh),
            "r" => Action(ControlRoomActionKind.Refresh),
            "0" => Action(ControlRoomActionKind.Back),
            "b" => Action(ControlRoomActionKind.Back),
            "q" => Action(ControlRoomActionKind.Quit),
            _ => Action(ControlRoomActionKind.Value, input)
        };
    }

    private static ControlRoomInputAction Action(ControlRoomActionKind kind, string? value = null)
    {
        return new ControlRoomInputAction
        {
            Kind = kind,
            Value = value
        };
    }

    private static string Normalize(string? input)
    {
        return (input ?? string.Empty).Trim().ToLowerInvariant();
    }
}
