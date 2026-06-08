using System.Linq;
using ContextCore.Abstractions;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;
using ContextCore.ModelGateway.Infrastructure;

namespace ContextCore.ControlRoom.Commands;

/// <summary>展示模型端点健康状态和用量统计的命令。</summary>
public static class ModelCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var subcommand = args.Count > 0 ? args[0] : "status";
        if (string.Equals(subcommand, "fallback", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(subcommand, "fallback-report", StringComparison.OrdinalIgnoreCase))
        {
            await GenerateFallbackReportAsync(service, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(subcommand, "status", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("model 支持的命令：status, fallback-report");
            return;
        }

        var status = await service.GetModelStatusAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (status.Options.ApiProviders.Count > 0)
        {
            TableRenderer.Render(
                "API 平台配置",
                ["名称", "类型", "启用", "端点", "密钥来源"],
                [.. status.Options.ApiProviders.Select(provider => new[]
                {
                    provider.Name,
                    provider.Provider,
                    YesNo(provider.Enabled),
                    string.IsNullOrWhiteSpace(provider.Endpoint) ? "未配置" : "已配置",
                    DescribeApiKeySource(provider.ApiKey)
                })]);
        }

        if (status.Options.ModelProfiles.Count > 0)
        {
            TableRenderer.Render(
                "模型 Profile",
                ["名称", "API平台", "实际模型", "类别", "能力", "角色", "任务", "模式", "启用"],
                status.Options.ModelProfiles.Select(profile => new[]
                {
                    profile.Name,
                    profile.ApiProviderName,
                    profile.Model,
                    profile.Category ?? "",
                    Join(profile.Capabilities),
                    Join(profile.Roles),
                    Join(profile.TaskKinds),
                    Join(profile.ThinkingModes),
                    YesNo(profile.Enabled)
                }).ToArray());
        }

        TableRenderer.Render(
            "模型端点配置",
            ["模型", "提供商", "启用", "端点", "需密钥", "密钥状态", "密钥来源", "错误"],
            status.Configuration.Select(model => new[]
            {
                model.Name,
                model.Provider,
                YesNo(model.Enabled),
                Configured(model.EndpointConfigured),
                YesNo(model.ApiKeyRequired),
                Configured(model.ApiKeyConfigured),
                string.IsNullOrWhiteSpace(model.ApiKeyEnvironmentVariable) ? model.ApiKeySource : $"{model.ApiKeySource}:{model.ApiKeyEnvironmentVariable}",
                model.ConfigurationError ?? ""
            }).ToArray());

        TableRenderer.Render(
            "模型路由",
            ["角色", "任务", "模式", "主路由", "主模型", "备用路由", "备用模型", "重试", "回退", "高风险"],
            status.Options.Routes.Select(route =>
            {
                var request = CreateRoutePreviewRequest(route);
                var primary = ModelRouteResolver.ResolveModel(
                    status.Options,
                    route.PrimaryModelName,
                    route.PrimaryModelCategory,
                    route.RequiredCapabilities,
                    request);
                var fallback = string.IsNullOrWhiteSpace(route.FallbackModelName)
                    && string.IsNullOrWhiteSpace(route.FallbackModelCategory)
                        ? null
                        : ModelRouteResolver.ResolveModel(
                            status.Options,
                            route.FallbackModelName,
                            route.FallbackModelCategory,
                            route.RequiredCapabilities,
                            request);

                return new[]
                {
                    route.Role.ToString(),
                    route.TaskKind ?? "",
                    route.ThinkingMode ?? "",
                    DescribeConfiguredTarget(route.PrimaryModelName, route.PrimaryModelCategory),
                    DescribeResolvedTarget(primary),
                    DescribeConfiguredTarget(route.FallbackModelName, route.FallbackModelCategory),
                    DescribeResolvedTarget(fallback),
                    route.MaxRetryCount.ToString(),
                    route.EnableFallback ? "启用" : "禁用",
                    YesNo(route.HighRiskTask)
                };
            }).ToArray());

        TableRenderer.Render(
            "模型健康状态",
            ["模型", "提供商", "状态", "延迟", "最近错误"],
            [.. status.Options.Models.Select(model =>
            {
                var health = status.Health.FirstOrDefault(item =>
                    string.Equals(item.ModelName, model.Name, StringComparison.OrdinalIgnoreCase));

                return new[]
                {
                    model.Name,
                    model.Provider,
                    TranslateAvailability(health?.Availability),
                    health is null ? "" : $"{health.LatencyMs} ms",
                    health?.LastError ?? ""
                };
            })]);

        Console.WriteLine();
        Console.WriteLine($"近期使用中的回退调用次数：{status.FallbackCount}");

        TableRenderer.Render(
            "近期模型调用",
            ["时间", "操作", "角色", "模型", "成功", "回退", "延迟", "Token", "错误"],
            status.UsageLogs.Select(log => new[]
            {
                log.CreatedAt.ToString("u"),
                log.OperationId,
                log.Role.ToString(),
                log.ModelName,
                YesNo(log.Succeeded),
                YesNo(log.FallbackUsed),
                $"{log.LatencyMs} ms",
                $"{log.InputTokens}/{log.OutputTokens}",
                log.ErrorMessage ?? ""
            }).ToArray());
    }

    private static ModelRequest CreateRoutePreviewRequest(ModelRoleRoute route)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AddMetadata(metadata, "taskKind", route.TaskKind);
        AddMetadata(metadata, "compressionTask", route.TaskKind);
        AddMetadata(metadata, "thinkingMode", route.ThinkingMode);

        return new ModelRequest
        {
            Role = route.Role,
            Prompt = "",
            Metadata = metadata
        };
    }

    private static void AddMetadata(
        Dictionary<string, string> metadata,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            metadata[key] = value.Trim();
        }
    }

    private static string DescribeConfiguredTarget(
        string? modelName,
        string? category)
    {
        if (!string.IsNullOrWhiteSpace(modelName))
        {
            return modelName;
        }

        return string.IsNullOrWhiteSpace(category) ? "" : $"类别:{category}";
    }

    private static string DescribeResolvedTarget(ModelRouteModelSelection? selection)
    {
        if (selection is null)
        {
            return "";
        }

        return selection.Found
            ? selection.ModelName ?? ""
            : $"未命中：{selection.Reason}";
    }

    private static string DescribeApiKeySource(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "未配置";
        }

        return apiKey.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            ? $"环境变量:{apiKey[4..].Trim()}"
            : "已配置";
    }

    private static string TranslateAvailability(ModelAvailability? availability)
    {
        return availability switch
        {
            ModelAvailability.Available => "可用",
            ModelAvailability.Unavailable => "不可用",
            _ => "未知"
        };
    }

    private static string YesNo(bool value)
    {
        return value ? "是" : "否";
    }

    private static string Configured(bool value)
    {
        return value ? "已配置" : "未配置";
    }

    private static string Join(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "" : string.Join(",", values);
    }

    private static async Task GenerateFallbackReportAsync(
        ControlRoomService service,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("正在从存储中读取近期的压缩质量评估记录...");
        var reports = await service.GetRecentCompressionQualityAsync(200, cancellationToken)
            .ConfigureAwait(false);

        if (reports.Count == 0)
        {
            Console.WriteLine("未找到任何历史压缩质量记录。请先执行一些压缩操作或运行评测任务。");
            return;
        }

        var fallbackReports = reports.Where(r => r.Signals.Contains("fallback-used", StringComparer.OrdinalIgnoreCase)).ToList();
        var primaryReports = reports.Where(r => !r.Signals.Contains("fallback-used", StringComparer.OrdinalIgnoreCase)).ToList();

        TableRenderer.Render(
            "压缩源数据分类统计",
            ["类型", "样本量", "占比"],
            [
                new[] { "主模型 (无回退)", primaryReports.Count.ToString(), $"{(double)primaryReports.Count / reports.Count * 100:0.0}%" },
                new[] { "回退兜底模型", fallbackReports.Count.ToString(), $"{(double)fallbackReports.Count / reports.Count * 100:0.0}%" },
                new[] { "总计", reports.Count.ToString(), "100%" }
            ]);

        var rows = new List<string[]>();
        
        if (primaryReports.Count > 0)
        {
            rows.Add(new[]
            {
                "主模型 (无回退)",
                primaryReports.Count.ToString(),
                $"{primaryReports.Average(r => r.CompletenessScore):0.000}",
                $"{primaryReports.Average(r => r.ConsistencyScore):0.000}",
                $"{primaryReports.Average(r => r.UsabilityScore):0.000}",
                $"{primaryReports.Average(r => r.RiskScore):0.000}",
                $"{(double)primaryReports.Count(r => r.RequiresReview) / primaryReports.Count * 100:0.0}%",
                $"{primaryReports.Average(r => r.CompressionRatio):0.0%}"
            });
        }
        else
        {
            rows.Add(new[] { "主模型 (无回退)", "0", "-", "-", "-", "-", "-", "-" });
        }

        if (fallbackReports.Count > 0)
        {
            rows.Add(new[]
            {
                "回退兜底模型",
                fallbackReports.Count.ToString(),
                $"{fallbackReports.Average(r => r.CompletenessScore):0.000}",
                $"{fallbackReports.Average(r => r.ConsistencyScore):0.000}",
                $"{fallbackReports.Average(r => r.UsabilityScore):0.000}",
                $"{fallbackReports.Average(r => r.RiskScore):0.000}",
                $"{(double)fallbackReports.Count(r => r.RequiresReview) / fallbackReports.Count * 100:0.0}%",
                $"{fallbackReports.Average(r => r.CompressionRatio):0.0%}"
            });
        }
        else
        {
            rows.Add(new[] { "回退兜底模型", "0", "-", "-", "-", "-", "-", "-" });
        }

        TableRenderer.Render(
            "回退兜底与主模型压缩质量对比",
            ["类型", "样本量", "完整性 (Completeness)", "一致性 (Consistency)", "可用性 (Usability)", "风险度 (Risk)", "需要复核率", "平均压缩比"],
            rows.ToArray());

        Console.WriteLine("\n[质量分析与建议]");
        if (fallbackReports.Count > 0)
        {
            var avgFallbackRisk = fallbackReports.Average(r => r.RiskScore);
            var avgPrimaryRisk = primaryReports.Count > 0 ? primaryReports.Average(r => r.RiskScore) : 0;
            
            if (avgFallbackRisk > 0.6)
            {
                Console.WriteLine("⚠️ 警告：回退兜底模型的压缩风险偏高 (Avg Risk > 0.6)。这可能会引入低质量的临时上下文摘要。");
            }
            if (primaryReports.Count > 0 && avgFallbackRisk > avgPrimaryRisk + 0.15)
            {
                Console.WriteLine($"💡 提示：回退模型质量 (Avg Risk: {avgFallbackRisk:0.00}) 显著低于主模型 (Avg Risk: {avgPrimaryRisk:0.00})。建议检查回退路由的模型选择或改进提示词。");
            }
            else
            {
                Console.WriteLine("✅ 评估：回退兜底模型的质量目前处于安全范围内。");
            }
        }
        else
        {
            Console.WriteLine("ℹ️ 当前未检测到使用了回退模型的压缩记录。当主模型调用失败（超时/限流/错误）触发网关自动回退时，此处将会呈现对比数据。");
        }
    }
}
