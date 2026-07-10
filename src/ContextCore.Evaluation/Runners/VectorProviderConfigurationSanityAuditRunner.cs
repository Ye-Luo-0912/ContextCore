using System.Text;
using ContextCore.Abstractions.Models;

namespace ContextCore.ControlRoom.Services;

/// <summary>V3.10.F Qwen3 provider 配置一致性审计；只读取报告，不改变任何 runtime 行为。</summary>
public sealed class VectorProviderConfigurationSanityAuditRunner
{
    private const string ExpectedProviderId = "qwen3-embedding-0.6b-onnx";
    private const string ExpectedModelId = "qwen3-embedding-0.6b";
    private const string ExpectedPathSegment = "qwen3-embedding-0.6b-onnx";
    private const int ExpectedDimension = 1024;

    public VectorProviderConfigurationSanityAuditReport BuildReport(
        IReadOnlyList<VectorProviderConfigurationSanityAuditItem> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var blocked = new List<string>();
        if (checks.Count == 0)
        {
            blocked.Add("NoQwen3ProviderReports");
        }

        foreach (var check in checks)
        {
            if (!check.Passed)
            {
                blocked.Add($"{check.ReportKind}:ProviderConfigurationMismatch");
            }
        }

        var passed = blocked.Count == 0;
        return new VectorProviderConfigurationSanityAuditReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Passed = passed,
            ProviderComparison = passed ? "Conclusive" : "Inconclusive",
            ExpectedProviderType = EmbeddingProviderTypes.OnnxLocal,
            ExpectedProviderId = ExpectedProviderId,
            ExpectedModelId = ExpectedModelId,
            ExpectedPathSegment = ExpectedPathSegment,
            ExpectedDimension = ExpectedDimension,
            ExpectedUseForRuntime = false,
            ReportChecks = checks,
            BlockedReasons = blocked,
            Diagnostics = passed
                ? ["ProviderType=OnnxLocal", "UseForRuntime=false", "Qwen3ProviderScopeMatched"]
                : ["ProviderComparison=Inconclusive", "FreezeMustNotPromoteOrV4Recheck"],
            Recommendation = passed ? "ReadyForProviderComparisonFreeze" : "BlockedByProviderConfigurationMismatch"
        };
    }

    public VectorProviderConfigurationSanityAuditItem Check(
        string reportKind,
        string reportPath,
        string providerType,
        string providerId,
        string modelId,
        string? modelPath,
        string? tokenizerPath,
        int dimension,
        bool useForRuntime)
    {
        var mismatches = new List<string>();
        AddIfFalse(mismatches, IsOnnxProviderType(providerType), "ProviderTypeMismatch");
        AddIfFalse(mismatches, IsEqual(providerId, ExpectedProviderId), "ProviderIdMismatch");
        AddIfFalse(mismatches, IsEqual(modelId, ExpectedModelId), "ModelIdMismatch");
        AddIfFalse(mismatches, ContainsPathSegment(modelPath), "ModelPathMismatch");
        AddIfFalse(mismatches, ContainsPathSegment(tokenizerPath), "TokenizerPathMismatch");
        AddIfFalse(mismatches, dimension == ExpectedDimension, "DimensionMismatch");
        AddIfFalse(mismatches, !useForRuntime, "UseForRuntimeMustBeFalse");

        return new VectorProviderConfigurationSanityAuditItem
        {
            ReportKind = reportKind,
            ReportPath = reportPath,
            ProviderType = providerType,
            ProviderId = providerId,
            ModelId = modelId,
            ModelPath = modelPath,
            TokenizerPath = tokenizerPath,
            Dimension = dimension,
            UseForRuntime = useForRuntime,
            Passed = mismatches.Count == 0,
            Mismatches = mismatches
        };
    }

    public static string BuildMarkdown(VectorProviderConfigurationSanityAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var builder = new StringBuilder();
        builder.AppendLine("# Vector Provider Configuration Sanity Audit");
        builder.AppendLine();
        builder.AppendLine($"Generated: {report.GeneratedAt:O}");
        builder.AppendLine();
        builder.AppendLine($"- Passed: `{report.Passed}`");
        builder.AppendLine($"- ProviderComparison: `{report.ProviderComparison}`");
        builder.AppendLine($"- ExpectedProviderType: `{report.ExpectedProviderType}`");
        builder.AppendLine($"- ExpectedProviderId: `{report.ExpectedProviderId}`");
        builder.AppendLine($"- ExpectedModelId: `{report.ExpectedModelId}`");
        builder.AppendLine($"- ExpectedPathSegment: `{report.ExpectedPathSegment}`");
        builder.AppendLine($"- ExpectedDimension: `{report.ExpectedDimension}`");
        builder.AppendLine($"- ExpectedUseForRuntime: `{report.ExpectedUseForRuntime}`");
        builder.AppendLine($"- Recommendation: `{report.Recommendation}`");
        builder.AppendLine();
        builder.AppendLine("| Report | Passed | ProviderType | ProviderId | ModelId | Dimension | Runtime | Mismatches |");
        builder.AppendLine("|---|---|---|---|---|---:|---|---|");
        foreach (var check in report.ReportChecks)
        {
            builder.AppendLine(
                $"| {check.ReportKind} | {check.Passed} | {check.ProviderType} | {check.ProviderId} | {check.ModelId} | {check.Dimension} | {check.UseForRuntime} | {string.Join(", ", check.Mismatches)} |");
        }

        AppendList(builder, "BlockedReasons", report.BlockedReasons);
        AppendList(builder, "Diagnostics", report.Diagnostics);
        return builder.ToString();
    }

    private static bool IsOnnxProviderType(string providerType)
    {
        return providerType.Equals(EmbeddingProviderTypes.OnnxLocal, StringComparison.OrdinalIgnoreCase)
               || providerType.Equals("onnx-local", StringComparison.OrdinalIgnoreCase)
               || providerType.Equals("onnx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEqual(string value, string expected)
    {
        return value.Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPathSegment(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && path.Replace('\\', '/').Contains(ExpectedPathSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddIfFalse(ICollection<string> mismatches, bool condition, string reason)
    {
        if (!condition)
        {
            mismatches.Add(reason);
        }
    }

    private static void AppendList(StringBuilder builder, string title, IReadOnlyList<string> values)
    {
        builder.AppendLine();
        builder.AppendLine($"## {title}");
        if (values.Count == 0)
        {
            builder.AppendLine("- none");
            return;
        }

        foreach (var value in values)
        {
            builder.AppendLine($"- `{value}`");
        }
    }
}
