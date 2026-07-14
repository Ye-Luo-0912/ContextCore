using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>
/// POLICY-01 行为快照测试：在将硬编码值迁移到 Policy/Profile/Registry 类之前，捕获当前行为。
/// 这些测试必须在迁移前后保持一致（stress-test 快照的 FinalScore=1.0 来自 Type 检查，不依赖夹具惩罚）。
/// </summary>
[TestClass]
[TestCategory("Snapshot")]
public sealed class ContextCorePolicyMigrationSnapshotTests
{
    [TestMethod]
    public async Task ModeBudgetSnapshot_ShouldResolveExactTokenBudgetsAndModeNameForEachMode()
    {
        var cases = new[]
        {
            new { Mode = ContextPackageMode.Chat, ModeName = "ChatMode", ExpectedBudget = 2_400 },
            new { Mode = ContextPackageMode.Novel, ModeName = "NovelMode", ExpectedBudget = 6_000 },
            new { Mode = ContextPackageMode.Automation, ModeName = "AutomationMode", ExpectedBudget = 4_000 },
            new { Mode = ContextPackageMode.Coding, ModeName = "CodingMode", ExpectedBudget = 5_000 }
        };

        foreach (var testCase in cases)
        {
            var now = DateTimeOffset.UtcNow;
            var contextStore = new InMemoryContextStore();
            var builder = new BasicContextPackageBuilder(
                contextStore,
                constraintStore: null,
                globalContextStore: null,
                memoryStore: null,
                relationStore: null);
            await contextStore.SaveAsync(CreateItem(
                $"{testCase.ModeName}-recent",
                $"{testCase.ModeName} 当前任务上下文，用于验证模式预算快照。",
                now));

            var result = await builder.BuildDetailedAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-snapshot",
                CollectionId = "collection-snapshot",
                Mode = testCase.Mode,
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "workspace-snapshot",
                    CollectionId = "collection-snapshot",
                    Mode = testCase.Mode,
                    IncludeGlobalContext = false,
                    IncludeHardConstraints = false,
                    IncludeSoftConstraints = false,
                    IncludeWorkingMemory = false,
                    IncludeStableMemory = false,
                    IncludeRecentRawContext = true
                }
            });

            Assert.AreEqual(testCase.ExpectedBudget, result.TokenBudget,
                $"{testCase.ModeName} 解析的 token 预算应匹配快照。");
            Assert.AreEqual(testCase.ExpectedBudget, result.Budget.TokenBudget,
                $"{testCase.ModeName} Budget.TokenBudget 应匹配快照。");
            Assert.AreEqual(testCase.ModeName, result.Metadata["budget.mode"],
                $"{testCase.ModeName} 元数据应包含模式名称。");
        }
    }

    [TestMethod]
    public async Task AuditModeDetectionSnapshot_ShouldIncludeDeprecatedItemsInAuditMode()
    {
        var now = DateTimeOffset.UtcNow;
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null);

        await memoryStore.SaveAsync(CreateMemory(
            "audit-deprecated-decision",
            "审计旧版文档：这是被废弃的旧版决策记录，仅在审计模式下应被召回。",
            now.AddMinutes(-30),
            importance: 0.4,
            status: ContextMemoryStatus.Deprecated));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-snapshot",
            CollectionId = "collection-snapshot",
            QueryText = "审计旧版文档",
            TokenBudget = 2_000,
            IsAuditMode = true,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-snapshot",
                CollectionId = "collection-snapshot",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                MaxRecentItems = 10
            }
        });

        var historicalSection = result.Package.Sections.FirstOrDefault(section => section.Name == "historical_context");

        Assert.IsNotNull(historicalSection, "审计模式下应生成 historical_context section。");
        StringAssert.Contains(historicalSection!.Content, "审计旧版文档",
            "审计模式下废弃项应被包含在 historical_context 中。");
        Assert.IsTrue(result.SelectedItems.Any(item => item.ItemId == "audit-deprecated-decision"),
            "废弃项应出现在 SelectedItems 中。");
        Assert.IsTrue(result.SelectedItems.Any(item =>
            item.ItemId == "audit-deprecated-decision" && item.SectionName == "historical_context"),
            "废弃项应归入 historical_context section。");
    }

    [TestMethod]
    public async Task StressTestScoringSnapshot_ShouldAssignFixedLowPlaceholderScore()
    {
        var now = DateTimeOffset.UtcNow;
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null);

        await memoryStore.SaveAsync(CreateMemory(
            "stress-test-placeholder",
            "stress-test 占位项，用于验证固定低分占位行为。",
            now,
            importance: 0.95,
            type: "stress-test"));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-snapshot",
            CollectionId = "collection-snapshot",
            TokenBudget = 2_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-snapshot",
                CollectionId = "collection-snapshot",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                MaxRecentItems = 10
            }
        });

        var stressItem = result.SelectedItems.FirstOrDefault(item => item.ItemId == "stress-test-placeholder");

        Assert.IsNotNull(stressItem, "stress-test 项应被选入工作记忆（无锚点时通过过滤）。");
        Assert.IsNotNull(stressItem!.ScoreBreakdown, "工作记忆项应填充 ScoreBreakdown。");
        Assert.AreEqual(1.0, stressItem.ScoreBreakdown!.FinalScore,
            "stress-test 类型项应获得固定低分占位 FinalScore=1.0。");
        Assert.AreEqual(1.0, stressItem.ScoreBreakdown.BaseScore,
            "stress-test 类型项 BaseScore 应为 1.0。");
    }

    [TestMethod]
    public async Task ChatModeKeywordBoostSnapshot_ShouldBoostStablePreferenceItem()
    {
        var now = DateTimeOffset.UtcNow;
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null);

        await memoryStore.SaveAsync(CreateMemory(
            "stable:generic-chat-baseline",
            "ChatMode 普通稳定记忆：历史背景说明。",
            now.AddMinutes(-1),
            importance: 0.95,
            layer: ContextMemoryLayer.Stable,
            status: ContextMemoryStatus.Stable));
        await memoryStore.SaveAsync(CreateMemory(
            "stable:preference-snapshot",
            "ChatMode stable preference：用户稳定偏好是中文输出。",
            now.AddMinutes(-10),
            importance: 0.25,
            layer: ContextMemoryLayer.Stable,
            status: ContextMemoryStatus.Stable));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-snapshot",
            CollectionId = "collection-snapshot",
            QueryText = "mode reserve regression",
            TokenBudget = 2_000,
            Mode = ContextPackageMode.Chat,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-snapshot",
                CollectionId = "collection-snapshot",
                TokenBudget = 2_000,
                Mode = ContextPackageMode.Chat,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = true,
                IncludeRecentRawContext = false,
                MaxRecentItems = 5
            }
        });

        AssertSelectedBefore(result, "stable:preference-snapshot", "stable:generic-chat-baseline");
    }

    private static void AssertSelectedBefore(
        ContextPackageBuildResult result,
        string expectedEarlier,
        string expectedLater)
    {
        var selected = result.SelectedItems
            .Select((item, index) => new { item.ItemId, Index = index })
            .ToArray();
        var earlier = selected.SingleOrDefault(item => item.ItemId == expectedEarlier);
        var later = selected.SingleOrDefault(item => item.ItemId == expectedLater);

        Assert.IsNotNull(earlier, $"未选中预期优先项：{expectedEarlier}");
        Assert.IsNotNull(later, $"未选中对照项：{expectedLater}");
        Assert.IsTrue(
            earlier.Index < later.Index,
            $"{expectedEarlier} 应排在 {expectedLater} 之前。当前顺序：{string.Join(", ", selected.Select(item => item.ItemId))}");
    }

    private static ContextItem CreateItem(
        string id,
        string content,
        DateTimeOffset updatedAt,
        IReadOnlyList<string>? tags = null)
    {
        return new ContextItem
        {
            Id = id,
            WorkspaceId = "workspace-snapshot",
            CollectionId = "collection-snapshot",
            Type = "note",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags ?? [],
            SourceRefs = [$"source:{id}"],
            Importance = 0.5,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }

    private static ContextMemoryItem CreateMemory(
        string id,
        string content,
        DateTimeOffset updatedAt,
        double importance,
        ContextMemoryLayer layer = ContextMemoryLayer.Working,
        ContextMemoryStatus status = ContextMemoryStatus.Verified,
        string? type = null)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-snapshot",
            CollectionId = "collection-snapshot",
            Layer = layer,
            Status = status,
            Type = type ?? "task-state",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = ["snapshot"],
            SourceRefs = [$"source:{id}"],
            Importance = importance,
            Confidence = 0.9,
            Version = 1,
            Metadata = new Dictionary<string, string>(),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }
}
