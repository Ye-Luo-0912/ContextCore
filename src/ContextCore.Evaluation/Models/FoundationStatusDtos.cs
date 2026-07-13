using ContextCore.Abstractions.Models;

namespace ContextCore.Evaluation.Models;

/// <summary>统一 runtime mode 名称；registry 只记录允许/禁止，不直接改运行时。</summary>
public static class ShadowRuntimeModes
{
    public const string Off = nameof(Off);

    public const string PreviewOnly = nameof(PreviewOnly);

    public const string Shadow = nameof(Shadow);

    public const string RuntimeShadow = nameof(RuntimeShadow);

    public const string ApplyGuarded = nameof(ApplyGuarded);

    public const string DefaultOn = nameof(DefaultOn);

    public const string ExistingRuntime = nameof(ExistingRuntime);
}

public sealed class FoundationServiceAuthOptions
{
    public bool Enabled { get; init; } = true;

    public ServiceDeploymentProfile DeploymentProfile { get; init; } = ServiceDeploymentProfile.Development;

    public bool RequireApiKey { get; init; }

    public string ApiKeyHeaderName { get; init; } = "X-ContextCore-Key";

    public bool AllowDevelopmentNoAuth { get; init; } = true;

    public bool RedactSecrets { get; init; } = true;

    public bool FailOnSecretLeak { get; init; } = true;
}

public sealed class FoundationServiceAuthEnforcementSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool SmokePassed { get; init; }

    public bool DevelopmentNoAuthAllowed { get; init; }

    public bool ServiceMissingApiKeyBlocked { get; init; }

    public bool ServiceConfiguredApiKeyPassed { get; init; }

    public bool ProductionMissingAuthBlocked { get; init; }

    public bool WrongApiKeyUnauthorized { get; init; }

    public bool CorrectApiKeyAvailable { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class ServiceReportNavigationSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool SmokePassed { get; init; }

    public int ReportCount { get; init; }

    public int DegradedReportCount { get; init; }

    public bool AbsolutePathLeakDetected { get; init; }

    public bool SecretLeakDetected { get; init; }

    public bool EnvelopeSchemaStable { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public sealed class FoundationApiContractSnapshot
{
    public string SnapshotId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SchemaVersion { get; init; } = string.Empty;

    public IReadOnlyList<string> EnvelopeSchemaFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<FoundationApiEndpointContract> Endpoints { get; init; } =
        Array.Empty<FoundationApiEndpointContract>();

    public IReadOnlyList<string> CapabilityStatusSchemaFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ReportNavigationSchemaFields { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ForbiddenActions { get; init; } = Array.Empty<string>();

    public string AuthScheme { get; init; } = string.Empty;

    public string ApiKeyHeaderName { get; init; } = string.Empty;

    public bool ReadOnly { get; init; } = true;

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }
}

public sealed class FoundationClientContractSnapshot
{
    public string SnapshotId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public string SchemaVersion { get; init; } = string.Empty;

    public IReadOnlyList<FoundationApiClientMethodContract> Methods { get; init; } =
        Array.Empty<FoundationApiClientMethodContract>();

    public IReadOnlyList<FoundationApiClientMethodContract> AliasMethods { get; init; } =
        Array.Empty<FoundationApiClientMethodContract>();

    public bool ReadOnly { get; init; } = true;
}

public sealed class HostedServiceSmokeOptions
{
    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public ServiceDeploymentProfile DeploymentProfile { get; init; } = ServiceDeploymentProfile.Development;

    public bool RequireApiKey { get; init; }

    public string ApiKeyHeaderName { get; init; } = "X-ContextCore-Key";

    public int TimeoutSeconds { get; init; } = 15;

    public bool VerifyReadOnly { get; init; } = true;

    public bool VerifyNoRuntimeMutation { get; init; } = true;
}

public sealed class ServiceFoundationStatusSmokeReport
{
    public string OperationId { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool SmokePassed { get; init; }

    public string Recommendation { get; init; } = string.Empty;

    public int EndpointCount { get; init; }

    public int CapabilityCount { get; init; }

    public bool FoundationStatusPassed { get; init; }

    public bool ReleaseCandidatePassed { get; init; }

    public bool ReproducibilityPassed { get; init; }

    public bool RuntimeChangeGatePassed { get; init; }

    public bool VectorFormalPreviewPassed { get; init; }

    public bool PostgresFreezePassed { get; init; }

    public bool RuntimeMutated { get; init; }

    public bool FormalRetrievalAllowed { get; init; }

    public bool RuntimeSwitchAllowed { get; init; }

    public bool PackingPolicyChanged { get; init; }

    public bool PackageOutputChanged { get; init; }

    public IReadOnlyList<string> BlockedReasons { get; init; } = Array.Empty<string>();
}

public static class ProjectStateAuditStatuses
{
    public const string Frozen = nameof(Frozen);
    public const string Ready = nameof(Ready);
    public const string PreviewOnly = nameof(PreviewOnly);
    public const string PlanOnly = nameof(PlanOnly);
    public const string Blocked = nameof(Blocked);
    public const string Unknown = nameof(Unknown);
}
