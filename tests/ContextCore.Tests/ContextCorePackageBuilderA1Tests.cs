using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.Tests;

/// <summary>覆盖 A1 Context Package Builder 主干中的短期筛选与锚点提取能力。</summary>
[TestClass]
public sealed class ContextCorePackageBuilderA1Tests
{
    [TestMethod]
    public void RecentContextFilter_ShouldKeepRelevantCurrentSignalsAndExplainExcludedItems()
    {
        var now = DateTimeOffset.UtcNow;
        var filter = new RecentContextFilter();
        var items = new[]
        {
            CreateItem(
                "recent-a1",
                "A1 PackageBuilder 需要继续实现 Recent Filter 和 Anchor Extraction。",
                now.AddMinutes(-5),
                tags: ["current", "package"]),
            CreateItem(
                "old-branch",
                "无关支线：一次性测试说明，后续不需要进入上下文包。",
                now.AddDays(-90),
                tags: ["branch"])
        };

        var result = filter.Filter(items, new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "继续 A1 PackageBuilder",
            RequiredTags = ["package"],
            Metadata = new Dictionary<string, string>
            {
                ["taskId"] = "task-a1"
            }
        }, take: 10, now);

        var relevant = result.Single(item => item.SourceItemId == "recent-a1");
        var excluded = result.Single(item => item.SourceItemId == "old-branch");

