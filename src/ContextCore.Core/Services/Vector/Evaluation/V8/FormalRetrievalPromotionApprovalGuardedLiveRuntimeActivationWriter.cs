using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

/// <summary>V8.24 guarded live runtime activation evidence 写出器。仅在 scoped guarded mode 内写 applied/audit/state 3 个 artifact。</summary>
public static class GuardedLiveRuntimeActivationWriter
{
    private const string ActivationMode = "GuardedScopedRuntime";
    private const string ActivationSource = "V8.23LiveRuntimeActivationExecutionDryRun";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed class WriteResult
    {
        public bool AllArtifactsWritten { get; init; }
        public string AppliedPath { get; init; } = string.Empty;
        public string AuditPath { get; init; } = string.Empty;
        public string StatePath { get; init; } = string.Empty;
        public IReadOnlyList<string> WrittenPaths { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }

    public static WriteResult WriteAll(
        string root,
        string activationId,
        string boundGrantId,
        string capability,
        string scope,
        string sourceLiveRuntimeActivationExecutionDryRunOperationId,
        string sourceRuntimeActivationArtifactIntegrityOperationId,
        string sourceGuardedRuntimeActivationArtifactWriteOutOperationId,
        bool killSwitchArmed,
        bool scopeGuardActive,
        bool rollbackBindingPresent,
        DateTimeOffset now)
    {
        var written = new List<string>(3);
        var errors = new List<string>();
        try
        {
            Directory.CreateDirectory(root);
            var scopeToken = scope.Replace('/', '-').Replace('\\', '-');
            var appliedPath = Path.Combine(root, $"live-runtime-activation-applied-{capability}-{scopeToken}.json");
            var auditPath = Path.Combine(root, $"live-runtime-activation-audit-{capability}-{scopeToken}.jsonl");
            var statePath = Path.Combine(root, $"live-runtime-activation-state-{capability}-{scopeToken}.json");

            var applied = new LiveRuntimeActivationAppliedArtifactContent
            {
                ActivationId = activationId,
                BoundGrantId = boundGrantId,
                Capability = capability,
                Scope = scope,
                ActivationMode = ActivationMode,
                RuntimeActivation = true,
                FormalRetrievalAllowed = true,
                RuntimeSwitchAllowed = true,
                GlobalDefaultOn = false,
                PackageOutputChanged = false,
                FormalPackageWritten = false,
                VectorStoreBindingChanged = false,
                KillSwitchArmed = killSwitchArmed,
                RollbackBindingPresent = rollbackBindingPresent,
                ScopeGuardActive = scopeGuardActive,
                ActivationSource = ActivationSource,
                SourceLiveRuntimeActivationExecutionDryRunOperationId = sourceLiveRuntimeActivationExecutionDryRunOperationId,
                SourceRuntimeActivationArtifactIntegrityOperationId = sourceRuntimeActivationArtifactIntegrityOperationId,
                SourceGuardedRuntimeActivationArtifactWriteOutOperationId = sourceGuardedRuntimeActivationArtifactWriteOutOperationId,
                CreatedAt = now.ToString("O")
            };
            WriteJson(appliedPath, applied);
            written.Add(appliedPath);

            var auditEvent = new LiveRuntimeActivationAuditEvent
            {
                EventId = $"frp-live-runtime-activation-audit-{Guid.NewGuid():N}",
                EventType = "GuardedLiveRuntimeActivationApplied",
                ActivationId = activationId,
                BoundGrantId = boundGrantId,
                Capability = capability,
                Scope = scope,
                ActivationMode = ActivationMode,
                RuntimeActivation = true,
                FormalRetrievalAllowed = true,
                RuntimeSwitchAllowed = true,
                GlobalDefaultOn = false,
                PackageOutputChanged = false,
                FormalPackageWritten = false,
                VectorStoreBindingChanged = false,
                KillSwitchArmed = killSwitchArmed,
                ScopeGuardActive = scopeGuardActive,
                RollbackBindingPresent = rollbackBindingPresent,
                ActivationSource = ActivationSource,
                SourceLiveRuntimeActivationExecutionDryRunOperationId = sourceLiveRuntimeActivationExecutionDryRunOperationId,
                Timestamp = now.ToString("O")
            };
            WriteJsonLine(auditPath, auditEvent);
            written.Add(auditPath);

            var state = new LiveRuntimeActivationStateContent
            {
                ActivationId = activationId,
                BoundGrantId = boundGrantId,
                Capability = capability,
                Scope = scope,
                State = "Active",
                ActivationMode = ActivationMode,
                RuntimeActivation = true,
                FormalRetrievalAllowed = true,
                RuntimeSwitchAllowed = true,
                GlobalDefaultOn = false,
                PackageOutputChanged = false,
                FormalPackageWritten = false,
                VectorStoreBindingChanged = false,
                KillSwitchArmed = killSwitchArmed,
                ScopeGuardActive = scopeGuardActive,
                RollbackBindingPresent = rollbackBindingPresent,
                ActivationSource = ActivationSource,
                LastTransitionAt = now.ToString("O"),
                CreatedAt = now.ToString("O")
            };
            WriteJson(statePath, state);
            written.Add(statePath);

            return new WriteResult
            {
                AllArtifactsWritten = written.Count == 3,
                AppliedPath = appliedPath,
                AuditPath = auditPath,
                StatePath = statePath,
                WrittenPaths = written,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return new WriteResult { AllArtifactsWritten = false, WrittenPaths = written, Errors = errors };
        }
    }

    private static void WriteJson(string path, object payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(true));
    }

    private static void WriteJsonLine(string path, object payload)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(payload, JsonOptions).Replace("\r", string.Empty).Replace("\n", string.Empty);
        File.WriteAllText(path, json + Environment.NewLine, new UTF8Encoding(true));
    }
}

