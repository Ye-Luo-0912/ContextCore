using System.Text.Json;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Embedding;
using ContextCore.Storage.InMemory;

namespace ContextCore.ControlRoom.Services;

/// <summary>运行 planning proposal shadow retrieval comparison，不影响正式 eval/package 输出。</summary>
public sealed class PlanningShadowEvalRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task<ShadowRetrievalComparisonReport> RunAsync(
        string contextsRootPath,
        string? categoryFilter = null,
        bool includeSeedBatches = false,
        CancellationToken cancellationToken = default)
    {
        var comparisons = new List<ShadowRetrievalComparisonItem>();
        var categories = new[] { "chat", "project", "novel", "automation", "coding-mode" };

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (categoryFilter is not null
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRootPath, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var corpus = await LoadCategoryCorpusAsync(categoryDir, includeSeedBatches, cancellationToken)
                .ConfigureAwait(false);
            var samples = await LoadCategorySamplesAsync(categoryDir, includeSeedBatches, cancellationToken)
                .ConfigureAwait(false);
            if (samples.Count == 0)
            {
                continue;
            }

            var workspaceId = $"eval-{category}";
            var collectionId = "test";
            var state = ControlRoomService.CreateState(
                "memory",
                "eval",
                workspaceId,
                collectionId);
            await SeedCorpusAsync(state, corpus, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);

            var planningSnapshotService = new PlanningSnapshotService(
                new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
                state.MemoryStore,
                state.ConstraintStore,
                new InMemoryContextLearningStore());
            var proposalService = new RetrievalPlanProposalService(
                planningSnapshotService,
                new PlanningIntentDetector());
            var shadowExecutor = new ShadowRetrievalPlanExecutor(
                state.ContextStore,
                state.MemoryStore,
                state.RelationStore,
                constraintStore: state.ConstraintStore);
            var snapshot = await planningSnapshotService
                .GetSnapshotAsync(workspaceId, collectionId, sessionId: null, cancellationToken)
                .ConfigureAwait(false);

            foreach (var sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = await CreateRetrievalRequestAsync(
                        state,
                        workspaceId,
                        collectionId,
                        sample,
                        cancellationToken)
                    .ConfigureAwait(false);
                var legacy = await state.Retriever.RetrieveAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                var proposal = proposalService.Propose(snapshot, sample.Query, sample.Mode);
                var shadow = await shadowExecutor.ExecuteAsync(proposal, request, cancellationToken)
                    .ConfigureAwait(false);
                comparisons.Add(ShadowRetrievalComparisonReportBuilder.BuildSample(sample, legacy, shadow));
            }
        }

        return ShadowRetrievalComparisonReportBuilder.Build(
            includeSeedBatches ? "extended" : "a3",
            comparisons);
    }

    public async Task<ShadowRetrievalComparisonReport> RunOptInAsync(
        string contextsRootPath,
        IReadOnlyList<string> optInIntents,
        string? categoryFilter = null,
        bool includeSeedBatches = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(optInIntents);

        var comparisons = new List<ShadowRetrievalComparisonItem>();
        var categories = new[] { "chat", "project", "novel", "automation", "coding-mode" };

        foreach (var category in categories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (categoryFilter is not null
                && !string.Equals(category, categoryFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var categoryDir = Path.Combine(contextsRootPath, category);
            if (!Directory.Exists(categoryDir))
            {
                continue;
            }

            var corpus = await LoadCategoryCorpusAsync(categoryDir, includeSeedBatches, cancellationToken)
                .ConfigureAwait(false);
            var samples = await LoadCategorySamplesAsync(categoryDir, includeSeedBatches, cancellationToken)
                .ConfigureAwait(false);
            if (samples.Count == 0)
            {
                continue;
            }

            var workspaceId = $"eval-{category}";
            var collectionId = "test";
            var state = ControlRoomService.CreateState(
                "memory",
                "eval",
                workspaceId,
                collectionId);
            await SeedCorpusAsync(state, corpus, workspaceId, collectionId, cancellationToken)
                .ConfigureAwait(false);

            var planningSnapshotService = new PlanningSnapshotService(
                new InMemoryShortTermMemoryStore(new ShortTermMemoryPolicy()),
                state.MemoryStore,
                state.ConstraintStore,
                new InMemoryContextLearningStore());
            var safetyProfile = RetrievalPlanSafetyProfile.CreateDefault();
            var proposalService = new RetrievalPlanProposalService(
                planningSnapshotService,
                new PlanningIntentDetector(),
                safetyProfile);
            var shadowExecutor = new ShadowRetrievalPlanExecutor(
                state.ContextStore,
                state.MemoryStore,
                state.RelationStore,
                new RetrievalPlanProposalValidator(safetyProfile),
                state.ConstraintStore);
            var optInRetriever = new HybridContextRetriever(
                state.ContextStore,
                state.MemoryStore,
                state.RelationStore,
                state.EmbeddingProvider,
                state.VectorStore,
                traceStore: null,
                attentionScorer: new RuleBasedContextAttentionScorer(),
                attentionRerankOptions: new RetrievalAttentionRerankOptions(),
                planningOptions: new RetrievalPlanningOptions
                {
                    Mode = RetrievalPlanningOptions.ApplyGuardedMode,
                    ApplyMode = RetrievalPlanningOptions.IntentScopedApplyMode,
                    OptInIntents = optInIntents.ToArray(),
                    FallbackToLegacyOnViolation = true,
                    EmitComparisonTrace = true
                },
                planningProposalService: proposalService,
                planningShadowExecutor: shadowExecutor);

            foreach (var sample in samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = await CreateRetrievalRequestAsync(
                        state,
                        workspaceId,
                        collectionId,
                        sample,
                        cancellationToken)
                    .ConfigureAwait(false);
                var legacy = await state.Retriever.RetrieveAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                var optIn = await optInRetriever.RetrieveAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                comparisons.Add(ShadowRetrievalComparisonReportBuilder.BuildSample(
                    sample,
                    legacy,
                    ToOptInShadowResult(optIn)));
            }
        }

        return ShadowRetrievalComparisonReportBuilder.Build(
            includeSeedBatches ? "extended" : "a3",
            comparisons);
    }

    private static async Task<IReadOnlyList<ContextEvalSample>> LoadCategorySamplesAsync(
        string categoryDir,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        if (includeSeedBatches)
        {
            var result = await new ContextEvalSampleLoader()
                .LoadAsync(categoryDir, cancellationToken)
                .ConfigureAwait(false);
            return result.Samples;
        }

        var samplesPath = Path.Combine(categoryDir, "seed_samples.json");
        if (!File.Exists(samplesPath))
        {
            return [];
        }

        return JsonSerializer.Deserialize<IReadOnlyList<ContextEvalSample>>(
            await File.ReadAllTextAsync(samplesPath, cancellationToken).ConfigureAwait(false),
            JsonOptions) ?? [];
    }

    private static async Task<ContextEvalCorpus> LoadCategoryCorpusAsync(
        string categoryDir,
        bool includeSeedBatches,
        CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<string, ContextItem>(StringComparer.OrdinalIgnoreCase);
        var memories = new Dictionary<string, ContextMemoryItem>(StringComparer.OrdinalIgnoreCase);
        var relations = new Dictionary<string, ContextRelation>(StringComparer.OrdinalIgnoreCase);
        var constraints = new Dictionary<string, ContextConstraint>(StringComparer.OrdinalIgnoreCase);
        var corpusFiles = includeSeedBatches
            ? Directory.EnumerateFiles(categoryDir, "corpus*.json", SearchOption.TopDirectoryOnly)
            : File.Exists(Path.Combine(categoryDir, "corpus.json"))
                ? [Path.Combine(categoryDir, "corpus.json")]
                : Enumerable.Empty<string>();

        foreach (var file in corpusFiles.Order(StringComparer.OrdinalIgnoreCase))
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
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
        }

        return new ContextEvalCorpus
        {
            Contexts = contexts.Values.ToArray(),
            Memories = memories.Values.ToArray(),
            Relations = relations.Values.ToArray(),
            Constraints = constraints.Values.ToArray()
        };
    }

    private static async Task SeedCorpusAsync(
        ControlRoomState state,
        ContextEvalCorpus corpus,
        string workspaceId,
        string collectionId,
        CancellationToken cancellationToken)
    {
        foreach (var ctx in corpus.Contexts)
        {
            var normalized = new ContextItem
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
            await state.ContextStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        foreach (var mem in corpus.Memories)
        {
            var normalized = new ContextMemoryItem
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
            await state.MemoryStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        foreach (var relation in corpus.Relations)
        {
            var normalized = new ContextRelation
            {
                Id = relation.Id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SourceId = relation.SourceId,
                TargetId = relation.TargetId,
                RelationType = relation.RelationType,
                Weight = relation.Weight,
                Confidence = relation.Confidence,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await state.RelationStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        foreach (var constraint in corpus.Constraints)
        {
            var normalized = new ContextConstraint
            {
                Id = constraint.Id,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                Level = constraint.Level,
                Status = constraint.Status,
                Scope = constraint.Scope,
                Content = constraint.Content,
                Metadata = constraint.Metadata,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await state.ConstraintStore.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        var memoryIds = corpus.Memories
            .Select(memory => memory.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inputs = corpus.Contexts
            .Select(context => new EmbeddingInput
            {
                Id = context.Id,
                Text = $"{context.Title} {context.Content}",
                SourceRef = context.Id
            })
            .Concat(corpus.Memories.Select(memory => new EmbeddingInput
            {
                Id = memory.Id,
                Text = memory.Content,
                SourceRef = memory.Id
            }))
            .ToArray();

        if (inputs.Length == 0)
        {
            return;
        }

        var embedResult = await state.EmbeddingProvider.EmbedAsync(new EmbeddingRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            InputKind = EmbeddingInputKind.Text,
            Inputs = inputs
        }, cancellationToken).ConfigureAwait(false);
        foreach (var vector in embedResult.Vectors)
        {
            await state.VectorStore.UpsertAsync(new VectorRecord
            {
                Id = "vec-" + vector.InputId,
                WorkspaceId = workspaceId,
                CollectionId = collectionId,
                SourceId = vector.SourceRef,
                SourceKind = memoryIds.Contains(vector.SourceRef) ? "memory" : "context",
                ModelName = embedResult.ModelName,
                Dimensions = embedResult.Dimensions,
                Vector = vector.Values,
                ContentHash = vector.Metadata.TryGetValue("contentHash", out var hash) ? hash : vector.InputId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ContextRetrievalRequest> CreateRetrievalRequestAsync(
        ControlRoomState state,
        string workspaceId,
        string collectionId,
        ContextEvalSample sample,
        CancellationToken cancellationToken)
    {
        var tokenBudget = 4000;
        if (sample.Metadata.TryGetValue("tokenBudget", out var budgetValue)
            && int.TryParse(budgetValue, out var parsedBudget)
            && parsedBudget > 0)
        {
            tokenBudget = parsedBudget;
        }

        var queryVector = Array.Empty<float>();
        var embedResult = await state.EmbeddingProvider.EmbedAsync(new EmbeddingRequest
        {
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            InputKind = EmbeddingInputKind.Query,
            Inputs = [new EmbeddingInput { Id = "query", Text = sample.Query }]
        }, cancellationToken).ConfigureAwait(false);
        if (embedResult.Succeeded && embedResult.Vectors.Count > 0)
        {
            queryVector = embedResult.Vectors[0].Values.ToArray();
        }

        return new ContextRetrievalRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = workspaceId,
            CollectionId = collectionId,
            QueryText = sample.Query,
            QueryVector = queryVector,
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
                ["attention.mustNotHit"] = string.Join(",", sample.MustNotHit),
                ["planning.mode"] = sample.Mode,
                ["eval.expectedConstraints"] = string.Join(",", sample.ExpectedConstraints)
            }
        };
    }

    private static ShadowRetrievalResult ToOptInShadowResult(ContextRetrievalResult result)
    {
        var metadata = result.Trace.Metadata;
        var diagnostics = metadata
            .Where(pair => pair.Key.StartsWith("planningShadow.", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                pair => pair.Key["planningShadow.".Length..],
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);

        diagnostics.TryAdd("validatorApplied", "false");
        diagnostics.TryAdd("validPlan", "true");
        diagnostics.TryAdd("repairedPlan", "false");
        diagnostics.TryAdd("fallbackToLegacySafePlan", "false");
        diagnostics.TryAdd("vectorDisabled", "true");
        diagnostics.TryAdd("mustNotHitAddedAfterValidation", "0");
        diagnostics.TryAdd("lifecycleViolationAfterValidation", "0");
        diagnostics["planningExecutionStatus"] = metadata.GetValueOrDefault("planningExecutionStatus", "Legacy");
        diagnostics["planningFallbackUsed"] = metadata.GetValueOrDefault("planningFallbackUsed", "false");
        diagnostics["planningFallbackReason"] = metadata.GetValueOrDefault("planningFallbackReason", string.Empty);
        diagnostics["planningOptInMatched"] = metadata.GetValueOrDefault("planningOptInMatched", "false");
        diagnostics["planningLegacySelected"] = metadata.GetValueOrDefault("planningLegacySelected", string.Empty);
        diagnostics["planningProposalSelected"] = metadata.GetValueOrDefault("planningProposalSelected", string.Empty);
        diagnostics["planningFinalSelected"] = metadata.GetValueOrDefault("planningFinalSelected", string.Empty);
        diagnostics["planningSafetyChecks"] = metadata.GetValueOrDefault("planningSafetyChecks", string.Empty);
        diagnostics["shadowVectorEnabled"] = "false";

        var intent = metadata.GetValueOrDefault("planningIntent", "Unknown");
        var mode = metadata.GetValueOrDefault("planningShadow.effectiveMode", string.Empty);
        if (string.IsNullOrWhiteSpace(mode))
        {
            mode = metadata.GetValueOrDefault("planningApplyMode", RetrievalPlanningOptions.IntentScopedApplyMode);
        }

        return new ShadowRetrievalResult
        {
            OperationId = result.OperationId,
            ProposalId = metadata.GetValueOrDefault("planningProposalId", string.Empty),
            ProposalSummary = $"{intent}/{mode}",
            ShadowCandidates = result.Trace.Candidates,
            ShadowSelectedItems = result.SelectedItems,
            Diagnostics = diagnostics,
            Warnings = metadata.TryGetValue("planningWarnings", out var warnings)
                ? warnings.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>()
        };
    }
}
