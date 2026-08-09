using ContextCore.Abstractions;
using ContextCore.ControlRoom.Services;

namespace ContextCore.ControlRoom.Commands;

/// <summary>
/// Learning Artifact 控制命令（WP-P）。
/// </summary>
/// <remarks>
/// 用法：
/// learning artifact get --snapshot-id &lt;id&gt; [--workspace &lt;id&gt;]
/// learning artifact list [--take &lt;N&gt;] [--workspace &lt;id&gt;]
/// learning artifact export --out &lt;directory&gt; [--model-artifact-id &lt;id&gt;] [--collection &lt;id&gt;]
/// learning decision get --decision-id &lt;id&gt; [--workspace &lt;id&gt;]
///
/// 说明：
/// - Direct 模式从本地 ILearningArtifactStore / IDecisionTraceStore 读取；
///   Service 模式下本地组件为 null，命令提示通过 Service API（/api/learning）远程调用。
/// </remarks>
public static class LearningCommand
{
    public static async Task ExecuteAsync(
        ControlRoomService service,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        var subcommand = args.Count > 0 ? args[0].ToLowerInvariant() : string.Empty;
        switch (subcommand)
        {
            case "artifact" when args.Count > 1 && args[1].Equals("get", StringComparison.OrdinalIgnoreCase):
                await GetArtifactAsync(service, args, cancellationToken).ConfigureAwait(false);
                break;

            case "artifact" when args.Count > 1 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase):
                await ListArtifactsAsync(service, args, cancellationToken).ConfigureAwait(false);
                break;

            case "artifact" when args.Count > 1 && args[1].Equals("export", StringComparison.OrdinalIgnoreCase):
                await ExportArtifactAsync(service, args, cancellationToken).ConfigureAwait(false);
                break;

            case "decision" when args.Count > 1 && args[1].Equals("get", StringComparison.OrdinalIgnoreCase):
                await GetDecisionAsync(service, args, cancellationToken).ConfigureAwait(false);
                break;

            default:
                PrintUsage(Console.Error);
                Environment.ExitCode = 2;
                break;
        }
    }

    private static async Task GetArtifactAsync(
        ControlRoomService service, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var store = service.State.LearningArtifactStore;
        if (store is null)
        {
            Console.Error.WriteLine("当前 ControlRoom 模式未配置 Learning Artifact Store（仅 Direct 模式可用；Service 模式请调用 GET /api/learning/artifacts/{snapshotId}）。");
            Environment.ExitCode = 2;
            return;
        }

        var snapshotId = CommandHelpers.GetOption(args, "--snapshot-id");
        if (string.IsNullOrWhiteSpace(snapshotId))
        {
            Console.Error.WriteLine("learning artifact get 需要 --snapshot-id 参数。");
            Environment.ExitCode = 2;
            return;
        }

        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId;
        var artifact = await store.GetAsync(workspaceId, snapshotId, cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            Console.Error.WriteLine($"快照工件不存在：{snapshotId}（workspace={workspaceId}）。");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"SnapshotId: {artifact.Snapshot.SnapshotId}");
        Console.WriteLine($"SchemaVersion: {artifact.Snapshot.SchemaVersion}");
        Console.WriteLine($"ModelArtifactId: {artifact.Snapshot.ModelArtifactId ?? "<null>"}");
        Console.WriteLine($"InputEvidence: {artifact.Snapshot.InputEvidenceCount?.ToString() ?? "<unknown>"}");
        Console.WriteLine($"Materialized: {artifact.Snapshot.MaterializedCount}");
        Console.WriteLine($"Completeness: {artifact.Snapshot.CompletenessRatio?.ToString("P1") ?? "<unknown>"}");
        Console.WriteLine($"ContentHash: {artifact.Snapshot.ContentHash}");
        Console.WriteLine($"LineageDecisions: {artifact.Snapshot.LineageDecisionCount}");
        Console.WriteLine($"DataFile: {artifact.DataFilePath ?? "<null>"}");
    }

    private static async Task ListArtifactsAsync(
        ControlRoomService service, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var store = service.State.LearningArtifactStore;
        if (store is null)
        {
            Console.Error.WriteLine("当前 ControlRoom 模式未配置 Learning Artifact Store（仅 Direct 模式可用；Service 模式请调用 GET /api/learning/artifacts）。");
            Environment.ExitCode = 2;
            return;
        }

        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId;
        var takeText = CommandHelpers.GetOption(args, "--take");
        var take = int.TryParse(takeText, out var parsed) && parsed > 0 ? parsed : 20;

        var artifacts = await store.ListRecentAsync(workspaceId, take, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"最近 {artifacts.Count} 个数据集快照工件（workspace={workspaceId}）：");
        foreach (var artifact in artifacts)
        {
            Console.WriteLine(
                $"  {artifact.Snapshot.SnapshotId}  model={artifact.Snapshot.ModelArtifactId ?? "-"}  " +
                $"complete={artifact.Snapshot.CompletenessRatio?.ToString("P0") ?? "-"}  " +
                $"created={artifact.Snapshot.CreatedAt:O}");
        }
    }

    private static async Task ExportArtifactAsync(
        ControlRoomService service, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var exporter = service.State.TrainingDataExporter;
        var store = service.State.LearningArtifactStore;
        if (exporter is null || store is null)
        {
            Console.Error.WriteLine("当前 ControlRoom 模式未配置训练数据导出器 / Artifact Store（仅 Direct 模式可用；Service 模式请调用 POST /api/learning/artifacts/export）。");
            Environment.ExitCode = 2;
            return;
        }

        var outputPath = CommandHelpers.GetOption(args, "--out") ?? CommandHelpers.GetOption(args, "-o");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.Error.WriteLine("learning artifact export 需要 --out <directory> 参数。");
            Environment.ExitCode = 2;
            return;
        }

        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId;
        var export = await exporter.ExportAsync(new TrainingDataExportRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = CommandHelpers.GetOption(args, "--collection"),
            ModelArtifactId = CommandHelpers.GetOption(args, "--model-artifact-id"),
            OutputDirectory = outputPath
        }, cancellationToken).ConfigureAwait(false);

        if (export.DatasetSnapshot is { } snapshot)
        {
            // 数据质量闸门（WP-U）：Blocked（空数据集等）→ 不落库并提示。
            var gate = new ContextCore.Core.Services.MemoryEvolution.LearningDataQualityGate();
            var quality = gate.Evaluate(snapshot, export.PositiveCount, export.NegativeCount);
            if (quality.Verdict == ContextCore.Core.Services.MemoryEvolution.LearningDataQualityVerdict.Blocked)
            {
                Console.Error.WriteLine($"数据集质量阻断，未落库：{string.Join("；", quality.Issues)}");
                Environment.ExitCode = 1;
                return;
            }

            if (quality.Verdict == ContextCore.Core.Services.MemoryEvolution.LearningDataQualityVerdict.Warning)
            {
                Console.WriteLine($"数据质量警告（仍落库）：{string.Join("；", quality.Issues)}");
            }

            await store.SaveAsync(new DatasetSnapshotArtifact
            {
                Snapshot = snapshot,
                DataFilePath = export.DataFilePath,
                ManifestFilePath = export.ManifestFilePath,
                StoredAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine($"导出完成：{export.EntryCount} 条样本（{export.DataFilePath}）。");
        Console.WriteLine($"快照：{export.DatasetSnapshot?.SnapshotId ?? "<null>"}（完整性 " +
                          $"{export.DatasetSnapshot?.CompletenessRatio?.ToString("P1") ?? "<unknown>"}）。");
    }

    private static async Task GetDecisionAsync(
        ControlRoomService service, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var store = service.State.DecisionTraceStore;
        if (store is null)
        {
            Console.Error.WriteLine("当前 ControlRoom 模式未配置决策记录存储（仅 Direct 模式可用；Service 模式请调用 GET /api/learning/decisions/{decisionId}）。");
            Environment.ExitCode = 2;
            return;
        }

        var decisionId = CommandHelpers.GetOption(args, "--decision-id");
        if (string.IsNullOrWhiteSpace(decisionId))
        {
            Console.Error.WriteLine("learning decision get 需要 --decision-id 参数。");
            Environment.ExitCode = 2;
            return;
        }

        var workspaceId = CommandHelpers.GetOption(args, "--workspace") ?? service.State.WorkspaceId;
        var record = await store.GetAsync(workspaceId, workspaceId, decisionId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            Console.Error.WriteLine($"决策记录不存在：{decisionId}（workspace={workspaceId}）。");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"DecisionId: {record.DecisionId}");
        Console.WriteLine($"Source: {record.Source}");
        Console.WriteLine($"QueryText: {record.QueryText}");
        Console.WriteLine($"PolicyVersion: {record.PolicyVersion}");
        Console.WriteLine($"Candidates: {record.Candidates.Count}");
        Console.WriteLine($"Selected: {record.Outcome.SelectedCount}");
        Console.WriteLine($"Dropped: {record.Outcome.DroppedCount}");
        Console.WriteLine($"CreatedAt: {record.CreatedAt:O}");
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("learning artifact get --snapshot-id <id> [--workspace <id>]");
        writer.WriteLine("learning artifact list [--take <N>] [--workspace <id>]");
        writer.WriteLine("learning artifact export --out <directory> [--model-artifact-id <id>] [--collection <id>]");
        writer.WriteLine("learning decision get --decision-id <id> [--workspace <id>]");
    }
}