        Assert.IsNull(relevant.ExcludeReason);
        Assert.IsTrue(relevant.Relevance > excluded.Relevance);
        Assert.IsNotNull(excluded.ExcludeReason);
        StringAssert.Contains(relevant.Reason, "匹配");
    }

    [TestMethod]
    public void ContextAnchorExtractor_ShouldExtractMetadataQueryAndRecentAnchors()
    {
        var extractor = new ContextAnchorExtractor();
        var anchors = extractor.Extract(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "继续 A1 PackageBuilder 约束合并",
            RequiredTags = ["package"],
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode",
                ["project"] = "ContextCore",
                ["taskKind"] = "PackageBuilder"
            }
        }, [
            new RecentContextItem
            {
                SourceItemId = "recent-a1",
                Content = "短期上下文筛选需要保留当前任务和约束。",
                Relevance = 0.9,
                RecencyWeight = 0.8,
                Reason = "测试"
            }
        ]);

        Assert.IsTrue(anchors.Any(anchor => anchor.Type == AnchorType.Mode && anchor.Name == "CodingMode"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Type == AnchorType.Project && anchor.Name == "ContextCore"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Type == AnchorType.Task && anchor.Name == "PackageBuilder"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Type == AnchorType.Constraint && anchor.Name.Contains("约束")));
    }

    [TestMethod]
    public void ContextAnchorExtractor_ShouldNotContainFixtureKeywordOrStopWordTables()
    {
        var anchoringPath = Path.Combine(FindRepositoryRoot(), "src", "ContextCore.Core", "Services", "Anchoring");
        var sourceText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(anchoringPath, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        var forbiddenTerms = new[]
        {
            "林风",
            "苍穹大陆",
            "拍卖行",
            "九转金丹",
            "龙魂草",
            "王室血脉",
            "\"如何\"",
            "\"什么\"",
            "\"怎么\"",
            "\"为什么\""
        };

        foreach (var term in forbiddenTerms)
        {
            Assert.IsFalse(
                sourceText.Contains(term, StringComparison.OrdinalIgnoreCase),
                $"锚点提取逻辑不应包含硬编码夹具词或停用词：{term}");
        }
    }

    [TestMethod]
    public void RetrievalPlanner_ShouldBuildRetrievalRequestPlanWithSharedAnchorRules()
    {
        var planner = new RetrievalPlanner();

        var plan = planner.Plan(new ContextRetrievalRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "性能路径需要避免重复 IO",
            RequiredTags = ["performance"],
            RequiredTypes = ["rule"],
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode",
                ["project"] = "ContextCore",
                ["taskKind"] = "Retrieval"
            }
        });

        var anchors = plan.Snapshot?.Anchors ?? Array.Empty<ContextAnchor>();

        Assert.IsTrue(plan.NeedsStableMemory);
        Assert.IsTrue(anchors.Any(anchor => anchor.Source == "metadata:mode" && anchor.Name == "CodingMode"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Source == "metadata:project" && anchor.Name == "ContextCore"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Source == "request.requiredTags" && anchor.Name == "performance"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Source == "request.requiredTypes" && anchor.Name == "rule"));
        Assert.IsTrue(anchors.Any(anchor => anchor.Source == "request.query" && anchor.Name.Contains("性能")));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldFilterRecentContextAndWriteAnchorMetadata()
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
            "a1-relevant",
            "A1 PackageBuilder 当前任务：实现 Recent Filter 与 Anchor Extraction。",
            now.AddMinutes(-10),
            tags: ["package"]));
        await contextStore.SaveAsync(CreateItem(
            "a1-unrelated",
            "很久以前的一次性无关支线，不应该进入当前上下文包。",
            now.AddDays(-120),
            tags: ["archive"]));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 1_000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode",
                ["project"] = "ContextCore",
                ["taskKind"] = "PackageBuilder"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 10
            }
        });

        var recentSection = result.Package.Sections.Single(section => section.Name == "recent_context");

        StringAssert.Contains(recentSection.Content, "Recent Filter");
        Assert.IsFalse(recentSection.Content.Contains("无关支线", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(result.DroppedItems.Any(item => item.ItemId == "a1-unrelated"));
        Assert.IsTrue(int.Parse(result.Package.Metadata["anchor.count"]) >= 3);
        StringAssert.Contains(result.Package.Metadata["anchor.names"], "CodingMode");
        StringAssert.Contains(result.Package.Metadata["anchor.names"], "ContextCore");
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldRecallWorkingMemoryByAnchors()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null);

        await contextStore.SaveAsync(CreateItem(
            "recent-packagebuilder",
            "当前任务继续推进 PackageBuilder 的 Working Memory Recall。",
            now.AddMinutes(-5),
            tags: ["package"]));
        await memoryStore.SaveAsync(CreateMemory(
            "working-relevant",
            "PackageBuilder 当前处于 active 状态，需要优先召回中期任务状态。",
            now.AddMinutes(-2),
            importance: 0.4,
            metadata: new Dictionary<string, string> { ["state"] = "active" }));
        await memoryStore.SaveAsync(CreateMemory(
            "working-unrelated",
            "小说支线人物关系整理，当前任务不需要。",
            now.AddMinutes(-1),
            importance: 1.0));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "继续 PackageBuilder",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 1
            }
        });

        var workingSection = package.Sections.Single(section => section.Name == "working_memory");

        StringAssert.Contains(workingSection.Content, "PackageBuilder 当前处于 active 状态");
        Assert.IsFalse(workingSection.Content.Contains("小说支线", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldExpandGraphWithWhitelistDepthAndLimits()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var relationStore = new InMemoryRelationStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore: null,
            relationStore);

        await contextStore.SaveAsync(CreateItem(
            "graph-seed",
            "PackageBuilder 图谱扩展入口。",
            now.AddMinutes(-5),
            tags: ["package"]));
        await contextStore.SaveAsync(CreateItem(
            "graph-allowed",
            "一跳相关设计决策。",
            now.AddMinutes(-4)));
        await contextStore.SaveAsync(CreateItem(
            "graph-second-hop",
            "二跳依赖信息。",
            now.AddMinutes(-3)));
        await contextStore.SaveAsync(CreateItem(
            "graph-blocked",
            "重复噪音信息。",
            now.AddMinutes(-2)));

        await relationStore.SaveAsync(CreateRelation(
            "rel-allowed",
            "graph-seed",
            "graph-allowed",
            ContextRelationTypes.RelatedTo,
            confidence: 0.9));
        await relationStore.SaveAsync(CreateRelation(
            "rel-second",
            "graph-allowed",
            "graph-second-hop",
            ContextRelationTypes.DependsOn,
            confidence: 0.9));
        await relationStore.SaveAsync(CreateRelation(
            "rel-blocked",
            "graph-seed",
            "graph-blocked",
            ContextRelationTypes.Duplicates,
            confidence: 1.0));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "PackageBuilder 图谱扩展",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 1,
                Metadata = new Dictionary<string, string>
                {
                    ["relationExpansionDepth"] = "2",
                    ["relationMaxNodes"] = "2",
                    ["relationMinConfidence"] = "0.5"
                }
            }
        });

        var relatedSection = package.Sections.Single(section => section.Name == "related_context");

        StringAssert.Contains(relatedSection.Content, "一跳相关设计决策");
        StringAssert.Contains(relatedSection.Content, "二跳依赖信息");
        Assert.IsFalse(relatedSection.Content.Contains("重复噪音", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldExpandGraphFromWorkingMemoryEntitySeeds()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore);

        await contextStore.SaveAsync(CreateItem(
            "recent-graph-seed",
            "PackageBuilder 当前需要从中期记忆抽取图谱实体节点。",
            now.AddMinutes(-5),
            tags: ["package"]));
        await contextStore.SaveAsync(CreateItem(
            "entity-root",
            "中期记忆指向的实体根节点。",
            now.AddMinutes(-4)));
        await contextStore.SaveAsync(CreateItem(
            "entity-related",
            "由中期实体节点扩展出的关系内容。",
            now.AddMinutes(-3)));
        await memoryStore.SaveAsync(CreateMemory(
            "working-entity-seed",
            "PackageBuilder active 状态关联 context:entity-root，需要从该节点扩展关系。",
            now.AddMinutes(-2),
            importance: 0.7,
            metadata: new Dictionary<string, string>
            {
                ["state"] = "active",
                ["entityId"] = "entity-root"
            }));
        await relationStore.SaveAsync(CreateRelation(
            "rel-entity",
            "entity-root",
            "entity-related",
            ContextRelationTypes.RelatedTo,
            confidence: 0.9));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "PackageBuilder",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 2
            }
        });

        var relatedSection = package.Sections.Single(section => section.Name == "related_context");

        StringAssert.Contains(relatedSection.Content, "由中期实体节点扩展出的关系内容");
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldInjectStableMemoryByAnchorsAndWorkingSignals()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null);

        await contextStore.SaveAsync(CreateItem(
            "recent-stable",
            "当前继续 PackageBuilder 的 Stable Memory Injection。",
            now.AddMinutes(-5),
            tags: ["package"]));
        await memoryStore.SaveAsync(CreateMemory(
            "working-stable",
            "PackageBuilder 当前 active 状态需要参考长期项目背景。",
            now.AddMinutes(-3),
            importance: 0.6,
            metadata: new Dictionary<string, string>
            {
                ["state"] = "active",
                ["topic"] = "PackageBuilder"
            }));
        await memoryStore.SaveAsync(CreateMemory(
            "stable-relevant",
            "ContextCore PackageBuilder 长期项目背景：上下文包必须保持最小充分。",
            now.AddMinutes(-2),
            importance: 0.5,
            metadata: new Dictionary<string, string> { ["category"] = "project_background" },
            layer: ContextMemoryLayer.Stable,
            status: ContextMemoryStatus.Stable,
            tags: ["project", "package"]));
        await memoryStore.SaveAsync(CreateMemory(
            "stable-unrelated",
            "小说世界观长期设定：人物支线需要保留神秘感。",
            now.AddMinutes(-1),
            importance: 1.0,
            metadata: new Dictionary<string, string> { ["category"] = "worldbuilding" },
            layer: ContextMemoryLayer.Stable,
            status: ContextMemoryStatus.Stable,
            tags: ["fiction"]));

        var package = await builder.BuildAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "继续 PackageBuilder 稳定记忆注入",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 2
            }
        });

        var stableSection = package.Sections.Single(section => section.Name == "stable_memory");

        StringAssert.Contains(stableSection.Content, "上下文包必须保持最小充分");
        Assert.IsFalse(stableSection.Content.Contains("小说世界观", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldOutputExcludedUncertaintiesAndBudgetDiagnostics()
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
            "diag-relevant",
            "PackageBuilder 诊断输出需要保留当前任务上下文。",
            now.AddMinutes(-2),
            tags: ["current", "package"]));
        await contextStore.SaveAsync(CreateItem(
            "diag-unrelated",
            "无关支线：很早以前的一段临时说明，不应进入当前上下文包。",
            now.AddDays(-90),
            tags: ["branch"]));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 500,
            Metadata = new Dictionary<string, string>
            {
                ["includeDiagnosticsSections"] = "true"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 500,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 10
            }
        });

        var sectionNames = result.Package.Sections.Select(section => section.Name).ToArray();

        CollectionAssert.Contains(sectionNames, "excluded");
        CollectionAssert.Contains(sectionNames, "uncertainties");
        Assert.IsTrue(result.DroppedItems.Any(item => item.ItemId == "diag-unrelated"));
        Assert.IsTrue(result.Uncertainties.Any(item => item.Code == "ExcludedItems"));
        Assert.AreEqual(500, result.Budget.TokenBudget);
        Assert.IsTrue(result.Budget.UsedTokens > 0);
        Assert.IsTrue(result.Budget.WasteRatio >= 0);
        Assert.IsTrue(result.Budget.Sections.Any(item => item.SectionName == "recent_context"));
        Assert.AreEqual("1", result.Metadata["diagnostics.droppedItems"]);
        Assert.IsTrue(result.Package.Metadata.ContainsKey("budget.usedTokens"));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldProtectMustHitBeforeLowValueItemsUnderBudgetPressure()
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
            "memory:chat-active-plan",
            "当前计划：本轮必须保留用户正在执行的 active plan。",
            now.AddMinutes(-5),
            importance: 0.3,
            metadata: new Dictionary<string, string> { ["state"] = "active" }));
        await memoryStore.SaveAsync(CreateMemory(
            "memory:low-value-noise",
            "低预算上下文包 stress-test 无用字符 " + new string('低', 800),
            now.AddMinutes(-1),
            importance: 0.95));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "低预算上下文包",
            TokenBudget = 120,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "ChatMode",
                ["eval.mustHit"] = "memory:chat-active-plan"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 120,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                MaxRecentItems = 2,
                SectionTokenBudgets = new Dictionary<string, int>
                {
                    ["working_memory"] = 24
                }
            }
        });

        Assert.IsTrue(result.SelectedItems.Any(item => item.ItemId == "memory:chat-active-plan"));
        Assert.IsFalse(result.SelectedItems.Any(item => item.ItemId == "memory:low-value-noise"));
        Assert.AreEqual(
            "memory:chat-active-plan",
            result.SelectedItems.First(item => item.SectionName == "working_memory").ItemId);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldAppendExplicitMustHitBeyondWorkingMemoryTopN()
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
            "memory:high-score-context",
            "高分普通上下文：当前查询直接命中。",
            now.AddMinutes(-1),
            importance: 0.95,
            metadata: new Dictionary<string, string> { ["mode"] = "NovelMode" }));
        await memoryStore.SaveAsync(CreateMemory(
            "memory:active-character-state",
            "人物状态：主角受伤状态仍在，人物状态必须连续。",
            now.AddMinutes(-20),
            importance: 0.2,
            metadata: new Dictionary<string, string> { ["mode"] = "NovelMode" }));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "当前查询",
            TokenBudget = 1_000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "NovelMode",
                ["eval.mustHit"] = "memory:active-character-state"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                MaxRecentItems = 1
            }
        });

        Assert.IsTrue(result.SelectedItems.Any(item => item.ItemId == "memory:active-character-state"));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldIsolateDiagnosticsBudgetFromNormalContext()
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
            "memory:important-working",
            "当前任务关键工作记忆：必须保留主流程上下文，诊断预算是否隔离仍需确认。",
            now.AddMinutes(-5),
            importance: 0.95,
            metadata: new Dictionary<string, string>
            {
                ["state"] = "active",
                ["mode"] = "CodingMode"
            }));

        for (var i = 0; i < 6; i++)
        {
            await memoryStore.SaveAsync(CreateMemory(
                $"memory:diagnostic-noise-{i}",
                "主流程上下文 stress-test 诊断噪声 " + new string('诊', 500),
                now.AddMinutes(i),
                importance: 0.4,
                metadata: new Dictionary<string, string> { ["mode"] = "CodingMode" }));
        }

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "主流程上下文",
            TokenBudget = 800,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode",
                ["expectedUncertainties"] = "诊断预算是否隔离",
                ["includeDiagnosticsSections"] = "true"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 800,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                MaxRecentItems = 6,
                SectionTokenBudgets = new Dictionary<string, int>
                {
                    ["working_memory"] = 90
                }
            }
        });

        var sectionBudgets = result.Budget.Sections.ToDictionary(item => item.SectionName, StringComparer.OrdinalIgnoreCase);

        Assert.IsTrue(result.SelectedItems.Any(item => item.ItemId == "memory:important-working"));
        CollectionAssert.Contains(result.Package.Sections.Select(section => section.Name).ToArray(), "uncertainties");
        if (sectionBudgets.TryGetValue("excluded", out var excludedBudget))
        {
            Assert.IsTrue(excludedBudget.AllocatedTokens <= 32);
        }
        Assert.IsTrue(sectionBudgets["uncertainties"].AllocatedTokens <= 32);
        Assert.IsTrue(sectionBudgets["working_memory"].AllocatedTokens > sectionBudgets["uncertainties"].AllocatedTokens);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldReserveAutomationRecoverySignals()
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
            "memory:generic-automation-note",
            "自动化普通说明：只记录工具运行背景。",
            now.AddMinutes(-1),
            importance: 0.95,
            metadata: new Dictionary<string, string> { ["mode"] = "AutomationMode" }));
        await memoryStore.SaveAsync(CreateMemory(
            "memory:automation-recovery",
            "AutomationMode recovery point：last error 后的恢复点、retry policy 和 dead-letter state 必须优先。",
            now.AddMinutes(-10),
            importance: 0.3,
            metadata: new Dictionary<string, string> { ["mode"] = "AutomationMode" }));

        var result = await BuildModeReservePackageAsync(builder, "AutomationMode", includeStableMemory: false);

        AssertSelectedBefore(result, "memory:automation-recovery", "memory:generic-automation-note");
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldReserveNovelForeshadowingAndItemState()
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
            "memory:novel-generic-note",
            "NovelMode 普通章节备注。",
            now.AddMinutes(-1),
            importance: 0.95,
            metadata: new Dictionary<string, string> { ["mode"] = "NovelMode" }));
        await memoryStore.SaveAsync(CreateMemory(
            "foreshadow:bell-sound",
            "NovelMode foreshadow 伏笔铃声：需要确认兑现方式可多选。",
            now.AddMinutes(-8),
            importance: 0.3,
            metadata: new Dictionary<string, string> { ["mode"] = "NovelMode" }));
        await memoryStore.SaveAsync(CreateMemory(
            "item:sword-broken",
            "NovelMode item-state 物品状态：主角的断剑仍是 broken state。",
            now.AddMinutes(-9),
            importance: 0.25,
            metadata: new Dictionary<string, string>
            {
                ["mode"] = "NovelMode",
                ["signal"] = "item-state"
            }));

        var result = await BuildModeReservePackageAsync(builder, "NovelMode", includeStableMemory: false);

        AssertSelectedBefore(result, "foreshadow:bell-sound", "memory:novel-generic-note");
        AssertSelectedBefore(result, "item:sword-broken", "memory:novel-generic-note");
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldReserveChatStablePreference()
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
            "stable:generic-chat-memory",
            "ChatMode 普通稳定记忆：历史背景。",
            now.AddMinutes(-1),
            importance: 0.95,
            metadata: new Dictionary<string, string> { ["mode"] = "ChatMode" },
            layer: ContextMemoryLayer.Stable,
            status: ContextMemoryStatus.Stable));
        await memoryStore.SaveAsync(CreateMemory(
            "stable:preference-language",
            "ChatMode stable preference：用户稳定偏好是中文输出、scope boundary 清晰、active task 优先。",
            now.AddMinutes(-10),
            importance: 0.25,
            metadata: new Dictionary<string, string>
            {
                ["mode"] = "ChatMode",
                ["signal"] = "preference-language"
            },
            layer: ContextMemoryLayer.Stable,
            status: ContextMemoryStatus.Stable));

        var result = await BuildModeReservePackageAsync(builder, "ChatMode", includeWorkingMemory: false);

        AssertSelectedBefore(result, "stable:preference-language", "stable:generic-chat-memory");
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldApplyModeBasedBudgetsForSupportedModes()
    {
        var cases = new[]
        {
            new { Mode = "ChatMode", DefaultBudget = 2_400, RecentBudget = 672 },
            new { Mode = "NovelMode", DefaultBudget = 6_000, RecentBudget = 1_080 },
            new { Mode = "AutomationMode", DefaultBudget = 4_000, RecentBudget = 640 },
            new { Mode = "CodingMode", DefaultBudget = 5_000, RecentBudget = 1_000 }
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
                $"{testCase.Mode}-recent",
                $"{testCase.Mode} 当前任务上下文，用于验证模式预算。",
                now));

            var result = await builder.BuildDetailedAsync(new ContextPackageRequest
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                Metadata = new Dictionary<string, string>
                {
                    ["mode"] = testCase.Mode
                },
                Policy = new ContextPackagePolicy
                {
                    WorkspaceId = "workspace-test",
                    CollectionId = "collection-test",
                    IncludeGlobalContext = false,
                    IncludeHardConstraints = false,
                    IncludeSoftConstraints = false,
                    IncludeWorkingMemory = false,
                    IncludeStableMemory = false,
                    IncludeRecentRawContext = true
                }
            });

            var sectionBudget = result.Budget.Sections.Single(item => item.SectionName == "recent_context");

            Assert.AreEqual(testCase.DefaultBudget, result.TokenBudget);
            Assert.AreEqual(testCase.DefaultBudget, result.Budget.TokenBudget);
            Assert.AreEqual(testCase.Mode, result.Metadata["budget.mode"]);
            Assert.AreEqual(testCase.RecentBudget, sectionBudget.AllocatedTokens);
        }
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldPreferExplicitSectionBudgetOverModeProfile()
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
            "coding-explicit-budget",
            new string('编', 400),
            now));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 1_000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                SectionTokenBudgets = new Dictionary<string, int>
                {
                    ["recent_context"] = 20
                }
            }
        });

        var section = result.Package.Sections.Single(item => item.Name == "recent_context");
        var sectionBudget = result.Budget.Sections.Single(item => item.SectionName == "recent_context");

        Assert.AreEqual(20, sectionBudget.AllocatedTokens);
        Assert.IsTrue(section.EstimatedTokens <= 20);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldBuildCurrentTaskAndEvidenceSectionsWhenEnabled()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null,
            workingMemoryService: memoryStore);

        await memoryStore.SetCurrentTaskAsync(new WorkingMemoryCurrentTask
        {
            TaskId = "task-a1-budget",
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Title = "补齐 current task 与 evidence 预算",
            Description = "当前任务需要在上下文包中输出任务摘要和来源证据索引。",
            Status = "active",
            Tags = ["package", "budget"],
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode",
                ["project"] = "ContextCore"
            },
            CreatedAt = now,
            UpdatedAt = now
        });
        await contextStore.SaveAsync(CreateItem(
            "evidence-source",
            "补齐 current task 和 evidence：用于验证 evidence section 的原始上下文。",
            now,
            tags: ["budget"]));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "补齐 current task 和 evidence",
            RequiredTags = ["budget"],
            TokenBudget = 1_000,
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = "CodingMode",
                ["includeCurrentTaskSection"] = "true",
                ["includeEvidenceSection"] = "true"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = true,
                MaxRecentItems = 10,
                Metadata = new Dictionary<string, string>
                {
                    ["includeCurrentTaskSection"] = "true",
                    ["includeEvidenceSection"] = "true"
                }
            }
        });

        var sectionNames = result.Package.Sections.Select(section => section.Name).ToArray();
        var currentTask = result.Package.Sections.Single(section => section.Name == "current_task");
        var evidence = result.Package.Sections.Single(section => section.Name == "evidence");
        var currentTaskBudget = result.Budget.Sections.Single(section => section.SectionName == "current_task");
        var evidenceBudget = result.Budget.Sections.Single(section => section.SectionName == "evidence");

        CollectionAssert.Contains(sectionNames, "current_task");
        CollectionAssert.Contains(sectionNames, "evidence");
        StringAssert.Contains(currentTask.Content, "补齐 current task 与 evidence 预算");
        StringAssert.Contains(evidence.Content, "evidence-source");
        StringAssert.Contains(evidence.Content, "source:evidence-source");
        Assert.AreEqual(120, currentTaskBudget.AllocatedTokens);
        Assert.AreEqual(160, evidenceBudget.AllocatedTokens);
        Assert.IsTrue(result.SelectedItems.Any(item =>
            item.SectionName == "current_task"
            && item.Kind == "current_task"));
        Assert.IsNotNull(result.Output.CurrentTask);
        StringAssert.Contains(result.Output.CurrentTask!.Content, "补齐 current task 与 evidence 预算");
        Assert.IsTrue(result.Output.RecentContext.Any(item => item.Content.Contains("evidence section")));
        Assert.IsTrue(result.Output.Evidence.Any(item => item.Content.Contains("source:evidence-source")));
        Assert.AreEqual(result.DroppedItems.Count, result.Output.Excluded.Count);
        Assert.AreEqual(result.Budget.TokenBudget, result.Output.Budget.TokenBudget);
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldReportSupersededVersionConflictAndLowConfidenceRelation()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var relationStore = new InMemoryRelationStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore);

        await contextStore.SaveAsync(CreateItem(
            "low-confidence-target",
            "低置信度关系目标，不应被直接召回。",
            now.AddMinutes(-4)));
        await memoryStore.SaveAsync(CreateMemory(
            "decision-v1",
            "旧版决策，已经被新版替代。",
            now.AddMinutes(-3),
            importance: 0.6,
            metadata: new Dictionary<string, string>
            {
                ["state"] = "active",
                ["entityId"] = "decision-A",
                ["version"] = "1",
                ["supersededBy"] = "decision-v2",
                ["priorityScope"] = "stable"
            }));
        await memoryStore.SaveAsync(CreateMemory(
            "decision-v2",
            "新版决策，应优先使用。",
            now.AddMinutes(-2),
            importance: 0.7,
            metadata: new Dictionary<string, string>
            {
                ["state"] = "active",
                ["entityId"] = "decision-A",
                ["version"] = "2",
                ["priorityScope"] = "current-input"
            }));
        await relationStore.SaveAsync(CreateRelation(
            "low-rel",
            "decision-v1",
            "low-confidence-target",
            ContextRelationTypes.RelatedTo,
            confidence: 0.1));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "PackageBuilder 决策冲突",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                MaxRecentItems = 10,
                Metadata = new Dictionary<string, string>
                {
                    ["relationMinConfidence"] = "0.5"
                }
            }
        });

        var codes = result.Uncertainties.Select(item => item.Code).ToArray();

        CollectionAssert.Contains(codes, "SupersededSelectedItem");
        CollectionAssert.Contains(codes, "EntityVersionConflict");
        CollectionAssert.Contains(codes, "LowConfidenceRelation");
        Assert.IsFalse(result.Package.Sections.Any(section =>
            section.Content.Contains("低置信度关系目标", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldKeepMergedConstraintsSectionDisabledByDefault()
    {
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint(
            "runtime-default-off",
            ConstraintLevel.Runtime,
            "默认关闭时，运行时约束不应自动进入合并 constraints section。"));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false
            }
        });

        Assert.IsFalse(result.Package.Sections.Any(section => section.Name == "constraints"));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldBuildMergedConstraintsSectionWithPriorityOrder()
    {
        var constraintStore = new InMemoryConstraintStore();
        var builder = new BasicContextPackageBuilder(
            new InMemoryContextStore(),
            constraintStore,
            globalContextStore: null,
            memoryStore: null,
            relationStore: null);

        await constraintStore.SaveAsync(CreateConstraint(
            "domain-soft",
            ConstraintLevel.Domain,
            "领域软约束：中文上下文优先保留原始术语。",
            metadata: new Dictionary<string, string> { ["scope"] = "domain-soft" }));
        await constraintStore.SaveAsync(CreateConstraint(
            "user-stable",
            ConstraintLevel.User,
            "用户稳定偏好：输出、日志和提示信息保持中文。",
            metadata: new Dictionary<string, string> { ["scope"] = "user-stable" }));
        await constraintStore.SaveAsync(CreateConstraint(
            "project-hard",
            ConstraintLevel.Hard,
            "项目硬约束：文件默认写入项目内专用目录。",
            metadata: new Dictionary<string, string>
            {
                ["constraintScope"] = "project",
                ["project"] = "ContextCore"
            }));
        await constraintStore.SaveAsync(CreateConstraint(
            "mode-soft",
            ConstraintLevel.Soft,
            "模式约束：CodingMode 下优先保持实现可验证。",
            metadata: new Dictionary<string, string>
            {
                ["constraintScope"] = "mode",
                ["mode"] = "CodingMode"
            }));
        await constraintStore.SaveAsync(CreateConstraint(
            "runtime",
            ConstraintLevel.Runtime,
            "运行时约束：本次构建不得写入明文密钥。",
            metadata: new Dictionary<string, string> { ["scope"] = "runtime" }));
        await constraintStore.SaveAsync(CreateConstraint(
            "system-safety",
            ConstraintLevel.System,
            "系统安全约束：不能输出用户私有密钥。",
            metadata: new Dictionary<string, string> { ["scope"] = "safety" }));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            TokenBudget = 2_000,
            Metadata = new Dictionary<string, string>
            {
                ["currentConstraints"] = "当前输入约束：优先补齐上一项未完成任务。"
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 2_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = false,
                IncludeStableMemory = false,
                IncludeRecentRawContext = false,
                Metadata = new Dictionary<string, string>
                {
                    ["includeMergedConstraintsSection"] = "true"
                }
            }
        });

        var section = result.Package.Sections.Single(section => section.Name == "constraints");

        StringAssert.Contains(section.Content, "[系统/安全 | System]");
        StringAssert.Contains(section.Content, "[当前输入 | Runtime]");
        StringAssert.Contains(section.Content, "[运行时 | Runtime]");
        StringAssert.Contains(section.Content, "[模式 | Soft]");
        StringAssert.Contains(section.Content, "[项目 | Hard]");
        StringAssert.Contains(section.Content, "[用户稳定 | User]");
        StringAssert.Contains(section.Content, "[领域软约束 | Domain]");
        AssertTextOrder(
            section.Content,
            "系统安全约束",
            "当前输入约束",
            "运行时约束",
            "模式约束",
            "项目硬约束",
            "用户稳定偏好",
            "领域软约束");
        Assert.IsTrue(result.SelectedItems.Any(item =>
            item.SectionName == "constraints"
            && item.Kind == "merged_constraint"));
    }

    [TestMethod]
    public async Task PackageBuilderPolicy_ShouldNotPersistOrVectorizeShortTermContextByDefault()
    {
        var now = DateTimeOffset.UtcNow;
        var contextStore = new InMemoryContextStore();
        var memoryStore = new InMemoryMemoryStore();
        var vectorStore = new InMemoryVectorStore();
        var builder = new BasicContextPackageBuilder(
            contextStore,
            constraintStore: null,
            globalContextStore: null,
            memoryStore,
            relationStore: null);

        await contextStore.SaveAsync(CreateItem(
            "short-term-dialogue",
            "短期对话：这里只是本轮执行过程中的临时讨论，不应默认进入工作记忆、稳定记忆或向量索引。",
            now,
            tags: ["runtime"]));

        var result = await builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "短期对话",
            RequiredTags = ["runtime"],
            TokenBudget = 1_000,
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 10
            }
        });

        var workingItems = await memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Working,
            Take = 10
        });
        var stableItems = await memoryStore.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = ContextMemoryLayer.Stable,
            Take = 10
        });
        var vectors = await vectorStore.SearchAsync(new VectorQuery
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Vector = [1f, 0f],
            TopK = 10,
            IncludeVector = false
        });

        var recentSection = result.Package.Sections.Single(section => section.Name == "recent_context");

        StringAssert.Contains(recentSection.Content, "短期对话");
        Assert.AreEqual(0, workingItems.Count);
        Assert.AreEqual(0, stableItems.Count);
        Assert.AreEqual(0, vectors.Count);
    }

    private static Task<ContextPackageBuildResult> BuildModeReservePackageAsync(
        BasicContextPackageBuilder builder,
        string mode,
        bool includeWorkingMemory = true,
        bool includeStableMemory = true)
    {
        return builder.BuildDetailedAsync(new ContextPackageRequest
        {
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            QueryText = "mode reserve regression",
            TokenBudget = 1_000,
            Mode = mode switch
            {
                "ChatMode" => ContextPackageMode.Chat,
                "NovelMode" => ContextPackageMode.Novel,
                "AutomationMode" => ContextPackageMode.Automation,
                "CodingMode" => ContextPackageMode.Coding,
                _ => ContextPackageMode.None
            },
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = mode
            },
            Policy = new ContextPackagePolicy
            {
                WorkspaceId = "workspace-test",
                CollectionId = "collection-test",
                TokenBudget = 1_000,
                IncludeGlobalContext = false,
                IncludeHardConstraints = false,
                IncludeSoftConstraints = false,
                IncludeWorkingMemory = includeWorkingMemory,
                IncludeStableMemory = includeStableMemory,
                IncludeRecentRawContext = false,
                MaxRecentItems = 5,
                Mode = mode switch
                {
                    "ChatMode" => ContextPackageMode.Chat,
                    "NovelMode" => ContextPackageMode.Novel,
                    "AutomationMode" => ContextPackageMode.Automation,
                    "CodingMode" => ContextPackageMode.Coding,
                    _ => ContextPackageMode.None
                }
            }
        });
    }

    private static void AssertSelectedBefore(
        ContextPackageBuildResult result,
        string expectedEarlier,
        string expectedLater)
    {
        var selected = result.SelectedItems.Select((item, index) => new { item.ItemId, Index = index }).ToArray();
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
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
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

    private static ContextConstraint CreateConstraint(
        string id,
        ConstraintLevel level,
        string content,
        Dictionary<string, string>? metadata = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ContextConstraint
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Scope = ContextScope.Collection,
            Level = level,
            Content = content,
            SourceRefs = [$"source:{id}"],
            Status = ContextMemoryStatus.Verified,
            Confidence = 0.9,
            Metadata = metadata ?? new Dictionary<string, string>(),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static ContextMemoryItem CreateMemory(
        string id,
        string content,
        DateTimeOffset updatedAt,
        double importance,
        Dictionary<string, string>? metadata = null,
        ContextMemoryLayer layer = ContextMemoryLayer.Working,
        ContextMemoryStatus status = ContextMemoryStatus.Verified,
        IReadOnlyList<string>? tags = null)
    {
        return new ContextMemoryItem
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            Layer = layer,
            Status = status,
            Type = "task-state",
            Content = content,
            ContentFormat = ContextContentFormat.PlainText,
            Tags = tags ?? ["package"],
            SourceRefs = [$"source:{id}"],
            Importance = importance,
            Confidence = 0.9,
            Version = 1,
            Metadata = metadata ?? new Dictionary<string, string>(),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
    }

    private static ContextRelation CreateRelation(
        string id,
        string sourceId,
        string targetId,
        string relationType,
        double confidence)
    {
        return new ContextRelation
        {
            Id = id,
            WorkspaceId = "workspace-test",
            CollectionId = "collection-test",
            SourceId = sourceId,
            TargetId = targetId,
            RelationType = relationType,
            Weight = 1.0,
            Confidence = confidence,
            SourceRefs = [$"source:{id}"],
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static void AssertTextOrder(string content, params string[] orderedFragments)
    {
        var previousIndex = -1;
        foreach (var fragment in orderedFragments)
        {
            var index = content.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
            Assert.IsTrue(index >= 0, $"未找到片段：{fragment}");
            Assert.IsTrue(index > previousIndex, $"片段顺序不正确：{fragment}");
            previousIndex = index;
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "ContextCore.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 ContextCore.sln，无法定位仓库根目录。");
    }
}
