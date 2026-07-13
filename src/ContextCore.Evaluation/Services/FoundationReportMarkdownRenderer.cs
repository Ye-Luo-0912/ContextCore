using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Services;

/// <summary>
/// 历史产物解析 Markdown 渲染器；将冻结报告渲染为只读 Markdown 文本。
/// </summary>
public static class FoundationReportMarkdownRenderer
{
    public static string BuildContractMarkdown(FoundationApiContractReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service API Contract Freeze");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine($"- ContractPassed: `{report.ContractPassed}`");
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- EndpointCount: `{report.EndpointCount}`");
        builder.AppendLine($"- ClientMethodCount: `{report.ClientMethodCount}`");
        builder.AppendLine($"- EnvelopeSchemaVersion: `{report.EnvelopeSchemaVersion}`");
        builder.AppendLine($"- AuthMode: `{report.AuthMode}`");
        builder.AppendLine($"- AuthConfigured: `{report.AuthConfigured}`");
        builder.AppendLine($"- ProductionMode: `{report.ProductionMode}`");
        builder.AppendLine($"- DegradedBehaviorStable: `{report.DegradedBehaviorStable}`");
        builder.AppendLine($"- ReportNavigationSchemaStable: `{report.ReportNavigationSchemaStable}`");
        builder.AppendLine($"- ForbiddenActionsExposed: `{report.ForbiddenActionsExposed}`");
        builder.AppendLine($"- SecretLeakDetected: `{report.SecretLeakDetected}`");
        builder.AppendLine($"- AbsolutePathLeakDetected: `{report.AbsolutePathLeakDetected}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- FormalPackageWritten: `{report.FormalPackageWritten}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine($"- RuntimeMutated: `{report.RuntimeMutated}`");
        builder.AppendLine();
        builder.AppendLine("## Endpoints");
        foreach (var endpoint in report.Endpoints)
        {
            builder.AppendLine($"- `{endpoint.Method} {endpoint.Route}` -> `{endpoint.ResponseType}` envelope=`{endpoint.UsesEnvelope}` readOnly=`{endpoint.ReadOnly}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Client Methods");
        foreach (var method in report.ClientMethods)
        {
            builder.AppendLine($"- `{method.MethodName}` -> `{method.Route}` response=`{method.ResponseType}` envelope=`{method.DeserializesEnvelope}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Envelope Schema");
        AppendList(builder, report.EnvelopeSchemaFields);
        builder.AppendLine();
        builder.AppendLine("## Report Navigation Schema");
        AppendList(builder, report.ReportNavigationSchemaFields);
        builder.AppendLine();
        builder.AppendLine("## Forbidden Actions");
        AppendList(builder, report.ForbiddenActions);
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("This contract is read-only and does not allow runtime switch, formal retrieval, formal package write, PackingPolicy integration, or package output mutation.");
        return builder.ToString();
    }

    public static string BuildServiceFoundationFreezeMarkdown(ServiceFoundationFreezeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Service Foundation Freeze Gate");
        builder.AppendLine();
        builder.AppendLine($"Generated: `{report.GeneratedAt:O}`");
        builder.AppendLine($"OperationId: `{report.OperationId}`");
        builder.AppendLine();
        builder.AppendLine("## Summary");
        builder.AppendLine();
        builder.AppendLine($"- FreezePassed: `{report.FreezePassed}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine($"- ServiceFoundation: `{report.ServiceFoundation}`");
        builder.AppendLine($"- FoundationApi: `{report.FoundationApi}`");
        builder.AppendLine($"- OpenApiContract: `{report.OpenApiContract}`");
        builder.AppendLine($"- AuthDeploymentProfile: `{report.AuthDeploymentProfile}`");
        builder.AppendLine($"- NextAllowedPhase: `{report.NextAllowedPhase}`");
        builder.AppendLine();
        builder.AppendLine("## Phase Gates");
        builder.AppendLine();
        foreach (var phase in report.PhaseStatuses)
        {
            builder.AppendLine($"- {phase.Key}: `{phase.Value}`");
        }

        builder.AppendLine();
        builder.AppendLine("## Boundary");
        builder.AppendLine();
        builder.AppendLine($"- RuntimeMutationAllowed: `{report.RuntimeMutationAllowed}`");
        builder.AppendLine($"- FormalRetrievalAllowed: `{report.FormalRetrievalAllowed}`");
        builder.AppendLine($"- RuntimeSwitchAllowed: `{report.RuntimeSwitchAllowed}`");
        builder.AppendLine($"- ReadyForRuntimeSwitch: `{report.ReadyForRuntimeSwitch}`");
        builder.AppendLine($"- PackingPolicyChanged: `{report.PackingPolicyChanged}`");
        builder.AppendLine($"- PackageOutputChanged: `{report.PackageOutputChanged}`");
        builder.AppendLine();
        builder.AppendLine("## Service Signals");
        builder.AppendLine();
        builder.AppendLine($"- HostedSmokeRecommendation: `{report.HostedSmokeRecommendation}`");
        builder.AppendLine($"- AuthDeploymentRecommendation: `{report.AuthDeploymentRecommendation}`");
        builder.AppendLine($"- ContractDriftRecommendation: `{report.ContractDriftRecommendation}`");
        builder.AppendLine();
        builder.AppendLine("## Blocked Reasons");
        AppendList(builder, report.BlockedReasons);
        builder.AppendLine();
        builder.AppendLine("Service Foundation freeze is still read-only: it does not enable formal retrieval, runtime switch, formal package write, PackingPolicy integration, or package output mutation.");
        return builder.ToString();
    }

    private static void AppendList(StringBuilder builder, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            builder.AppendLine("- (empty)");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }
}
