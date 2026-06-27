using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

/// <summary>V8.21 runtime-activation artifact 写出器。只写 activation evidence，不应用到 runtime。</summary>
public static class FormalRetrievalPromotionApprovalRuntimeActivationArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public sealed class WriteResult
    {
        public bool AllArtifactsWritten { get; init; }
        public IReadOnlyList<string> WrittenPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> FailedPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    public static WriteResult WriteAll(GuardedRuntimeActivationArtifactWriteOutDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.PlannedArtifactPaths.Count != 5)
        {
            return new WriteResult
            {
                AllArtifactsWritten = false,
                Errors = new[] { $"expected 5 planned paths, got {decision.PlannedArtifactPaths.Count}" }
            };
        }

        var written = new List<string>(5);
        var failed = new List<string>();
        var errors = new List<string>();

        try
        {
            var switchId = $"frp-runtime-switch-{Guid.NewGuid():N}";
            var runtimeSwitchPath = decision.PlannedArtifactPaths[0];
            var activationAuditPath = decision.PlannedArtifactPaths[1];
            var runtimeGuardManifestPath = decision.PlannedArtifactPaths[2];
            var scopeEnforcementManifestPath = decision.PlannedArtifactPaths[3];
            var activationRollbackBindingPath = decision.PlannedArtifactPaths[4];

            var runtimeSwitch = new GuardedRuntimeActivationRuntimeSwitchArtifactContent
            {
                SwitchId = switchId,
                BoundGrantId = decision.BoundGrantId,
                Capability = decision.BoundCapability,
                Scope = decision.BoundScope,
                SwitchMode = "GuardedArtifactOnly",
                ApplyToRuntime = false,
                RuntimeActivation = false,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                SourceGuardedRuntimeActivationDryRunOperationId = decision.SourceGuardedRuntimeActivationDryRunOperationId,
                CreatedAt = now.ToString("O")
            };
            WriteJson(runtimeSwitchPath, runtimeSwitch);
            written.Add(runtimeSwitchPath);

            var auditEvent = new GuardedRuntimeActivationAuditArtifactEvent
            {
                EventId = $"frp-runtime-activation-audit-{Guid.NewGuid():N}",
                EventType = "GuardedRuntimeActivationArtifactWriteOut",
                BoundGrantId = decision.BoundGrantId,
                Capability = decision.BoundCapability,
                Scope = decision.BoundScope,
                RuntimeActivationArtifactsWritten = true,
                RuntimeActivation = false,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false,
                SourceGuardedRuntimeActivationDryRunOperationId = decision.SourceGuardedRuntimeActivationDryRunOperationId,
                Timestamp = now.ToString("O")
            };
            WriteJsonLine(activationAuditPath, auditEvent);
            written.Add(activationAuditPath);

            var runtimeGuardManifest = new GuardedRuntimeActivationRuntimeGuardManifestContent
            {
                BoundGrantId = decision.BoundGrantId,
                Scope = decision.BoundScope,
                KillSwitchRequired = true,
                ScopeGuardRequired = true,
                RollbackRequired = true,
                RuntimeActivationAllowed = false,
                CreatedAt = now.ToString("O")
            };
            WriteJson(runtimeGuardManifestPath, runtimeGuardManifest);
            written.Add(runtimeGuardManifestPath);

            var scopeEnforcementManifest = new GuardedRuntimeActivationScopeEnforcementManifestContent
            {
                BoundGrantId = decision.BoundGrantId,
                AllowedScope = decision.BoundScope,
                GlobalDefaultOn = false,
                WildcardScopeAllowed = false,
                CreatedAt = now.ToString("O")
            };
            WriteJson(scopeEnforcementManifestPath, scopeEnforcementManifest);
            written.Add(scopeEnforcementManifestPath);

            var rollbackBinding = new GuardedRuntimeActivationRollbackBindingContent
            {
                BoundGrantId = decision.BoundGrantId,
                RollbackSnapshotReference = decision.PlannedGuardedActivationContract.ReferencedRollbackSnapshotPath,
                RevocationRecordReference = decision.PlannedGuardedActivationContract.ReferencedRevocationRecordPath,
                ConfigPatchSourceReference = decision.PlannedGuardedActivationContract.ReferencedConfigPatchSourcePath,
                RestoreTestRequired = true,
                RuntimeActivation = false,
                CreatedAt = now.ToString("O")
            };
            WriteJson(activationRollbackBindingPath, rollbackBinding);
            written.Add(activationRollbackBindingPath);

            return new WriteResult
            {
                AllArtifactsWritten = written.Count == 5,
                WrittenPaths = written,
                FailedPaths = failed,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new WriteResult
            {
                AllArtifactsWritten = false,
                WrittenPaths = written,
                FailedPaths = failed,
                Errors = errors
            };
        }
    }

    private static void EnsureDirectory(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void WriteJson(string path, object payload)
    {
        EnsureDirectory(path);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(true));
    }

    private static void WriteJsonLine(string path, object payload)
    {
        EnsureDirectory(path);
        var json = JsonSerializer.Serialize(payload, JsonOptions).Replace("\r", string.Empty).Replace("\n", string.Empty);
        File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(true));
    }
}

public sealed class GuardedRuntimeActivationRuntimeSwitchArtifactContent
{
    public string SwitchId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string SwitchMode { get; init; } = string.Empty;
    public bool ApplyToRuntime { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public string SourceGuardedRuntimeActivationDryRunOperationId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
}

public sealed class GuardedRuntimeActivationAuditArtifactEvent
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public bool RuntimeActivationArtifactsWritten { get; init; }
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public string SourceGuardedRuntimeActivationDryRunOperationId { get; init; } = string.Empty;
    public string Timestamp { get; init; } = string.Empty;
}

public sealed class GuardedRuntimeActivationRuntimeGuardManifestContent
{
    public string BoundGrantId { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public bool KillSwitchRequired { get; init; }
    public bool ScopeGuardRequired { get; init; }
    public bool RollbackRequired { get; init; }
    public bool RuntimeActivationAllowed { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
}

public sealed class GuardedRuntimeActivationScopeEnforcementManifestContent
{
    public string BoundGrantId { get; init; } = string.Empty;
    public string AllowedScope { get; init; } = string.Empty;
    public bool GlobalDefaultOn { get; init; }
    public bool WildcardScopeAllowed { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
}

public sealed class GuardedRuntimeActivationRollbackBindingContent
{
    public string BoundGrantId { get; init; } = string.Empty;
    public string RollbackSnapshotReference { get; init; } = string.Empty;
    public string RevocationRecordReference { get; init; } = string.Empty;
    public string ConfigPatchSourceReference { get; init; } = string.Empty;
    public bool RestoreTestRequired { get; init; }
    public bool RuntimeActivation { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
}
