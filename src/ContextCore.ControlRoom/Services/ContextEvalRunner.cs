using System.Text;
using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

/// <summary>
/// 上下文评测运行器，支持 Chat, Project, Novel, Automation, Coding 多场景上下文评测。
/// 采用隔离的 InMemory 存储运行评测，不污染物理存储。
/// </summary>
public sealed class ContextEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly RetrievalAttentionRerankOptions? _attentionRerankOptions;
    private readonly GraphExpansionApplyOptions? _graphExpansionApplyOptions;

    public ContextEvalRunner(
        RetrievalAttentionRerankOptions? attentionRerankOptions = null,
        GraphExpansionApplyOptions? graphExpansionApplyOptions = null)
    {
        _attentionRerankOptions = attentionRerankOptions;
        _graphExpansionApplyOptions = graphExpansionApplyOptions;
    }

    /// <summary>
    /// 运行指定目录下的评测。
    /// </summary>
    /// <param name="contextsRootPath">评测 contexts 目录（例如 eval/contexts/）</param>
    /// <param name="categoryFilter">可选分类过滤（如 chat, project, novel 等）</param>
    /// <param name="includeSeedBatches">是否纳入 seed*.json / corpus*.json 扩展批次。默认只跑稳定基线 seed_samples.json。</param>
    public async Task<ContextEvalReport> RunAsync(
        string contextsRootPath,
        string? categoryFilter = null,
        bool includeSeedBatches = false)
    {
        var results = new List<ContextEvalResult>();
        var categories = new[] { "chat", "project", "novel", "automation", "coding-mode" };

        foreach (var category in categories)
        {
            if (categoryFilter is not null && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRootPath, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            // 1. 加载语料和样本。默认只跑稳定基线；显式 includeSeedBatches 时纳入增量批次做探索评测。
            var corpus = await LoadCategoryCorpusAsync(categoryDir, includeSeedBatches).ConfigureAwait(false);
            var samples = await LoadCategorySamplesAsync(categoryDir, includeSeedBatches).ConfigureAwait(false);
            if (samples.Count == 0)
            {
                continue;
            }

            // 2. 初始化 InMemory 隔离状态
            var workspaceId = $"eval-{category}";
            var collectionId = "test";
            var state = ControlRoomService.CreateState(
                "memory",
                "eval",
                workspaceId,
                collectionId,
                attentionRerankOptions: _attentionRerankOptions,
                graphExpansionApplyOptions: _graphExpansionApplyOptions);

            // 3. 灌入语料数据
            if (corpus is not null)
            {
                foreach (var ctx in corpus.Contexts)
                {
                    var normalizedCtx = new ContextItem
                    {
                        Id = ctx.Id,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        Type = ctx.Type,
                        Title = ctx.Title,
                        Content = ctx.Content,
                        ContentFormat = ctx.ContentFormat,
                        Tags = ctx.Tags,
                        Refs = ctx.Refs,
                        SourceRefs = ctx.SourceRefs,
                        Metadata = ctx.Metadata,
                        Importance = ctx.Importance,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await state.ContextStore.SaveAsync(normalizedCtx);
                }

                foreach (var mem in corpus.Memories)
                {
                    var normalizedMem = new ContextMemoryItem
                    {
                        Id = mem.Id,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        Layer = mem.Layer,
                        Status = mem.Status,
                        Type = mem.Type,
                        Content = mem.Content,
                        ContentFormat = mem.ContentFormat,
                        Tags = mem.Tags,
                        SourceRefs = mem.SourceRefs,
                        RelationRefs = mem.RelationRefs,
                        Importance = mem.Importance,
                        Confidence = mem.Confidence,
                        Version = mem.Version <= 0 ? 1 : mem.Version,
                        Metadata = mem.Metadata,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await state.MemoryStore.SaveAsync(normalizedMem);
                }

                var relationTypeNormalizer = new RelationTypeNormalizer();
                foreach (var rel in corpus.Relations)
                {
                    var normalizedRel = new ContextRelation
                    {
                        Id = rel.Id,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        SourceId = rel.SourceId,
                        TargetId = rel.TargetId,
                        RelationType = rel.RelationType,
                        Weight = rel.Weight,
                        Confidence = rel.Confidence,
                        SourceRefs = rel.SourceRefs,
                        Metadata = new Dictionary<string, string>(rel.Metadata, StringComparer.OrdinalIgnoreCase),
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    await state.RelationStore
                        .SaveAsync(relationTypeNormalizer.NormalizeAndBackfillFixtureRelation(
                            normalizedRel,
                            "context-eval-runner"))
                        .ConfigureAwait(false);
                }

                foreach (var cst in corpus.Constraints)
                {
                    var normalizedCst = new ContextConstraint
                    {
                        Id = cst.Id,
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        Level = cst.Level,
                        Status = cst.Status,
                        Scope = cst.Scope,
                        Content = cst.Content,
                        Metadata = cst.Metadata,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await state.ConstraintStore.SaveAsync(normalizedCst);
                }

                // 4. 向量化语料
                var memoryIds = corpus.Memories
                    .Select(memory => memory.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var inputs = corpus.Contexts.Select(c => new EmbeddingInput
                {
                    Id = c.Id,
                    Text = $"{c.Title} {c.Content}",
                    SourceRef = c.Id
                }).Concat(corpus.Memories.Select(m => new EmbeddingInput
                {
                    Id = m.Id,
                    Text = m.Content,
                    SourceRef = m.Id
                })).ToList();

                if (inputs.Count > 0)
                {
                    var embedRequest = new EmbeddingRequest
                    {
                        WorkspaceId = workspaceId,
                        CollectionId = collectionId,
                        InputKind = EmbeddingInputKind.Text,
                        Inputs = inputs
                    };
                    var embedResult = await state.EmbeddingProvider.EmbedAsync(embedRequest);
                    foreach (var vec in embedResult.Vectors)
                    {
                        await state.VectorStore.UpsertAsync(new VectorRecord
                        {
                            Id = "vec-" + vec.InputId,
                            WorkspaceId = workspaceId,
                            CollectionId = collectionId,
                            SourceId = vec.SourceRef,
                            SourceKind = memoryIds.Contains(vec.SourceRef) ? "memory" : "context",
                            ModelName = embedResult.ModelName,
                            Dimensions = embedResult.Dimensions,
                            Vector = vec.Values,
                            ContentHash = vec.Metadata.TryGetValue("contentHash", out var h) ? h : vec.InputId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            UpdatedAt = DateTimeOffset.UtcNow
                        });
                    }
                }
            }

            // 5. 逐个评测样本
            foreach (var sample in samples)
            {
                try
                {
                    var result = await EvaluateSampleAsync(
                        state,
                        workspaceId,
                        collectionId,
                        sample,
                        corpus?.ActivatedConstraintGaps ?? Array.Empty<ConstraintGapCandidate>());
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    results.Add(new ContextEvalResult
                    {
                        SampleId = sample.Id,
                        Query = sample.Query,
                        Mode = sample.Mode,
                        Succeeded = false,
                        ErrorMessage = ex.Message,
                        MustHit = sample.MustHit,
                        MustNotHit = sample.MustNotHit,
                        ExpectedConstraints = sample.ExpectedConstraints,
                        ExpectedEntities = sample.ExpectedEntities,
                        GoldenNotes = sample.GoldenNotes
                    });
                }
            }
        }

        return BuildReport(results);
    }

    private static async Task<IReadOnlyList<ContextEvalSample>> LoadCategorySamplesAsync(
        string categoryDir,
        bool includeSeedBatches)
    {
        if (includeSeedBatches)
        {
            var result = await new ContextEvalSampleLoader().LoadAsync(categoryDir).ConfigureAwait(false);
            return result.Samples;
        }

        var samplesPath = Path.Combine(categoryDir, "seed_samples.json");
        if (!File.Exists(samplesPath))
        {
            return [];
        }

        return JsonSerializer.Deserialize<IReadOnlyList<ContextEvalSample>>(
            await File.ReadAllTextAsync(samplesPath).ConfigureAwait(false),
            JsonOptions) ?? [];
    }

    private static async Task<ContextEvalCorpus> LoadCategoryCorpusAsync(
        string categoryDir,
        bool includeSeedBatches)
    {
        var contexts = new Dictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        var memories = new Dictionary<string, ContextMemoryItem>(StringComparer.OrdinalIgnoreCase);
        var relations = new Dictionary<string, ContextRelation>(StringComparer.OrdinalIgnoreCase);
        var constraints = new Dictionary<string, ContextConstraint>(StringComparer.OrdinalIgnoreCase);
        var activatedConstraintGaps = new Dictionary<string, ConstraintGapCandidate>(StringComparer.OrdinalIgnoreCase);
        var corpusFiles = includeSeedBatches
            ? Directory.EnumerateFiles(categoryDir, "corpus*.json", SearchOption.TopDirectoryOnly)
            : File.Exists(Path.Combine(categoryDir, "corpus.json"))
                ? [Path.Combine(categoryDir, "corpus.json")]
                : Enumerable.Empty<string>();

        foreach (var file in corpusFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            var corpus = JsonSerializer.Deserialize<ContextEvalCorpus>(json, JsonOptions) ?? new ContextEvalCorpus();
            foreach (var item in corpus.Contexts)
            {
                contexts[item.Id] = item;
            }

            foreach (var item in corpus.Memories)
            {
                memories[item.Id] = item;
            }

            foreach (var item in corpus.Relations)
            {
                relations[item.Id] = item;
            }

            foreach (var item in corpus.Constraints)
            {
                constraints[item.Id] = item;
            }

            foreach (var item in corpus.ActivatedConstraintGaps)
            {
                activatedConstraintGaps[item.GapId] = item;
            }
        }

        return new ContextEvalCorpus
        {
            Contexts = contexts.Values.ToArray(),
            Memories = memories.Values.ToArray(),
            Relations = relations.Values.ToArray(),
            Constraints = constraints.Values.ToArray(),
            ActivatedConstraintGaps = activatedConstraintGaps.Values.ToArray()
        };
    }

    private static async Task<IReadOnlyList<ContextConstraint>> ActivateConstraintGapFixturesForSampleAsync(
        ControlRoomState state,
        IReadOnlyList<ConstraintGapCandidate> activatedConstraintGaps,
        string workspaceId,
        string collectionId,
        string sampleId)
    {
        var fixtures = activatedConstraintGaps
            .Where(item => string.Equals(item.SourceSampleId, sampleId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (fixtures.Length == 0)
        {
            return Array.Empty<ContextConstraint>();
        }

        var gapStore = new InMemoryConstraintGapCandidateStore();
        var gapService = new ConstraintGapCandidateService(gapStore, state.ConstraintStore);
        var candidateReviewService = new CandidateConstraintReviewService(
            state.ConstraintStore,
            new InMemoryCandidateConstraintReviewStore());
        var activatedConstraints = new List<ContextConstraint>();

        foreach (var fixture in fixtures)
        {
            var gap = NormalizeActivatedConstraintGapFixture(fixture, workspaceId, collectionId);
            var saved = await gapStore.SaveAsync(gap).ConfigureAwait(false);
            var accepted = await gapService.AcceptAsync(
                saved.GapId,
                new ConstraintGapReviewRequest
                {
                    OperationId = $"eval-constraint-gap-accept-{saved.GapId}",
                    Reviewer = "eval-fixture",
                    Reason = "accepted eval constraint fixture through formal gap review chain",
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = "context-eval-runner",
                        ["fixturePhase"] = "P15"
                    }
                }).ConfigureAwait(false);

            if (accepted?.CreatedConstraintId is null)
            {
                throw new InvalidOperationException($"Eval constraint gap fixture was not accepted: {saved.GapId}");
            }

            var activated = await candidateReviewService.ActivateAsync(
                accepted.CreatedConstraintId,
                new CandidateConstraintReviewRequest
                {
                    OperationId = $"eval-candidate-constraint-activate-{saved.GapId}",
                    Reviewer = "eval-fixture",
                    Reason = "activated eval candidate constraint fixture through formal review chain",
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["source"] = "context-eval-runner",
                        ["fixturePhase"] = "P15",
                        ["sourceConstraintGapId"] = saved.GapId
                    }
                }).ConfigureAwait(false);

            if (activated is null || activated.Status != ContextMemoryStatus.Active)
            {
                throw new InvalidOperationException($"Eval candidate constraint fixture was not activated: {accepted.CreatedConstraintId}");
            }

            activatedConstraints.Add(activated.Constraint);
        }

        return activatedConstraints;
    }

    private static async Task ResetActivatedConstraintGapFixturesAsync(
        ControlRoomState state,
        IReadOnlyList<ContextConstraint> activatedConstraints)
    {
        if (activatedConstraints.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var constraint in activatedConstraints)
        {
            var metadata = new Dictionary<string, string>(constraint.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["evalFixtureResetAt"] = now.ToString("O"),
                ["evalFixtureResetReason"] = "sample_isolation"
            };
            await state.ConstraintStore.SaveAsync(new ContextConstraint
            {
                Id = constraint.Id,
                WorkspaceId = constraint.WorkspaceId,
                CollectionId = constraint.CollectionId,
                Scope = constraint.Scope,
                Level = ConstraintLevel.User,
                Content = constraint.Content,
                AppliesToRefs = constraint.AppliesToRefs.ToArray(),
                SourceRefs = constraint.SourceRefs.ToArray(),
                Status = ContextMemoryStatus.Rejected,
                Confidence = constraint.Confidence,
                Metadata = metadata,
                CreatedAt = constraint.CreatedAt,
                UpdatedAt = now
            }).ConfigureAwait(false);
        }
    }

    private static ConstraintGapCandidate NormalizeActivatedConstraintGapFixture(
        ConstraintGapCandidate fixture,
        string workspaceId,
        string collectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixture.GapId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fixture.ExpectedConstraintText);

        var now = DateTimeOffset.UtcNow;
        return new ConstraintGapCandidate
        {
            GapId = fixture.GapId,
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            SessionId = fixture.SessionId,
            Source = string.IsNullOrWhiteSpace(fixture.Source) ? "eval-constraint-gap-fixture" : fixture.Source,
            SourceSampleId = fixture.SourceSampleId,
            SourceOperationId = string.IsNullOrWhiteSpace(fixture.SourceOperationId)
                ? "eval-constraint-gap-fixture"
                : fixture.SourceOperationId,
            ExpectedConstraintText = fixture.ExpectedConstraintText,
            MatchedConstraintIds = fixture.MatchedConstraintIds.ToArray(),
            SuggestedConstraintTitle = fixture.SuggestedConstraintTitle,
            SuggestedConstraintScope = string.IsNullOrWhiteSpace(fixture.SuggestedConstraintScope)
                ? "Collection"
                : fixture.SuggestedConstraintScope,
            SuggestedConstraintType = string.IsNullOrWhiteSpace(fixture.SuggestedConstraintType)
                ? "Hard"
                : fixture.SuggestedConstraintType,
            Severity = string.IsNullOrWhiteSpace(fixture.Severity)
                ? ConstraintGapSeverity.High
                : fixture.Severity,
            Reason = fixture.Reason,
            EvidenceRefs = fixture.EvidenceRefs.ToArray(),
            Status = ConstraintGapStatus.Pending,
            CreatedAt = fixture.CreatedAt == default ? now : fixture.CreatedAt,
            Metadata = new Dictionary<string, string>(fixture.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["loadedBy"] = "ContextEvalRunner",
                ["fixturePhase"] = "P15"
            }
        };
    }

    private static async Task<ContextEvalResult> EvaluateSampleAsync(
        ControlRoomState state,
        string workspaceId,
        string collectionId,
        ContextEvalSample sample,
        IReadOnlyList<ConstraintGapCandidate> activatedConstraintGaps)
    {
        var tokenBudget = 4000;
        if (sample.Metadata.TryGetValue("tokenBudget", out var budgetStr) && int.TryParse(budgetStr, out var budget))
        {
            tokenBudget = budget;
        }

        // A. 检索测试 (Retrieval Eval)
        var retrievalRequest = new ContextRetrievalRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = sample.Query,
            TopK = 10,
            CandidateTake = 50,
            VectorTopK = 20,
            IncludeKeywordRecall = true,
            IncludeVectorRecall = true,
            IncludeRelationExpansion = true,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            TokenBudget = tokenBudget,
            Metadata = new Dictionary<string, string>
            {
                ["attention.mustHit"] = string.Join(",", sample.MustHit),
                ["attention.mustNotHit"] = string.Join(",", sample.MustNotHit)
            }
        };

        // 向量化 query
        var queryEmbedResult = await state.EmbeddingProvider.EmbedAsync(new EmbeddingRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            InputKind = EmbeddingInputKind.Query,
            Inputs = [new EmbeddingInput { Id = "query", Text = sample.Query }]
        });
        if (queryEmbedResult.Succeeded && queryEmbedResult.Vectors.Count > 0)
        {
            retrievalRequest = new ContextRetrievalRequest
            {
                OperationId = retrievalRequest.OperationId,
                WorkspaceId = retrievalRequest.WorkspaceId,
                CollectionId = retrievalRequest.CollectionId,
                QueryText = retrievalRequest.QueryText,
                QueryVector = queryEmbedResult.Vectors[0].Values,
                TopK = retrievalRequest.TopK,
                CandidateTake = retrievalRequest.CandidateTake,
                VectorTopK = retrievalRequest.VectorTopK,
                IncludeKeywordRecall = retrievalRequest.IncludeKeywordRecall,
                IncludeVectorRecall = retrievalRequest.IncludeVectorRecall,
                IncludeRelationExpansion = retrievalRequest.IncludeRelationExpansion,
                IncludeWorkingMemory = retrievalRequest.IncludeWorkingMemory,
                IncludeStableMemory = retrievalRequest.IncludeStableMemory,
                TokenBudget = retrievalRequest.TokenBudget,
                Metadata = new Dictionary<string, string>(retrievalRequest.Metadata)
            };
        }

        var retrievalResult = await state.Retriever.RetrieveAsync(retrievalRequest).ConfigureAwait(false);
        var attentionProfileMetrics = CalculateAttentionProfileMetrics(
            retrievalResult.Trace.AttentionProfileComparison,
            retrievalResult.Trace.AttentionShadowReport,
            sample);
        var attentionMetrics = attentionProfileMetrics.FirstOrDefault(item =>
                string.Equals(item.ProfileId, "default-shadow-v1", StringComparison.OrdinalIgnoreCase))
            ?? attentionProfileMetrics.FirstOrDefault()
            ?? ToAttentionProfileResult(
                "default-shadow-v1",
                "context-attention-shadow-policy/v1",
                CalculateAttentionMetrics(retrievalResult.Trace.AttentionShadowReport, sample),
                sample,
                retrievalResult.Trace.AttentionShadowReport,
                retrievalResult.Trace.AttentionScores);

        // B. 打包测试 (Package Eval)
        var packageRequest = new ContextPackageRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = sample.Query,
            TokenBudget = tokenBudget,
            Policy = new ContextPackagePolicy
            {
                Id = "eval-policy",
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                IncludeGlobalContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 20
            },
            Metadata = new Dictionary<string, string>
            {
                ["mode"] = sample.Mode,
                ["includeDiagnosticsSections"] = "true",
                ["includeExcludedSection"] = "true",
                ["includeUncertaintySection"] = "true",
                ["eval.mustHit"] = string.Join(",", sample.MustHit),
                ["eval.expectedUncertainties"] = string.Join(";", sample.ExpectedUncertainties)
            }
        };

        var activatedEvalConstraints = await ActivateConstraintGapFixturesForSampleAsync(
            state,
            activatedConstraintGaps,
            workspaceId,
            collectionId,
            sample.Id).ConfigureAwait(false);
        ContextPackageBuildResult packageResult;
        try
        {
            packageResult = await state.PackageBuilder.BuildDetailedAsync(packageRequest).ConfigureAwait(false);
        }
        finally
        {
            await ResetActivatedConstraintGapFixturesAsync(
                state,
                activatedEvalConstraints).ConfigureAwait(false);
        }

        var selectedIds = packageResult.SelectedItems.Select(item => item.ItemId).ToList();

        // 计算 Retrieval/Package Metrics
        var mustHitCount = sample.MustHit.Count;
        var mustNotHitCount = sample.MustNotHit.Count;

        var mustHitRecalled3 = sample.MustHit.Count(id => selectedIds.Take(3).Contains(id));
        var mustHitRecalled5 = sample.MustHit.Count(id => selectedIds.Take(5).Contains(id));
        var mustHitRecalled10 = sample.MustHit.Count(id => selectedIds.Take(10).Contains(id));
        var mustNotHitRecalled = sample.MustNotHit.Count(id => selectedIds.Contains(id));

        var recall3 = mustHitCount == 0 ? 1.0 : (double)mustHitRecalled3 / mustHitCount;
        var recall5 = mustHitCount == 0 ? 1.0 : (double)mustHitRecalled5 / mustHitCount;
        var recall10 = mustHitCount == 0 ? 1.0 : (double)mustHitRecalled10 / mustHitCount;

        // MRR 计算：两种变体
        // MRRAnyMustHit: 所有 mustHit 中排名最高的那个的倒数排名（主指标）
        // PrimaryMustHitMrr: 第一个 mustHit（按样本顺序）的倒数排名（传统 MRR 语义）
        double mrrAnyMustHit = 0.0;
        double primaryMustHitMrr = 0.0;

        // 建站每个 mustHit 在 selectedIds 中的位置
        var mustHitPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < selectedIds.Count; i++)
        {
            if (!mustHitPositions.ContainsKey(selectedIds[i]))
                mustHitPositions[selectedIds[i]] = i;
        }

        // MRRAnyMustHit: 取所有 mustHit 中位置最小（排名最高）的倒数
        int bestPosition = int.MaxValue;
        foreach (var mustHitId in sample.MustHit)
        {
            if (mustHitPositions.TryGetValue(mustHitId, out var pos) && pos < bestPosition)
                bestPosition = pos;
        }
        if (bestPosition < int.MaxValue)
            mrrAnyMustHit = 1.0 / (bestPosition + 1);

        // PrimaryMustHitMrr: 样本中第一个 mustHit 的倒数排名
        if (sample.MustHit.Count > 0 && mustHitPositions.TryGetValue(sample.MustHit[0], out var primaryPos))
            primaryMustHitMrr = 1.0 / (primaryPos + 1);

        var noiseRatio = selectedIds.Count == 0 ? 0.0 : (double)mustNotHitRecalled / selectedIds.Count;

        var fullPackageText = string.Join("\n", packageResult.Package.Sections.Select(s => s.Content));
        var constraintsText = string.Join("\n", packageResult.Output.Constraints.Select(c => c.Content));

        // 约束、实体、不确定性校验
        var hasAllConstraints = sample.ExpectedConstraints.All(expected =>
            IsExpectedTextSatisfied(expected, constraintsText, isEntity: false) ||
            IsExpectedTextSatisfied(expected, fullPackageText, isEntity: false));

        var hasAllEntities = sample.ExpectedEntities.All(expected =>
            IsExpectedTextSatisfied(expected, fullPackageText, isEntity: true));

        var constraintClosureDiagnostics = !hasAllConstraints && sample.ExpectedConstraints.Count > 0
            ? await BuildConstraintClosureDiagnosticsAsync(
                state,
                workspaceId,
                collectionId,
                sample.ExpectedConstraints,
                packageResult,
                constraintsText).ConfigureAwait(false)
            : Array.Empty<string>();

        // UncertaintyMatchResolver：支持 code 别名、diagnostics、conflict/deprecated evidence、
        // historical context、excluded reason 和 risk metadata 映射。
        // 各期望 code 的解析结果用于 trace 可观测性输出（不影响 warning 触发逻辑）
        var uncertaintyMatchResults = sample.ExpectedUncertainties
            .Select(expected =>
                UncertaintyMatchResolver.Resolve(
                    expected,
                    packageResult.Output.Uncertainties,
                    packageResult.SelectedItems,
                    packageResult.DroppedItems,
                    packageResult.Package.Sections,
                    packageResult.Output))
            .ToList();
        var hasAllUncertainties = uncertaintyMatchResults.All(r => r.Satisfied);

        // 新度量计算
        double unusedBudgetRatio = packageResult.Budget.TokenBudget > 0
            ? (double)packageResult.Budget.RemainingTokens / packageResult.Budget.TokenBudget
            : 0.0;

        double mustHitTokenShare = 0.0;
        if (packageResult.Budget.UsedTokens > 0)
        {
            var mustHitTokens = packageResult.SelectedItems
                .Where(item => sample.MustHit.Contains(item.ItemId))
                .Sum(item => item.EstimatedTokens);
            mustHitTokenShare = (double)mustHitTokens / packageResult.Budget.UsedTokens;
        }

        var selectedItemDiagnostics = packageResult.SelectedItems
            .Select((item, index) => ToEvalItemDiagnostic(item, sample, index + 1))
            .ToArray();
        var droppedItemDiagnostics = packageResult.DroppedItems
            .Select(item => ToEvalItemDiagnostic(item, sample))
            .ToArray();
        var budgetPressureBreakdown = BuildBudgetPressureBreakdown(
            packageResult,
            selectedItemDiagnostics,
            droppedItemDiagnostics);

        // 综合判定四种单条评测状态与具体的警告原因
        string status = "Passed";
        bool succeeded = true;
        var warningList = new List<string>();

        if (string.IsNullOrWhiteSpace(sample.Id) || string.IsNullOrWhiteSpace(sample.Query))
        {
            status = "InvalidSample";
            succeeded = false;
        }
        else if (recall10 < 0.99 || mustNotHitRecalled > 0 || !hasAllConstraints || !hasAllEntities)
        {
            status = "Failed";
            succeeded = false;
        }
        else
        {
            // 判定 PassedWithWarnings
            var hasDuplicate = packageResult.SelectedItems.Any(item => item.Reason == "referenced by duplicate section");
            var hasBudgetPressure = packageResult.DroppedItems.Any(item => item.Reason.Contains("token budget"));

            var isAuditMode = !string.IsNullOrWhiteSpace(sample.Query) && (
                sample.Query.Contains("废弃", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("作废", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("草稿", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("草案", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("旧版", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("旧", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("放弃", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("舍弃", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("审计", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("legacy", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("audit", StringComparison.OrdinalIgnoreCase)
            );

            var isConflictQuery = !string.IsNullOrWhiteSpace(sample.Query) && (
                sample.Query.Contains("冲突", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("矛盾", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("版本冲突", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                || sample.Query.Contains("contradiction", StringComparison.OrdinalIgnoreCase)
            );

            // lifecycle item 出现在普通 Section（非 lifecycle-allowed Section）中 = 风险
            // 说明：SupersededSelectedItem uncertainty 在 builder 修复后仅含普通 Section 中的项
            var hasDeprecatedInNormalSection =
                packageResult.Output.Uncertainties.Any(u => u.Code == "SupersededSelectedItem")
                || packageResult.SelectedItems.Any(item =>
                    item.Metadata.ContainsKey("lifecycleStatus")
                    && item.Metadata["lifecycleStatus"] == "Deprecated"
                    && SectionLifecyclePolicy.IsNormalSection(item.SectionName));

            // lifecycle item 正确进入 lifecycle-allowed Section（historical_context / conflict_evidence 等）
            var hasLifecycleItemsInAllowedSections = packageResult.SelectedItems.Any(item =>
                SectionLifecyclePolicy.IsLifecycleAllowedSection(item.SectionName));

            var hasExcludedDeprecated = packageResult.DroppedItems.Any(item =>
                item.Reason.Contains("deprecated", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("rejected", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("废弃", StringComparison.OrdinalIgnoreCase)
                || item.Reason.Contains("拒绝", StringComparison.OrdinalIgnoreCase));

            var isStableOrDoc = sample.MustHit.Any(id => id.StartsWith("stable:") || id.StartsWith("doc:") || id.StartsWith("ctx:") || id.StartsWith("novel:") || id.StartsWith("coding:"));
            var mrrThreshold = isStableOrDoc ? 0.09 : 0.40;

            // 使用 MRRAnyMustHit 作为主评测指标；使用用户指定的精确 key 名
            if (mrrAnyMustHit < mrrThreshold) warningList.Add("MRRLow");
            if (recall3 < 0.50) warningList.Add("Recall3Low");
            if (!hasAllUncertainties) warningList.Add("MissingUncertainty");
            if (hasDuplicate) warningList.Add("DuplicateIntercepted");
            if (mustHitTokenShare < 0.03) warningList.Add("LowMustHitTokenShare");
            if (hasBudgetPressure) warningList.Add("TokenBudgetPressure");

            if (hasDeprecatedInNormalSection)
            {
                // lifecycle item 进入了普通 Section（非 audit/conflict 路径），属于风险
                warningList.Add("LifecycleRiskSelectedInNormalContext");
            }

            if (hasLifecycleItemsInAllowedSections && isAuditMode)
            {
                // audit query：lifecycle item 正确进入 historical_context / deprecated_evidence，仅记录 Info
                warningList.Add("LifecycleItemIncludedForAudit");
            }

            if (hasExcludedDeprecated)
            {
                warningList.Add("LifecycleItemExcluded");
            }

            // 只有除 LifecycleItemIncludedForAudit 和 LifecycleItemExcluded 之外的警告才触发 PassedWithWarnings
            var activeWarnings = warningList.Where(w => w != "LifecycleItemIncludedForAudit" && w != "LifecycleItemExcluded").ToList();
            if (activeWarnings.Count > 0)
            {
                status = "PassedWithWarnings";
            }
        }

        int anchorsCount = 0;
        if (packageResult.Metadata.TryGetValue("anchor.count", out var acStr) && int.TryParse(acStr, out var ac))
        {
            anchorsCount = ac;
        }

        packageResult.Metadata.TryGetValue("anchor.names", out var anchorNamesVal);
        string anchorNames = anchorNamesVal ?? string.Empty;
        if (string.IsNullOrEmpty(anchorNames)) anchorNames = "None";

        // 拆分 Raw / Semantic Anchors
        packageResult.Metadata.TryGetValue("anchor.rawSearchTokens", out var rawTokensVal);
        packageResult.Metadata.TryGetValue("anchor.semanticAnchors", out var semAnchorsVal);
        string rawSearchTokens = rawTokensVal ?? string.Empty;
        string semanticAnchors = semAnchorsVal ?? string.Empty;
        int rawSearchTokensCount = 0;
        int semanticAnchorsCount = 0;
        if (packageResult.Metadata.TryGetValue("anchor.rawSearchTokensCount", out var rscStr) && int.TryParse(rscStr, out var rsc))
        {
            rawSearchTokensCount = rsc;
        }
        if (packageResult.Metadata.TryGetValue("anchor.semanticAnchorsCount", out var sacStr) && int.TryParse(sacStr, out var sac))
        {
            semanticAnchorsCount = sac;
        }

        var traceSb = new StringBuilder();
        traceSb.AppendLine($"Build ID: {packageResult.BuildId}");
        traceSb.AppendLine($"MRRAnyMustHit: {mrrAnyMustHit:F4} | PrimaryMustHitMRR: {primaryMustHitMrr:F4}");
        traceSb.AppendLine($"Attention Shadow: MRR={attentionMetrics.AttentionMrr:F4} | Recall@3={attentionMetrics.AttentionRecall3:P2} | Recall@5={attentionMetrics.AttentionRecall5:P2} | ChangeRatio={attentionMetrics.SelectedSetChangeRatio:P2}");
        if (attentionProfileMetrics.Count > 0)
        {
            traceSb.AppendLine("Attention Profiles: " + string.Join("; ", attentionProfileMetrics.Select(item => $"{item.ProfileId}=MRR:{item.AttentionMrr:F4}/R5:{item.AttentionRecall5:P2}/Change:{item.SelectedSetChangeRatio:P2}")));
        }
        traceSb.AppendLine($"Token Budget: {packageResult.Budget.TokenBudget} | Used: {packageResult.Budget.UsedTokens} | Remaining: {packageResult.Budget.RemainingTokens} | Unused Ratio: {unusedBudgetRatio:P2}");
        traceSb.AppendLine($"MustHit Token Share: {mustHitTokenShare:P2} | Waste Ratio: {packageResult.Budget.WasteRatio:P2}");
        traceSb.AppendLine("BudgetPressureBreakdown: " +
            $"mandatoryTokens={budgetPressureBreakdown.MandatoryTokens} | " +
            $"constraintsTokens={budgetPressureBreakdown.ConstraintsTokens} | " +
            $"workingTokens={budgetPressureBreakdown.WorkingTokens} | " +
            $"stableTokens={budgetPressureBreakdown.StableTokens} | " +
            $"evidenceTokens={budgetPressureBreakdown.EvidenceTokens} | " +
            $"diagnosticsTokens={budgetPressureBreakdown.DiagnosticsTokens} | " +
            $"historicalTokens={budgetPressureBreakdown.HistoricalTokens} | " +
            $"droppedMustHitTokens={budgetPressureBreakdown.DroppedMustHitTokens} | " +
            $"droppedLowPriorityTokens={budgetPressureBreakdown.DroppedLowPriorityTokens}");
        if (constraintClosureDiagnostics.Count > 0)
        {
            traceSb.AppendLine("ConstraintClosureDiagnostics:");
            foreach (var diagnostic in constraintClosureDiagnostics)
            {
                traceSb.AppendLine($"  - {diagnostic}");
            }
        }

        traceSb.AppendLine($"Raw Search Tokens ({rawSearchTokensCount}): {rawSearchTokens}");
        traceSb.AppendLine($"Semantic Anchors ({semanticAnchorsCount}): {semanticAnchors}");

        traceSb.AppendLine("Selected Items:");
        foreach (var item in packageResult.SelectedItems)
        {
            var duplicateSuffix = item.Metadata.TryGetValue("alsoReferencedBy", out var alsoRef)
                ? $" (also: {alsoRef})"
                : string.Empty;
            var isMustHit = ContainsEvalId(sample.MustHit, item.ItemId) ? " [MustHit]✓" : string.Empty;
            traceSb.AppendLine($"  - [{item.SectionName}] {item.ItemId} ({item.Kind}/{item.Type}) | Score: {item.Score:F2} | Tokens: {item.EstimatedTokens} | SourceRefs: {string.Join(", ", item.SourceRefs)}{isMustHit}{duplicateSuffix}");
            if (item.ScoreBreakdown is not null)
            {
                traceSb.AppendLine($"    ↳ {item.ScoreBreakdown.ToTraceString()}");
            }
        }

        traceSb.AppendLine("Dropped/Excluded Items:");
        foreach (var item in packageResult.DroppedItems)
        {
            var isMustHit = ContainsEvalId(sample.MustHit, item.ItemId) ? " [MustHit]✓" : string.Empty;
            traceSb.AppendLine($"  - {item.ItemId} ({item.Kind}/{item.Type}) | Score: {item.Score:F2} | Tokens: {item.EstimatedTokens} | Reason: {item.Reason}{isMustHit}");
        }

        traceSb.AppendLine("Uncertainties:");
        foreach (var unc in packageResult.Uncertainties)
        {
            traceSb.AppendLine($"  - [{unc.Code}] {unc.Severity}: {unc.Message} (Refs: {string.Join(", ", unc.ItemRefs)})");
        }

        if (uncertaintyMatchResults.Count > 0)
        {
            traceSb.AppendLine("ExpectedUncertainties (UncertaintyMatchResolver):");
            foreach (var result in uncertaintyMatchResults)
            {
                var mark = result.Satisfied ? "✓" : "✗";
                var source = string.IsNullOrWhiteSpace(result.Source)
                    ? "source=none"
                    : $"source={result.Source}";
                var failureType = string.IsNullOrWhiteSpace(result.FailureType)
                    ? string.Empty
                    : $", failureType={result.FailureType}";
                traceSb.AppendLine($"  - [{mark}] {result.ExpectedCode} ({source}{failureType})");
            }
        }

        var trace = traceSb.ToString();

        return new ContextEvalResult
        {
            SampleId = sample.Id,
            Query = sample.Query,
            Mode = sample.Mode,
            Succeeded = succeeded,
            Status = status,
            ErrorMessage = succeeded ? string.Empty : BuildEvalErrorMessage(selectedIds, packageResult, constraintClosureDiagnostics),
            RetrievalRecall3 = recall3,
            RetrievalRecall5 = recall5,
            RetrievalRecall10 = recall10,
            RetrievalMrrAnyMustHit = mrrAnyMustHit,
            PrimaryMustHitMrr = primaryMustHitMrr,
            RetrievalNoiseViolationRatio = noiseRatio,
            MustHitCount = mustHitCount,
            MustHitRecalledCount = mustHitRecalled10,
            MustNotHitCount = mustNotHitCount,
            MustNotHitRecalledCount = mustNotHitRecalled,
            AttentionMrr = attentionMetrics.AttentionMrr,
            AttentionRecall3 = attentionMetrics.AttentionRecall3,
            AttentionRecall5 = attentionMetrics.AttentionRecall5,
            AttentionImproved = attentionMetrics.Improved,
            AttentionRegressed = attentionMetrics.Regressed,
            AttentionWouldChangeSelectedSet = attentionMetrics.WouldChangeSelectedSet,
            MustNotHitPromotedCount = attentionMetrics.MustNotHitPromotedCount,
            AttentionSelectedSetChangeRatio = attentionMetrics.SelectedSetChangeRatio,
            AttentionProfiles = attentionProfileMetrics,
            AttentionRerankComparison = retrievalResult.Trace.AttentionRerankComparison,
            PackageTokenWasteRatio = packageResult.Budget.WasteRatio,
            UnusedBudgetRatio = unusedBudgetRatio,
            MustHitTokenShare = mustHitTokenShare,
            PackageHasAllConstraints = hasAllConstraints,
            PackageHasAllEntities = hasAllEntities,
            PackageHasAllUncertainties = hasAllUncertainties,
            AnchorsCount = anchorsCount,
            RawSearchTokensCount = rawSearchTokensCount,
            SemanticAnchorsCount = semanticAnchorsCount,
            RawSearchTokens = rawSearchTokens,
            SemanticAnchors = semanticAnchors,
            CandidatesCount = packageResult.SelectedItems.Count + packageResult.DroppedItems.Count,
            SelectedCount = packageResult.SelectedItems.Count,
            ExcludedCount = packageResult.DroppedItems.Count,
            TokenBudget = packageResult.Budget.TokenBudget,
            SelectedIds = selectedIds,
            ExcludedIds = packageResult.DroppedItems.Select(item => item.ItemId).ToArray(),
            PackageMetadata = new Dictionary<string, string>(packageResult.Package.Metadata, StringComparer.OrdinalIgnoreCase),
            PackageSectionNames = packageResult.Package.Sections.Select(section => section.Name).ToArray(),
            PackageSectionItemRefs = packageResult.Package.Sections
                .GroupBy(section => section.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<string>)group
                        .SelectMany(section => section.ItemRefs)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    StringComparer.OrdinalIgnoreCase),
            PackageBuildTrace = trace,
            MustHit = sample.MustHit,
            MustNotHit = sample.MustNotHit,
            ExpectedConstraints = sample.ExpectedConstraints,
            ExpectedEntities = sample.ExpectedEntities,
            ExpectedUncertainties = sample.ExpectedUncertainties,
            GoldenNotes = sample.GoldenNotes,
            WarningReasons = warningList,
            BudgetPressureBreakdown = budgetPressureBreakdown,
            SelectedItemDiagnostics = selectedItemDiagnostics,
            DroppedItemDiagnostics = droppedItemDiagnostics
        };
    }

    private static ContextEvalItemDiagnostic ToEvalItemDiagnostic(
        ContextPackageDecision item,
        ContextEvalSample sample,
        int rank)
    {
        return new ContextEvalItemDiagnostic
        {
            ItemId = item.ItemId,
            Kind = item.Kind,
            Type = item.Type,
            SectionName = item.SectionName,
            Reason = item.Reason,
            Score = item.Score,
            EstimatedTokens = item.EstimatedTokens,
            Rank = rank,
            IsMustHit = ContainsEvalId(sample.MustHit, item.ItemId),
            IsMustNotHit = ContainsEvalId(sample.MustNotHit, item.ItemId),
            SourceRefs = item.SourceRefs
        };
    }

    private static ContextEvalItemDiagnostic ToEvalItemDiagnostic(
        DroppedContextItem item,
        ContextEvalSample sample)
    {
        return new ContextEvalItemDiagnostic
        {
            ItemId = item.ItemId,
            Kind = item.Kind,
            Type = item.Type,
            SectionName = string.Empty,
            Reason = item.Reason,
            Score = item.Score,
            EstimatedTokens = item.EstimatedTokens,
            Rank = 0,
            IsMustHit = ContainsEvalId(sample.MustHit, item.ItemId),
            IsMustNotHit = ContainsEvalId(sample.MustNotHit, item.ItemId),
            SourceRefs = item.SourceRefs
        };
    }

    private static ContextEvalBudgetPressureBreakdown BuildBudgetPressureBreakdown(
        ContextPackageBuildResult packageResult,
        IReadOnlyList<ContextEvalItemDiagnostic> selectedItems,
        IReadOnlyList<ContextEvalItemDiagnostic> droppedItems)
    {
        var evidenceSectionTokens = SumSectionTokens(
            packageResult.Budget,
            "evidence",
            "related_context",
            "conflict_evidence",
            "deprecated_evidence");
        var diagnosticsSectionTokens = SumSectionTokens(
            packageResult.Budget,
            "diagnostics",
            "uncertainties",
            "excluded",
            "risk_flags");

        return new ContextEvalBudgetPressureBreakdown
        {
            MandatoryTokens = selectedItems
                .Where(item => IsMandatorySection(item.SectionName) || ContainsText(item.Reason, "mandatory"))
                .Sum(item => item.EstimatedTokens),
            ConstraintsTokens = selectedItems
                .Where(IsConstraintItem)
                .Sum(item => item.EstimatedTokens),
            WorkingTokens = selectedItems
                .Where(item => IsSection(item.SectionName, "working_memory", "current_task") ||
                               ContainsText(item.Kind, "working_memory"))
                .Sum(item => item.EstimatedTokens),
            StableTokens = selectedItems
                .Where(item => IsSection(item.SectionName, "stable_memory", "global_context") ||
                               ContainsText(item.Kind, "stable_memory"))
                .Sum(item => item.EstimatedTokens),
            EvidenceTokens = Math.Max(
                evidenceSectionTokens,
                selectedItems
                    .Where(item => IsSection(item.SectionName, "evidence", "related_context", "conflict_evidence", "deprecated_evidence"))
                    .Sum(item => item.EstimatedTokens)),
            DiagnosticsTokens = diagnosticsSectionTokens,
            HistoricalTokens = selectedItems
                .Where(item => IsSection(item.SectionName, "historical_context", "deprecated_evidence", "conflict_evidence") ||
                               ContainsText(item.Kind, "historical"))
                .Sum(item => item.EstimatedTokens),
            DroppedMustHitTokens = droppedItems
                .Where(item => item.IsMustHit)
                .Sum(item => item.EstimatedTokens),
            DroppedLowPriorityTokens = droppedItems
                .Where(item => !item.IsMustHit &&
                               ContainsText(item.Reason, "token budget") &&
                               item.Score < 25)
                .Sum(item => item.EstimatedTokens)
        };
    }

    private static async Task<IReadOnlyList<string>> BuildConstraintClosureDiagnosticsAsync(
        ControlRoomState state,
        string workspaceId,
        string collectionId,
        IReadOnlyList<string> expectedConstraints,
        ContextPackageBuildResult packageResult,
        string constraintsText)
    {
        var activeHardConstraints = await state.ConstraintStore.QueryAsync(
            new ContextConstraintQuery
            {
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Level = ConstraintLevel.Hard,
                Status = ContextMemoryStatus.Active,
                Take = int.MaxValue
            }).ConfigureAwait(false);

        var diagnostics = new List<string>();
        foreach (var expected in expectedConstraints.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var matchingConstraints = activeHardConstraints
                .Where(item => IsExpectedTextSatisfied(expected, item.Content, isEntity: false))
                .ToArray();
            var matchingIds = matchingConstraints
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var constraintExists = matchingIds.Count > 0;
            var constraintSelected = packageResult.SelectedItems.Any(item =>
                matchingIds.Contains(item.ItemId) &&
                IsSection(item.SectionName, "hard_constraints", "constraints", "soft_constraints"));
            var constraintRenderedInConstraintsSection = packageResult.Output.Constraints.Any(section =>
                IsExpectedTextSatisfied(expected, section.Content, isEntity: false));
            var constraintDroppedByBudget = packageResult.DroppedItems.Any(item =>
                matchingIds.Contains(item.ItemId) &&
                item.Reason.Contains("token budget", StringComparison.OrdinalIgnoreCase));
            var constraintTextMatchedExpected = IsExpectedTextSatisfied(expected, constraintsText, isEntity: false);

            diagnostics.Add(
                $"expected=\"{expected}\" | constraintExists={constraintExists} | " +
                $"constraintIds=[{string.Join(", ", matchingIds)}] | " +
                $"constraintSelected={constraintSelected} | " +
                $"constraintRenderedInConstraintsSection={constraintRenderedInConstraintsSection} | " +
                $"constraintDroppedByBudget={constraintDroppedByBudget} | " +
                $"constraintTextMatchedExpected={constraintTextMatchedExpected}");
        }

        return diagnostics;
    }

    private static string BuildEvalErrorMessage(
        IReadOnlyList<string> selectedIds,
        ContextPackageBuildResult packageResult,
        IReadOnlyList<string> constraintClosureDiagnostics)
    {
        var builder = new StringBuilder();
        builder.Append($"Selected: [{string.Join(", ", selectedIds)}], Dropped: [{string.Join(", ", packageResult.DroppedItems.Select(d => $"{d.ItemId}({d.Reason})"))}]");
        if (constraintClosureDiagnostics.Count > 0)
        {
            builder.Append(", ConstraintClosureDiagnostics: [");
            builder.Append(string.Join(" ; ", constraintClosureDiagnostics));
            builder.Append(']');
        }

        return builder.ToString();
    }

    private static int SumSectionTokens(ContextPackageBudgetReport budget, params string[] sectionNames)
    {
        return budget.Sections
            .Where(section => IsSection(section.SectionName, sectionNames))
            .Sum(section => section.UsedTokens);
    }

    private static bool IsConstraintItem(ContextEvalItemDiagnostic item) =>
        IsSection(item.SectionName, "hard_constraints", "constraints", "soft_constraints") ||
        ContainsText(item.Kind, "constraint") ||
        ContainsText(item.Type, "constraint");

    private static bool IsMandatorySection(string sectionName) =>
        IsSection(sectionName, "current_task", "hard_constraints");

    private static bool IsSection(string actual, params string[] expected)
    {
        var normalized = NormalizeSectionName(actual);
        return expected.Any(item =>
            string.Equals(normalized, NormalizeSectionName(item), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeSectionName(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim()
                .Replace("-", "_", StringComparison.Ordinal)
                .ToLowerInvariant();

    private static bool ContainsEvalId(IEnumerable<string> ids, string itemId) =>
        ids.Any(id => string.Equals(id, itemId, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsText(string value, string expected) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(expected, StringComparison.OrdinalIgnoreCase);

    private static AttentionEvalMetrics CalculateAttentionMetrics(
        AttentionShadowReport report,
        ContextEvalSample sample)
    {
        if (report.Ranks.Count == 0)
        {
            return new AttentionEvalMetrics(
                Mrr: 0d,
                Recall3: sample.MustHit.Count == 0 ? 1d : 0d,
                Recall5: sample.MustHit.Count == 0 ? 1d : 0d,
                CurrentMrr: 0d,
                Improved: false,
                Regressed: false,
                WouldChangeSelectedSet: false,
                MustHitDemotedCount: 0,
                MustNotHitPromotedCount: 0,
                MustNotHitWouldBeSelectedCount: 0,
                SelectedSetChangeRatio: 0d);
        }

        var mustHitCurrentRanks = ResolveBestRanks(sample.MustHit, report.Ranks, useAttentionRank: false);
        var mustHitAttentionRanks = ResolveBestRanks(sample.MustHit, report.Ranks, useAttentionRank: true);
        var currentMrr = ResolveMrr(mustHitCurrentRanks);
        var attentionMrr = ResolveMrr(mustHitAttentionRanks);
        var recall3 = ResolveRecall(sample.MustHit.Count, mustHitAttentionRanks, topK: 3);
        var recall5 = ResolveRecall(sample.MustHit.Count, mustHitAttentionRanks, topK: 5);

        return new AttentionEvalMetrics(
            Mrr: attentionMrr,
            Recall3: recall3,
            Recall5: recall5,
            CurrentMrr: currentMrr,
            Improved: attentionMrr > currentMrr + 0.0001,
            Regressed: attentionMrr + 0.0001 < currentMrr,
            WouldChangeSelectedSet: report.WouldChangeSelectedSet,
            MustHitDemotedCount: report.Ranks.Count(rank =>
                rank.IsMustHit
                && (rank.RankDelta < 0 || (rank.SelectedByCurrentPolicy && !rank.WouldBeSelectedByAttention))),
            MustNotHitPromotedCount: report.MustNotHitPromotedCount,
            MustNotHitWouldBeSelectedCount: report.Ranks.Count(rank => rank.IsMustNotHit && rank.WouldBeSelectedByAttention),
            SelectedSetChangeRatio: report.SelectedSetChangeRatio);
    }

    private static IReadOnlyList<ContextEvalAttentionProfileResult> CalculateAttentionProfileMetrics(
        AttentionProfileExperimentReport comparison,
        AttentionShadowReport fallbackReport,
        ContextEvalSample sample)
    {
        if (comparison.Profiles.Count == 0)
        {
            return
            [
                ToAttentionProfileResult(
                    "default-shadow-v1",
                    "context-attention-shadow-policy/v1",
                    CalculateAttentionMetrics(fallbackReport, sample),
                    sample,
                    fallbackReport,
                    Array.Empty<ContextAttentionScore>())
            ];
        }

        return comparison.Profiles
            .Select(profile => ToAttentionProfileResult(
                profile.ProfileId,
                profile.PolicyVersion,
                CalculateAttentionMetrics(profile.ShadowReport, sample),
                sample,
                profile.ShadowReport,
                profile.AttentionScores))
            .ToArray();
    }

    private static ContextEvalAttentionProfileResult ToAttentionProfileResult(
        string profileId,
        string policyVersion,
        AttentionEvalMetrics metrics,
        ContextEvalSample sample,
        AttentionShadowReport report,
        IReadOnlyList<ContextAttentionScore> scores)
    {
        return new ContextEvalAttentionProfileResult
        {
            ProfileId = profileId,
            PolicyVersion = policyVersion,
            CurrentMrr = metrics.CurrentMrr,
            AttentionMrr = metrics.Mrr,
            AttentionRecall3 = metrics.Recall3,
            AttentionRecall5 = metrics.Recall5,
            Improved = metrics.Improved,
            Regressed = metrics.Regressed,
            WouldChangeSelectedSet = metrics.WouldChangeSelectedSet,
            MustHitDemotedCount = metrics.MustHitDemotedCount,
            MustNotHitPromotedCount = metrics.MustNotHitPromotedCount,
            MustNotHitWouldBeSelectedCount = metrics.MustNotHitWouldBeSelectedCount,
            SelectedSetChangeRatio = metrics.SelectedSetChangeRatio,
            CandidateDiagnostics = ShouldWriteCandidateDiagnostics(sample, metrics)
                ? BuildCandidateDiagnostics(report, scores)
                : Array.Empty<ContextEvalAttentionCandidateDiagnostic>()
        };
    }

    private static bool ShouldWriteCandidateDiagnostics(
        ContextEvalSample sample,
        AttentionEvalMetrics metrics)
    {
        if (metrics.Regressed
            || metrics.MustHitDemotedCount > 0
            || metrics.MustNotHitPromotedCount > 0
            || metrics.MustNotHitWouldBeSelectedCount > 0)
        {
            return true;
        }

        return string.Equals(sample.Id, "project-sample-009", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sample.Id, "coding-sample-009", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sample.Id, "novel-sample-002", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<ContextEvalAttentionCandidateDiagnostic> BuildCandidateDiagnostics(
        AttentionShadowReport report,
        IReadOnlyList<ContextAttentionScore> scores)
    {
        var scoresById = scores
            .GroupBy(score => score.CandidateId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        return report.Ranks
            .Where(rank =>
                rank.IsMustHit
                || rank.IsMustNotHit
                || rank.SelectedByCurrentPolicy != rank.WouldBeSelectedByAttention
                || rank.RankDelta != 0
                || rank.CurrentRank <= 5
                || rank.AttentionRank <= 5)
            .OrderByDescending(rank => rank.IsMustHit || rank.IsMustNotHit)
            .ThenByDescending(rank => Math.Abs(rank.RankDelta))
            .ThenBy(rank => rank.AttentionRank)
            .Take(25)
            .Select(rank =>
            {
                scoresById.TryGetValue(rank.CandidateId, out var score);
                return new ContextEvalAttentionCandidateDiagnostic
                {
                    CandidateId = rank.CandidateId,
                    SourceId = rank.SourceId,
                    CurrentRank = rank.CurrentRank,
                    AttentionRank = rank.AttentionRank,
                    RankDelta = rank.RankDelta,
                    CurrentScore = rank.CurrentScore,
                    AttentionScore = rank.AttentionScore,
                    SelectedByCurrentPolicy = rank.SelectedByCurrentPolicy,
                    WouldBeSelectedByAttention = rank.WouldBeSelectedByAttention,
                    IsMustHit = rank.IsMustHit,
                    IsMustNotHit = rank.IsMustNotHit,
                    Lifecycle = rank.Lifecycle,
                    ChannelSources = rank.ChannelSources,
                    RelationPaths = rank.RelationPaths,
                    ScoreBreakdown = rank.ScoreBreakdown,
                    AttentionScoreBreakdown = score is null
                        ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                        : BuildAttentionScoreBreakdown(score),
                    Reasons = rank.Reasons
                };
            })
            .ToArray();
    }

    private static Dictionary<string, double> BuildAttentionScoreBreakdown(ContextAttentionScore score)
    {
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["queryMatch"] = score.QueryMatchScore,
            ["shortTermMatch"] = score.ShortTermMatchScore,
            ["relation"] = score.RelationScore,
            ["recency"] = score.RecencyScore,
            ["importance"] = score.ImportanceScore,
            ["channel"] = score.ChannelScore,
            ["learningFeedback"] = score.LearningFeedbackScore,
            ["lifecyclePenalty"] = score.LifecyclePenalty,
            ["scopePenalty"] = score.ScopePenalty,
            ["noiseRisk"] = score.NoiseRiskScore,
            ["final"] = score.FinalAttentionScore
        };
    }

    private static IReadOnlyList<int> ResolveBestRanks(
        IReadOnlyList<string> expectedIds,
        IReadOnlyList<AttentionShadowRank> ranks,
        bool useAttentionRank)
    {
        var result = new List<int>();
        foreach (var expectedId in expectedIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            var best = ranks
                .Where(rank => MatchesEvalId(rank, expectedId))
                .Select(rank => useAttentionRank ? rank.AttentionRank : rank.CurrentRank)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
            result.Add(best);
        }

        return result;
    }

    private static bool MatchesEvalId(AttentionShadowRank rank, string expectedId)
    {
        return string.Equals(rank.SourceId, expectedId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(rank.CandidateId, expectedId, StringComparison.OrdinalIgnoreCase);
    }

    private static double ResolveMrr(IReadOnlyList<int> ranks)
    {
        var best = ranks.Count == 0 ? int.MaxValue : ranks.Min();
        return best == int.MaxValue ? 0d : 1d / best;
    }

    private static double ResolveRecall(int mustHitCount, IReadOnlyList<int> ranks, int topK)
    {
        return mustHitCount == 0
            ? 1d
            : (double)ranks.Count(rank => rank <= topK) / mustHitCount;
    }

    private static ContextEvalReport BuildReport(IReadOnlyList<ContextEvalResult> results)
    {
        var total = results.Count;
        if (total == 0)
        {
            return new ContextEvalReport();
        }

        var passed = results.Count(r => r.Status == "Passed");
        var passedWithWarnings = results.Count(r => r.Status == "PassedWithWarnings");
        var failed = results.Count(r => r.Status == "Failed");
        var invalid = results.Count(r => r.Status == "InvalidSample");

        var successCount = results.Count(r => r.Succeeded);

        var warningSources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in results)
        {
            foreach (var reason in r.WarningReasons)
            {
                warningSources[reason] = warningSources.TryGetValue(reason, out var count) ? count + 1 : 1;
            }
        }

        return new ContextEvalReport
        {
            TotalSamples = total,
            PassedSamples = passed,
            PassedWithWarningsSamples = passedWithWarnings,
            FailedSamples = failed,
            InvalidSamples = invalid,
            PassRate = (double)successCount / total,
            AvgRetrievalRecall3 = results.Average(r => r.RetrievalRecall3),
            AvgRetrievalRecall5 = results.Average(r => r.RetrievalRecall5),
            AvgRetrievalRecall10 = results.Average(r => r.RetrievalRecall10),
            AvgRetrievalMrrAnyMustHit = results.Average(r => r.RetrievalMrrAnyMustHit),
            AvgPrimaryMustHitMrr = results.Average(r => r.PrimaryMustHitMrr),
            AvgRetrievalNoiseViolationRatio = results.Average(r => r.RetrievalNoiseViolationRatio),
            AvgAttentionMrr = results.Average(r => r.AttentionMrr),
            AvgAttentionRecall3 = results.Average(r => r.AttentionRecall3),
            AvgAttentionRecall5 = results.Average(r => r.AttentionRecall5),
            AttentionImprovedSamples = results.Count(r => r.AttentionImproved),
            AttentionRegressedSamples = results.Count(r => r.AttentionRegressed),
            MustNotHitPromotedCount = results.Sum(r => r.MustNotHitPromotedCount),
            SelectedSetChangeRatio = results.Average(r => r.AttentionSelectedSetChangeRatio),

            AvgPackageWasteRatio = results.Average(r => r.PackageTokenWasteRatio),
            AvgUnusedBudgetRatio = results.Average(r => r.UnusedBudgetRatio),
            AvgMustHitTokenShare = results.Average(r => r.MustHitTokenShare),
            PackageConstraintHitRate = (double)results.Count(r => r.PackageHasAllConstraints) / total,
            PackageEntityHitRate = (double)results.Count(r => r.PackageHasAllEntities) / total,
            PackageUncertaintyHitRate = (double)results.Count(r => r.PackageHasAllUncertainties) / total,

            AvgAnchorsCount = results.Average(r => r.AnchorsCount),
            AvgRawSearchTokensCount = results.Average(r => r.RawSearchTokensCount),
            AvgSemanticAnchorsCount = results.Average(r => r.SemanticAnchorsCount),
            AvgCandidatesCount = results.Average(r => r.CandidatesCount),
            AvgSelectedCount = results.Average(r => r.SelectedCount),
            AvgExcludedCount = results.Average(r => r.ExcludedCount),
            WarningSources = warningSources,
            ModeSummaries = results
                .GroupBy(r => r.Mode, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(BuildModeSummary)
                .ToArray(),
            AttentionProfileSummaries = BuildAttentionProfileSummaries(results),
            AttentionDiagnostics = BuildAttentionDiagnostics(results),
            Results = results
        };
    }

    private static IReadOnlyList<ContextEvalAttentionProfileSummary> BuildAttentionProfileSummaries(
        IReadOnlyList<ContextEvalResult> results)
    {
        var rows = results
            .SelectMany(result => result.AttentionProfiles.Select(profile => new { Result = result, Profile = profile }))
            .ToArray();

        return rows
            .GroupBy(row => (row.Profile.ProfileId, row.Profile.PolicyVersion))
            .OrderBy(group => group.Key.ProfileId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var items = group.ToArray();
                return new ContextEvalAttentionProfileSummary
                {
                    ProfileId = group.Key.ProfileId,
                    PolicyVersion = group.Key.PolicyVersion,
                    SampleCount = items.Length,
                    AvgAttentionMrr = items.Average(item => item.Profile.AttentionMrr),
                    AvgAttentionRecall3 = items.Average(item => item.Profile.AttentionRecall3),
                    AvgAttentionRecall5 = items.Average(item => item.Profile.AttentionRecall5),
                    ImprovedSamples = items.Count(item => item.Profile.Improved),
                    RegressedSamples = items.Count(item => item.Profile.Regressed),
                    CurrentMrrOneRegressionCount = items.Count(item =>
                        item.Profile.Regressed && item.Profile.CurrentMrr >= 0.9999),
                    MustNotHitPromotedCount = items.Sum(item => item.Profile.MustNotHitPromotedCount),
                    SelectedSetChangeRatio = items.Average(item => item.Profile.SelectedSetChangeRatio),
                    CategoryBreakdown = items
                        .GroupBy(item => item.Result.Mode, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(category => category.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(category =>
                        {
                            var categoryItems = category.ToArray();
                            return new ContextEvalAttentionProfileCategorySummary
                            {
                                Category = category.Key,
                                SampleCount = categoryItems.Length,
                                AvgAttentionMrr = categoryItems.Average(item => item.Profile.AttentionMrr),
                                AvgAttentionRecall3 = categoryItems.Average(item => item.Profile.AttentionRecall3),
                                AvgAttentionRecall5 = categoryItems.Average(item => item.Profile.AttentionRecall5),
                                ImprovedSamples = categoryItems.Count(item => item.Profile.Improved),
                                RegressedSamples = categoryItems.Count(item => item.Profile.Regressed),
                                CurrentMrrOneRegressionCount = categoryItems.Count(item =>
                                    item.Profile.Regressed && item.Profile.CurrentMrr >= 0.9999),
                                MustNotHitPromotedCount = categoryItems.Sum(item => item.Profile.MustNotHitPromotedCount),
                                SelectedSetChangeRatio = categoryItems.Average(item => item.Profile.SelectedSetChangeRatio)
                            };
                        })
                        .ToArray()
                };
            })
            .ToArray();
    }

    private static ContextEvalAttentionDiagnostics BuildAttentionDiagnostics(IReadOnlyList<ContextEvalResult> results)
    {
        var rows = results
            .SelectMany(result => result.AttentionProfiles.Select(profile => new { Result = result, Profile = profile }))
            .ToArray();

        return new ContextEvalAttentionDiagnostics
        {
            TopRegressedSamples = rows
                .Where(row => row.Profile.Regressed)
                .OrderByDescending(row => row.Profile.CurrentMrr - row.Profile.AttentionMrr)
                .ThenBy(row => row.Result.SampleId, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(row => ToAttentionDiagnosticSample(row.Result, row.Profile, "attention_mrr_regressed"))
                .ToArray(),
            MustHitDemotedSamples = rows
                .Where(row => row.Profile.MustHitDemotedCount > 0)
                .OrderByDescending(row => row.Profile.MustHitDemotedCount)
                .ThenBy(row => row.Profile.AttentionMrr - row.Profile.CurrentMrr)
                .Take(10)
                .Select(row => ToAttentionDiagnosticSample(row.Result, row.Profile, "must_hit_demoted"))
                .ToArray(),
            MustNotHitPromotedSamples = rows
                .Where(row => row.Profile.MustNotHitPromotedCount > 0 || row.Profile.MustNotHitWouldBeSelectedCount > 0)
                .OrderByDescending(row => row.Profile.MustNotHitPromotedCount + row.Profile.MustNotHitWouldBeSelectedCount)
                .ThenByDescending(row => row.Profile.SelectedSetChangeRatio)
                .Take(10)
                .Select(row => ToAttentionDiagnosticSample(row.Result, row.Profile, "must_not_hit_promoted"))
                .ToArray(),
            SelectedSetChangedSamples = rows
                .Where(row => row.Profile.WouldChangeSelectedSet)
                .OrderByDescending(row => row.Profile.SelectedSetChangeRatio)
                .ThenBy(row => row.Result.SampleId, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(row => ToAttentionDiagnosticSample(row.Result, row.Profile, "selected_set_changed"))
                .ToArray()
        };
    }

    private static ContextEvalAttentionDiagnosticSample ToAttentionDiagnosticSample(
        ContextEvalResult result,
        ContextEvalAttentionProfileResult profile,
        string reason)
    {
        return new ContextEvalAttentionDiagnosticSample
        {
            ProfileId = profile.ProfileId,
            SampleId = result.SampleId,
            Mode = result.Mode,
            CurrentMrr = profile.CurrentMrr,
            AttentionMrr = profile.AttentionMrr,
            MrrDelta = profile.AttentionMrr - profile.CurrentMrr,
            MustHitDemotedCount = profile.MustHitDemotedCount,
            MustNotHitPromotedCount = profile.MustNotHitPromotedCount,
            SelectedSetChangeRatio = profile.SelectedSetChangeRatio,
            Reason = reason,
            CandidateBreakdown = profile.CandidateDiagnostics
        };
    }

    private static ContextEvalModeSummary BuildModeSummary(IGrouping<string, ContextEvalResult> group)
    {
        var items = group.ToArray();
        var total = items.Length;
        var passed = items.Count(r => r.Status == "Passed");
        var passedWithWarnings = items.Count(r => r.Status == "PassedWithWarnings");
        var failed = items.Count(r => r.Status == "Failed");
        var invalid = items.Count(r => r.Status == "InvalidSample");
        var successCount = items.Count(r => r.Succeeded);

        var warningSources = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in items)
        {
            foreach (var reason in result.WarningReasons)
            {
                warningSources[reason] = warningSources.TryGetValue(reason, out var count) ? count + 1 : 1;
            }
        }

        return new ContextEvalModeSummary
        {
            Mode = group.Key,
            TotalSamples = total,
            PassedSamples = passed,
            PassedWithWarningsSamples = passedWithWarnings,
            FailedSamples = failed,
            InvalidSamples = invalid,
            PassRate = total == 0 ? 0.0 : (double)successCount / total,
            AvgRetrievalRecall3 = items.Average(r => r.RetrievalRecall3),
            AvgRetrievalRecall5 = items.Average(r => r.RetrievalRecall5),
            AvgRetrievalRecall10 = items.Average(r => r.RetrievalRecall10),
            AvgRetrievalMrrAnyMustHit = items.Average(r => r.RetrievalMrrAnyMustHit),
            AvgPrimaryMustHitMrr = items.Average(r => r.PrimaryMustHitMrr),
            AvgRetrievalNoiseViolationRatio = items.Average(r => r.RetrievalNoiseViolationRatio),
            AvgAttentionMrr = items.Average(r => r.AttentionMrr),
            AvgAttentionRecall3 = items.Average(r => r.AttentionRecall3),
            AvgAttentionRecall5 = items.Average(r => r.AttentionRecall5),
            AttentionImprovedSamples = items.Count(r => r.AttentionImproved),
            AttentionRegressedSamples = items.Count(r => r.AttentionRegressed),
            MustNotHitPromotedCount = items.Sum(r => r.MustNotHitPromotedCount),
            SelectedSetChangeRatio = items.Average(r => r.AttentionSelectedSetChangeRatio),
            AvgPackageWasteRatio = items.Average(r => r.PackageTokenWasteRatio),
            AvgUnusedBudgetRatio = items.Average(r => r.UnusedBudgetRatio),
            AvgMustHitTokenShare = items.Average(r => r.MustHitTokenShare),
            PackageConstraintHitRate = total == 0 ? 0.0 : (double)items.Count(r => r.PackageHasAllConstraints) / total,
            PackageEntityHitRate = total == 0 ? 0.0 : (double)items.Count(r => r.PackageHasAllEntities) / total,
            PackageUncertaintyHitRate = total == 0 ? 0.0 : (double)items.Count(r => r.PackageHasAllUncertainties) / total,
            AvgCandidatesCount = items.Average(r => r.CandidatesCount),
            AvgSelectedCount = items.Average(r => r.SelectedCount),
            AvgExcludedCount = items.Average(r => r.ExcludedCount),
            WarningSources = warningSources
        };
    }

    private static bool IsExpectedTextSatisfied(string expected, string actualText, bool isEntity)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(actualText))
        {
            return false;
        }

        var normalizedExpected = NormalizeEvalText(expected);
        var normalizedActual = NormalizeEvalText(actualText);
        if (normalizedActual.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsExpectedAliasSatisfied(expected, normalizedActual))
        {
            return true;
        }

        var tokens = ExtractExpectationTokens(expected).ToArray();
        if (tokens.Length == 0)
        {
            return false;
        }

        var matched = tokens.Count(token => normalizedActual.Contains(token, StringComparison.OrdinalIgnoreCase));
        var required = ResolveExpectedTokenThreshold(tokens.Length, isEntity);
        return matched >= required;
    }

    private static bool IsExpectedAliasSatisfied(string expected, string normalizedActual)
    {
        var normalizedExpected = NormalizeEvalText(expected);
        foreach (var (key, groups) in ExpectedTextAliasGroups)
        {
            var normalizedKey = NormalizeEvalText(key);
            if (!normalizedExpected.Contains(normalizedKey, StringComparison.OrdinalIgnoreCase) &&
                !normalizedKey.Contains(normalizedExpected, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var group in groups)
            {
                if (group.All(term => normalizedActual.Contains(NormalizeEvalText(term), StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int ResolveExpectedTokenThreshold(int tokenCount, bool isEntity)
    {
        if (tokenCount <= 1)
        {
            return 1;
        }

        if (isEntity)
        {
            return tokenCount <= 2
                ? 1
                : Math.Max(2, (int)Math.Ceiling(tokenCount * 0.50));
        }

        return tokenCount <= 2
            ? tokenCount
            : Math.Max(2, (int)Math.Ceiling(tokenCount * 0.65));
    }

    private static IEnumerable<string> ExtractExpectationTokens(string text)
    {
        var normalized = NormalizeEvalText(text);
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keywordHits = ChineseExpectationKeywords
            .Select(NormalizeEvalText)
            .Where(keyword => keyword.Length >= 2 && normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var keyword in keywordHits)
        {
            tokens.Add(keyword);
        }

        var current = new StringBuilder();

        void Flush()
        {
            var token = current.ToString();
            var containsCjk = token.Any(IsCjk);
            if (token.Length >= 2 && (!containsCjk || token.Length <= 6 || keywordHits.Length == 0))
            {
                tokens.Add(token);
            }

            current.Clear();
        }

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                current.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                Flush();
            }
        }

        Flush();

        // 中文期望优先使用领域关键词；没有关键词时才回退到二字片段，避免“码注/释和”这类噪音抬高门槛。
        if (keywordHits.Length == 0)
        {
            for (var i = 0; i < normalized.Length - 1; i++)
            {
                var part = normalized.Substring(i, 2);
                if (part.Any(IsCjk) && IsUsefulChineseExpectationPart(part))
                {
                    tokens.Add(part);
                }
            }
        }

        return tokens.Where(token => !IsWeakExpectationToken(token));
    }

    private static string NormalizeEvalText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch) || IsCjk(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    private static bool IsCjk(char ch) =>
        ch >= '\u4e00' && ch <= '\u9fff';

    private static readonly string[] ChineseExpectationKeywords =
    [
        "中文", "输出", "日志", "注释", "提示", "代码注释", "阶段性结论", "重复解释",
        "当前会话", "会话", "当前时间", "时间范围", "不确定性", "不确定", "说明",
        "废弃设定", "上下文包", "伏笔", "钟声伏笔", "人物状态", "物品状态",
        "主角", "断剑", "死信队列", "重试次数", "最大重试", "处理人",
        "失败步骤", "成功步骤", "自动化流程", "恢复点", "修复建议", "自动化报告", "可执行动作", "构建", "测试",
        "密钥扫描", "最终结论", "已验证", "未验证", "测试失败", "断言",
        "业务逻辑", "行为风险", "测试风险点", "覆盖率", "快照测试",
        "密钥配置", "密钥", "配置", "私有配置", "用户私有配置目录", "目录", "仓库文件", "仓库", "文件",
        "运行时", "规则", "约束", "魔法", "代价", "风险"
    ];

    private static readonly Dictionary<string, string[][]> ExpectedTextAliasGroups =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["临时情绪不应提升"] =
            [
                ["临时情绪", "不应提升"],
                ["临时情绪", "promotion"]
            ],
            ["关系状态用最新版本"] =
            [
                ["当前关系", "最新版本"],
                ["疏离感", "关系状态"]
            ],
            ["伏笔兑现"] =
            [
                ["伏笔", "兑现"]
            ],
            ["废弃结局不得召回"] =
            [
                ["旧版结局", "不得召回"],
                ["没有主角死亡设定", "旧版结局"]
            ],
            ["废弃设定不得进入上下文包"] =
            [
                ["禁止吃书设定"]
            ],
            ["只修复相关断言，不做无关重构"] =
            [
                ["只修复相关断言", "不改业务逻辑"]
            ],
            ["处理人"] =
            [
                ["负责人"]
            ],
            ["自动化报告"] =
            [
                ["本次运行", "报告"],
                ["报告", "可执行动作"]
            ],
            ["断剑"] =
            [
                ["剑已经断"]
            ],
            ["最终结论"] =
            [
                ["最终报告"]
            ]
        };

    private static readonly HashSet<char> ChineseExpectationWeakChars = new()
    {
        '的', '了', '在', '是', '和', '与', '或', '而', '及', '并', '不', '无',
        '应', '需', '须', '得', '要', '能', '否', '时', '后', '前', '本', '次'
    };

    private static bool IsUsefulChineseExpectationPart(string token)
    {
        if (token.Length < 2)
        {
            return false;
        }

        return token.Any(IsCjk) && !token.Any(ChineseExpectationWeakChars.Contains);
    }

    private static bool IsWeakExpectationToken(string token) =>
        token is "需要" or "必须" or "不得" or "不能" or "不要" or "已经" or "进行"
            or "应当" or "应该"
            or "the" or "and" or "with";

    private sealed record AttentionEvalMetrics(
        double Mrr,
        double Recall3,
        double Recall5,
        double CurrentMrr,
        bool Improved,
        bool Regressed,
        bool WouldChangeSelectedSet,
        int MustHitDemotedCount,
        int MustNotHitPromotedCount,
        int MustNotHitWouldBeSelectedCount,
        double SelectedSetChangeRatio);
}
