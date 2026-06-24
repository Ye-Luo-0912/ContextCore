using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;

namespace ContextCore.ModelGateway.Infrastructure;

/// <summary>提供预置模型网关配置（含 API 平台、模型 profile 与任务路由）的静态工具类。</summary>
public static class ModelGatewayDefaults
{
    public static ModelGatewayOptions CreateDefaultOptions()
    {
        var options = new ModelGatewayOptions
        {
            ApiProviders =
            [
                new ModelApiProviderOptions
                {
                    Name = "deepseek",
                    Provider = "deepseek",
                    Endpoint = "https://api.deepseek.com/v1",
                    ApiKey = "env:DEEPSEEK_API_KEY",
                    Timeout = TimeSpan.FromSeconds(30),
                    Enabled = true
                },
                new ModelApiProviderOptions
                {
                    Name = "pinai-openai",
                    Provider = "openai-compatible",
                    Endpoint = "https://us.pinai-cn.com/v1",
                    ApiKey = "env:PINAI_OPENAI_API_KEY",
                    Timeout = TimeSpan.FromSeconds(60),
                    Enabled = true,
                    Metadata = new Dictionary<string, string>
                    {
                        ["apiKeyEnv"] = "PINAI_OPENAI_API_KEY"
                    }
                },
                new ModelApiProviderOptions
                {
                    Name = "local-qwen",
                    Provider = "local-http",
                    Endpoint = "http://127.0.0.1:8080/v1",
                    Timeout = TimeSpan.FromSeconds(60),
                    Enabled = false
                }
            ],
            ModelProfiles =
            [
                new ModelProfileOptions
                {
                    Name = "deepseek-v4-flash",
                    ApiProviderName = "deepseek",
                    Model = "deepseek-v4-flash",
                    Category = "fast",
                    Capabilities = ["chat", "compression", "json-response-format", "routing"],
                    Roles = ["Router", "GeneralCompression", "Fallback"],
                    TaskKinds = ["Summarize", "Reduce", "GenerateIndexHints"],
                    ThinkingModes = ["fast"],
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "50"
                    }
                },
                new ModelProfileOptions
                {
                    Name = "deepseek-v4-pro",
                    ApiProviderName = "deepseek",
                    Model = "deepseek-v4-pro",
                    Category = "balanced",
                    Capabilities = ["chat", "compression", "reasoning", "structured-json-by-prompt"],
                    Roles = ["GeneralCompression", "StrongReasoning"],
                    TaskKinds = ["Summarize", "ExtractKeyPoints", "Reduce", "Merge"],
                    ThinkingModes = ["balanced"],
                    SupportsJsonResponseFormat = false,
                    Timeout = TimeSpan.FromSeconds(45),
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "60"
                    }
                },
                new ModelProfileOptions
                {
                    Name = "pinai-gpt-5.4-mini",
                    ApiProviderName = "pinai-openai",
                    Model = "gpt-5.4-mini",
                    Category = "gpt-fast",
                    Capabilities = ["chat", "compression", "json-response-format", "fallback", "routing"],
                    Roles = ["Router", "GeneralCompression", "Fallback"],
                    TaskKinds = ["Summarize", "Reduce", "GenerateIndexHints"],
                    ThinkingModes = ["fast", "balanced"],
                    Timeout = TimeSpan.FromSeconds(45),
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "45"
                    }
                },
                new ModelProfileOptions
                {
                    Name = "pinai-gpt-5.4",
                    ApiProviderName = "pinai-openai",
                    Model = "gpt-5.4",
                    Category = "deep",
                    Capabilities = ["chat", "compression", "reasoning", "json-response-format"],
                    Roles = ["GeneralCompression", "StrongReasoning"],
                    TaskKinds = ["Summarize", "ExtractKeyPoints", "Merge", "Validate"],
                    ThinkingModes = ["deep"],
                    Timeout = TimeSpan.FromSeconds(75),
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "80"
                    }
                },
                new ModelProfileOptions
                {
                    Name = "pinai-gpt-5.5",
                    ApiProviderName = "pinai-openai",
                    Model = "gpt-5.5",
                    Category = "audit",
                    Capabilities = ["chat", "compression", "reasoning", "audit", "json-response-format", "validation"],
                    Roles = ["GeneralCompression", "StrongReasoning", "Validator"],
                    TaskKinds = ["Validate", "ExtractKeyPoints", "Merge", "Custom"],
                    ThinkingModes = ["audit", "deep"],
                    Timeout = TimeSpan.FromSeconds(90),
                    Metadata = new Dictionary<string, string>
                    {
                        ["priority"] = "100"
                    }
                },
                new ModelProfileOptions
                {
                    Name = "local-qwen3.5-2b",
                    ApiProviderName = "local-qwen",
                    Model = "qwen3.5-2b",
                    Category = "local",
                    Capabilities = ["chat", "fallback"],
                    Roles = ["Fallback"],
                    Enabled = false
                }
            ],
            Routes =
            [
                new ModelRoleRoute
                {
                    Role = ModelRole.Router,
                    PrimaryModelCategory = "fast",
                    FallbackModelCategory = "gpt-fast",
                    RequiredCapabilities = ["routing"],
                    Priority = 10,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = true,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    TaskKind = "GenerateIndexHints",
                    PrimaryModelCategory = "fast",
                    FallbackModelCategory = "gpt-fast",
                    RequiredCapabilities = ["compression"],
                    Priority = 85,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    TaskKind = "ExtractKeyPoints",
                    ThinkingMode = "balanced",
                    PrimaryModelCategory = "balanced",
                    FallbackModelCategory = "deep",
                    RequiredCapabilities = ["compression"],
                    Priority = 90,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "fast",
                    PrimaryModelCategory = "fast",
                    FallbackModelCategory = "gpt-fast",
                    RequiredCapabilities = ["compression"],
                    Priority = 50,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "balanced",
                    PrimaryModelCategory = "balanced",
                    FallbackModelCategory = "fast",
                    RequiredCapabilities = ["compression"],
                    Priority = 60,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "deep",
                    PrimaryModelCategory = "deep",
                    FallbackModelCategory = "balanced",
                    RequiredCapabilities = ["compression", "reasoning"],
                    Priority = 80,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.GeneralCompression,
                    ThinkingMode = "audit",
                    PrimaryModelCategory = "audit",
                    RequiredCapabilities = ["audit", "validation"],
                    Priority = 90,
                    MaxRetryCount = 1,
                    EnableFallback = false,
                    HighRiskTask = true
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.StrongReasoning,
                    PrimaryModelCategory = "audit",
                    FallbackModelCategory = "deep",
                    RequiredCapabilities = ["reasoning"],
                    Priority = 70,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.Validator,
                    PrimaryModelCategory = "audit",
                    RequiredCapabilities = ["validation"],
                    Priority = 70,
                    MaxRetryCount = 2,
                    EnableFallback = false,
                    HighRiskTask = true
                },
                new ModelRoleRoute
                {
                    Role = ModelRole.Fallback,
                    PrimaryModelCategory = "fast",
                    FallbackModelCategory = "gpt-fast",
                    Priority = 1,
                    MaxRetryCount = 1,
                    EnableFallback = true,
                    FallbackOnTimeout = true,
                    FallbackOnRateLimit = true,
                    FallbackOnServerError = true,
                    FallbackOnInvalidJson = false,
                    HighRiskTask = false
                }
            ]
        };

        return ModelGatewayOptionsMaterializer.Materialize(options);
    }
}
