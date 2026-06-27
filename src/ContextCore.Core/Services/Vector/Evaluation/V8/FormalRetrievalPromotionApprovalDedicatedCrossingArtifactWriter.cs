using System.Text;
using System.Text.Json;

namespace ContextCore.Core.Services;

/// <summary>V8.18 受控 artifact 写出器。仅写 5 个文件到 dedicated-crossing 目录。</summary>
public static class FormalRetrievalPromotionApprovalDedicatedCrossingArtifactWriter
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

    public static WriteResult WriteAll(CrossingExecutionDecision decision, DateTimeOffset now)
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

        var capability = decision.BoundCapability;
        var scope = decision.BoundScope;
        var grantId = $"frp-grant-{Guid.NewGuid():N}";
        var patchId = $"frp-runtime-config-patch-{Guid.NewGuid():N}";
        var snapshotId = $"frp-rollback-snapshot-{Guid.NewGuid():N}";
        var revocationRecordId = $"frp-revocation-record-{Guid.NewGuid():N}";

        var written = new List<string>();
        var failed = new List<string>();
        var errors = new List<string>();

        try
        {
            var grantPath = decision.PlannedArtifactPaths[0];
            var configPatchPath = decision.PlannedArtifactPaths[1];
            var rollbackSnapshotPath = decision.PlannedArtifactPaths[2];
            var auditLogPath = decision.PlannedArtifactPaths[3];
            var revocationPath = decision.PlannedArtifactPaths[4];

            EnsureDirectory(grantPath);

            // 1) capability grant
            var grant = new
            {
                GrantId = grantId,
                Capability = capability,
                Scope = scope,
                SourcePreCrossingOperationId = decision.SourcePreCrossingOperationId,
                SourceDryRunOperationId = decision.SourceDryRunOperationId,
                IssuedAt = now.ToString("O"),
                ValidUntil = now.AddYears(1).ToString("O"),
                Revocable = true,
                RuntimeActivationAllowed = false,
                ArtifactOnly = true,
                Crossed = true,
                FormalRetrievalAllowed = false,
                RuntimeSwitchAllowed = false
            };
            WriteJson(grantPath, grant);
            written.Add(grantPath);

            // 2) runtime config patch — artifact only, never applied
            var configPatch = new
            {
                PatchId = patchId,
                TargetCapability = capability,
                TargetScope = scope,
                PatchMode = "ArtifactOnly",
                ApplyToRuntime = false,
                FormalRetrievalAllowed = false,
                SourceGrantId = grantId,
                SourcePreCrossingOperationId = decision.SourcePreCrossingOperationId,
                SourceDryRunOperationId = decision.SourceDryRunOperationId,
                IssuedAt = now.ToString("O")
            };
            WriteJson(configPatchPath, configPatch);
            written.Add(configPatchPath);

            // 3) rollback snapshot
            var rollbackSnapshot = new
            {
                SnapshotId = snapshotId,
                BoundCapability = capability,
                BoundScope = scope,
                SourceGrantId = grantId,
                PreCrossingState = new
                {
                    FormalRetrievalAllowed = false,
                    RuntimeActivation = false,
                    CapabilityGrantPresent = false,
                    RuntimeConfigPatchApplied = false
                },
                PlannedRestoreActions = new[]
                {
                    "delete capability grant artifact",
                    "delete runtime config patch artifact",
                    "append rollback event to audit log",
                    "mark revocation record as revoked"
                },
                RestoreTestRequired = true,
                IssuedAt = now.ToString("O")
            };
            WriteJson(rollbackSnapshotPath, rollbackSnapshot);
            written.Add(rollbackSnapshotPath);

            // 4) audit log (jsonl — one event per line)
            var auditEvent = new
            {
                EventId = $"frp-crossing-audit-{Guid.NewGuid():N}",
                EventType = "DedicatedCrossingArtifactWriteOut",
                BoundCapability = capability,
                BoundScope = scope,
                GrantId = grantId,
                PatchId = patchId,
                SnapshotId = snapshotId,
                SourcePreCrossingOperationId = decision.SourcePreCrossingOperationId,
                SourceDryRunOperationId = decision.SourceDryRunOperationId,
                Crossed = true,
                ArtifactOnly = true,
                RuntimeActivation = false,
                FormalRetrievalAllowed = false,
                Timestamp = now.ToString("O")
            };
            var auditLine = JsonSerializer.Serialize(auditEvent, JsonOptions);
            // jsonl: 单行
            var auditOneLine = auditLine.Replace("\r", string.Empty).Replace("\n", string.Empty);
            System.IO.File.WriteAllText(auditLogPath, auditOneLine + Environment.NewLine, new UTF8Encoding(true));
            written.Add(auditLogPath);

            // 5) revocation record
            var revocationRecord = new
            {
                RevocationRecordId = revocationRecordId,
                GrantId = grantId,
                BoundCapability = capability,
                BoundScope = scope,
                Revocable = true,
                RevocationPathPresent = true,
                RevocationStatus = "RevocableNotYetRevoked",
                IssuedAt = now.ToString("O")
            };
            WriteJson(revocationPath, revocationRecord);
            written.Add(revocationPath);

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
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }
    }

    private static void WriteJson(string path, object payload)
    {
        EnsureDirectory(path);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        System.IO.File.WriteAllText(path, json, new UTF8Encoding(true));
    }
}