public sealed class LiveRuntimeActivationAppliedArtifactContent
{
    public string ActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string ActivationMode { get; init; } = string.Empty;
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool RollbackBindingPresent { get; init; }
    public bool ScopeGuardActive { get; init; }
    public string ActivationSource { get; init; } = string.Empty;
    public string SourceLiveRuntimeActivationExecutionDryRunOperationId { get; init; } = string.Empty;
    public string SourceRuntimeActivationArtifactIntegrityOperationId { get; init; } = string.Empty;
    public string SourceGuardedRuntimeActivationArtifactWriteOutOperationId { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
}

public sealed class LiveRuntimeActivationAuditEvent
{
    public string EventId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string ActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string ActivationMode { get; init; } = string.Empty;
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool ScopeGuardActive { get; init; }
    public bool RollbackBindingPresent { get; init; }
    public string ActivationSource { get; init; } = string.Empty;
    public string SourceLiveRuntimeActivationExecutionDryRunOperationId { get; init; } = string.Empty;
    public string Timestamp { get; init; } = string.Empty;
}

public sealed class LiveRuntimeActivationStateContent
{
    public string ActivationId { get; init; } = string.Empty;
    public string BoundGrantId { get; init; } = string.Empty;
    public string Capability { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string ActivationMode { get; init; } = string.Empty;
    public bool RuntimeActivation { get; init; }
    public bool FormalRetrievalAllowed { get; init; }
    public bool RuntimeSwitchAllowed { get; init; }
    public bool GlobalDefaultOn { get; init; }
    public bool PackageOutputChanged { get; init; }
    public bool FormalPackageWritten { get; init; }
    public bool VectorStoreBindingChanged { get; init; }
    public bool KillSwitchArmed { get; init; }
    public bool ScopeGuardActive { get; init; }
    public bool RollbackBindingPresent { get; init; }
    public string ActivationSource { get; init; } = string.Empty;
    public string LastTransitionAt { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
}
