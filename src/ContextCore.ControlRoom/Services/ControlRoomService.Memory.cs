using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.Client;
using ContextCore.Core;
using ContextCore.Core.Services;
using ContextCore.Core.Services.Graph;
using ContextCore.Core.Services.Retrieval;
using ContextCore.Core.Services.Storage;
using ContextCore.Embedding;
using ContextCore.Embedding.Providers;
using ContextCore.ModelGateway;
using ContextCore.ModelGateway.Infrastructure;
using ContextCore.Storage.FileSystem;
using ContextCore.Storage.FileSystem.Stores;
using ContextCore.Storage.InMemory;
using ContextCore.Storage.InMemory.Stores;

namespace ContextCore.ControlRoom.Services;

public sealed partial class ControlRoomService
{

    public async Task<LearningFeedbackSubmitResult> SubmitLearningFeedbackAsync(
        LearningFeedbackSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            return await GetServiceClient()
                .SubmitLearningFeedbackAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }

        return await new LearningFeedbackService(_state.LearningFeedbackStore!)
            .SubmitAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<LearningFeedbackReviewResult> ReviewLearningFeedbackAsync(
        string feedbackId,
        FeedbackReviewStatus status,
        LearningFeedbackReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_state.IsServiceMode)
        {
            return status switch
            {
                FeedbackReviewStatus.ApprovedForDataset => await GetServiceClient()
                    .ApproveLearningFeedbackAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.Rejected => await GetServiceClient()
                    .RejectLearningFeedbackAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.NeedsRedaction => await GetServiceClient()
                    .MarkLearningFeedbackNeedsRedactionAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                FeedbackReviewStatus.NeedsMoreEvidence => await GetServiceClient()
                    .MarkLearningFeedbackNeedsEvidenceAsync(feedbackId, request, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unsupported feedback review status: {status}", nameof(status))
            };
        }

        var service = new LearningFeedbackReviewService(_state.LearningFeedbackStore!, _state.LearningFeedbackReviewStore!);
        return status switch
        {
            FeedbackReviewStatus.ApprovedForDataset => await service.ApproveAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            FeedbackReviewStatus.Rejected => await service.RejectAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            FeedbackReviewStatus.NeedsRedaction => await service.NeedsRedactionAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            FeedbackReviewStatus.NeedsMoreEvidence => await service.NeedsMoreEvidenceAsync(feedbackId, request, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new ArgumentException($"Unsupported feedback review status: {status}", nameof(status))
        };
    }

    public async Task<MemoryStatusBreakdown> GetMemoryStatusBreakdownAsync(
        CancellationToken cancellationToken = default)
    {
        var allMemory = await QueryMemoryAsync(null, null, int.MaxValue, cancellationToken).ConfigureAwait(false);

        return new MemoryStatusBreakdown
        {
            Total = allMemory.Count,
            WorkingLayer = allMemory.Count(item => item.Layer == ContextMemoryLayer.Working),
            StructuredLayer = allMemory.Count(item => item.Layer == ContextMemoryLayer.Structured),
            StableLayer = allMemory.Count(item => item.Layer == ContextMemoryLayer.Stable),
            Candidate = allMemory.Count(item => item.Status == ContextMemoryStatus.Candidate),
            Verified = allMemory.Count(item => item.Status == ContextMemoryStatus.Verified),
            Stable = allMemory.Count(item => item.Status == ContextMemoryStatus.Stable),
            Deprecated = allMemory.Count(item => item.Status == ContextMemoryStatus.Deprecated),
            Rejected = allMemory.Count(item => item.Status == ContextMemoryStatus.Rejected)
        };
    }

    public async Task<IReadOnlyList<ControlRoomListItem>> ListAsync(
        string layer,
        string? type,
        string? tag,
        string? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        layer = layer.ToLowerInvariant();

        switch (layer)
        {
            case "raw":
            {
                var rawItems = await QueryRawAsync(take, cancellationToken).ConfigureAwait(false);
                return rawItems
                    .Where(item => string.IsNullOrWhiteSpace(type) || string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
                    .Where(item => string.IsNullOrWhiteSpace(tag) || item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    .Select(item => new ControlRoomListItem
                    {
                        Id = item.Id,
                        Kind = "raw",
                        Layer = "Raw",
                        Type = item.Type,
                        Status = "",
                        Tags = string.Join(",", item.Tags),
                        UpdatedAt = item.UpdatedAt,
                        Preview = Preview(item.Content)
                    })
                    .ToArray();
            }
            case "constraints" or "constraint":
            {
                ConstraintLevel? level = null;
                if (Enum.TryParse<ConstraintLevel>(status, ignoreCase: true, out var parsedLevel))
                {
                    level = parsedLevel;
                }

                var constraints = await QueryConstraintsAsync(level, take, cancellationToken).ConfigureAwait(false);
                return constraints.Select(item => new ControlRoomListItem
                {
                    Id = item.Id,
                    Kind = "constraint",
                    Layer = "Constraint",
                    Type = item.Level.ToString(),
                    Status = item.Status.ToString(),
                    Tags = item.Scope.ToString(),
                    UpdatedAt = item.UpdatedAt,
                    Preview = Preview(item.Content)
                }).ToArray();
            }
            case "relations" or "relation":
            {
                var relations = await QueryRelationsAsync(take, cancellationToken).ConfigureAwait(false);
                return relations.Select(item => new ControlRoomListItem
                {
                    Id = item.Id,
                    Kind = "relation",
                    Layer = "Relation",
                    Type = item.RelationType,
                    Status = item.Confidence.ToString("0.00"),
                    Tags = $"{item.SourceId} -> {item.TargetId}",
                    UpdatedAt = item.CreatedAt,
                    Preview = string.Join(",", item.SourceRefs)
                }).ToArray();
            }
        }

        var memoryLayer = ParseMemoryLayer(layer);
        var memoryStatus = ParseMemoryStatus(status);
        var memories = await QueryMemoryAsync(memoryLayer, memoryStatus, take, cancellationToken).ConfigureAwait(false);

        return memories
            .Where(item => string.IsNullOrWhiteSpace(type) || string.Equals(item.Type, type, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(tag) || item.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            .Select(item => new ControlRoomListItem
            {
                Id = item.Id,
                Kind = "memory",
                Layer = item.Layer.ToString(),
                Type = item.Type,
                Status = item.Status.ToString(),
                Tags = string.Join(",", item.Tags),
                UpdatedAt = item.UpdatedAt,
                Preview = Preview(item.Content)
            })
            .ToArray();
    }

    public async Task<ControlRoomDetail?> ShowAsync(string id, CancellationToken cancellationToken = default)
    {
        var raw = await _state.ContextStore!.GetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken).ConfigureAwait(false);

        if (raw is not null)
        {
            var relations = await GetRelationsForIdAsync(id, cancellationToken).ConfigureAwait(false);
            return DetailFromRaw(raw, relations);
        }

        var memory = await _state.MemoryStore!.GetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            id,
            cancellationToken).ConfigureAwait(false);

        if (memory is not null)
        {
            var relations = await GetRelationsForIdAsync(id, cancellationToken).ConfigureAwait(false);
            return DetailFromMemory(memory, relations);
        }

        var constraints = await QueryConstraintsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var constraint = constraints.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (constraint is not null)
        {
            return DetailFromConstraint(constraint);
        }

        var relationsAll = await QueryRelationsAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var relation = relationsAll.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        if (relation is not null)
        {
            return DetailFromRelation(relation);
        }

        var jobs = await QueryJobsAsync(null, int.MaxValue, cancellationToken).ConfigureAwait(false);
        var job = jobs.FirstOrDefault(item => string.Equals(item.JobId, id, StringComparison.OrdinalIgnoreCase));
        return job is null ? null : DetailFromJob(job);
    }

    public async Task<ContextPackage> BuildPackagePreviewAsync(
        int tokenBudget,
        bool usePolicy,
        CancellationToken cancellationToken = default)
    {
        return await BuildPackagePreviewAsync(tokenBudget, usePolicy, policyId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ContextPackage> BuildPackagePreviewAsync(
        int tokenBudget,
        bool usePolicy,
        string? policyId,
        CancellationToken cancellationToken = default)
    {
        var result = await _state.PackageBuilder!
            .BuildDetailedAsync(
                await CreatePackagePreviewRequestAsync(tokenBudget, usePolicy, policyId, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        _state.LastPackage = result.Package;
        return result.Package;
    }

    public async Task<PackagePreviewDetails> BuildPackagePreviewDetailsAsync(
        int tokenBudget,
        bool usePolicy,
        CancellationToken cancellationToken = default)
    {
        return await BuildPackagePreviewDetailsAsync(tokenBudget, usePolicy, policyId: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<PackagePreviewDetails> BuildPackagePreviewDetailsAsync(
        int tokenBudget,
        bool usePolicy,
        string? policyId,
        CancellationToken cancellationToken = default)
    {
        var result = await _state.PackageBuilder!
            .BuildDetailedAsync(
                await CreatePackagePreviewRequestAsync(tokenBudget, usePolicy, policyId, cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken)
            .ConfigureAwait(false);
        _state.LastPackage = result.Package;
        var recentTrace = _state.RetrievalTraceStore is null
            ? null
            : (await _state.RetrievalTraceStore!.QueryRecentAsync(
                    _state.WorkspaceId,
                    _state.CollectionId,
                    1,
                    cancellationToken).ConfigureAwait(false))
                .FirstOrDefault();

        return new PackagePreviewDetails
        {
            Package = result.Package,
            SelectedItems = result.SelectedItems.Select(PackageCandidateItem.FromDecision).ToArray(),
            DroppedItems = result.DroppedItems.Select(PackageCandidateItem.FromDropped).ToArray(),
            Uncertainties = result.Uncertainties,
            Budget = result.Budget,
            PlanningMetadata = recentTrace?.Metadata ?? new Dictionary<string, string>()
        };
    }

    public Task<IReadOnlyList<ContextPackagePolicy>> ListPoliciesAsync(
        string? queryText = null,
        CancellationToken cancellationToken = default)
    {
        return _state.PackagePolicyStore!.QueryAsync(new ContextPackagePolicyQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            QueryText = queryText,
            Take = int.MaxValue
        }, cancellationToken);
    }

    public Task<ContextPackagePolicy?> GetPolicyAsync(
        string policyId,
        CancellationToken cancellationToken = default)
    {
        return _state.PackagePolicyStore!.GetAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            policyId,
            cancellationToken);
    }

    public async Task SavePolicyAsync(ContextPackagePolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var normalized = new ContextPackagePolicy
        {
            Id = policy.Id,
            WorkspaceId = string.IsNullOrWhiteSpace(policy.WorkspaceId) ? _state.WorkspaceId : policy.WorkspaceId,
            CollectionId = string.IsNullOrWhiteSpace(policy.CollectionId) ? _state.CollectionId : policy.CollectionId,
            Name = policy.Name,
            Description = policy.Description,
            TokenBudget = policy.TokenBudget,
            IncludeGlobalContext = policy.IncludeGlobalContext,
            IncludeHardConstraints = policy.IncludeHardConstraints,
            IncludeSoftConstraints = policy.IncludeSoftConstraints,
            IncludeWorkingMemory = policy.IncludeWorkingMemory,
            IncludeStableMemory = policy.IncludeStableMemory,
            IncludeRecentRawContext = policy.IncludeRecentRawContext,
            MaxRecentItems = policy.MaxRecentItems,
            SectionOrder = policy.SectionOrder.ToArray(),
            SectionPriorities = new Dictionary<string, int>(policy.SectionPriorities),
            SectionTokenBudgets = new Dictionary<string, int>(policy.SectionTokenBudgets),
            Metadata = new Dictionary<string, string>(policy.Metadata)
        };

        await _state.PackagePolicyStore!.SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ContextPackageRequest> CreatePackagePreviewRequestAsync(
        int tokenBudget,
        bool usePolicy,
        string? policyId,
        CancellationToken cancellationToken)
    {
        ContextPackagePolicy? policy = null;
        if (!string.IsNullOrWhiteSpace(policyId))
        {
            policy = await _state.PackagePolicyStore!.GetAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                policyId,
                cancellationToken).ConfigureAwait(false);
            if (policy is null)
            {
                throw new InvalidOperationException($"未找到策略：{policyId}");
            }
        }
        else if (usePolicy)
        {
            policy = new ContextPackagePolicy
            {
                Id = "control-room-preview",
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                TokenBudget = tokenBudget,
                IncludeGlobalContext = true,
                IncludeHardConstraints = true,
                IncludeSoftConstraints = true,
                IncludeWorkingMemory = true,
                IncludeStableMemory = true,
                IncludeRecentRawContext = true,
                MaxRecentItems = 20
            };
        }

        return new ContextPackageRequest
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            TokenBudget = tokenBudget,
            Policy = policy,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "control-room",
                ["tokenBudget"] = tokenBudget.ToString(),
                ["policyId"] = policy?.Id ?? string.Empty
            }
        };
    }

    public async Task<RetrievalDebugDetails> BuildRetrievalDebugAsync(
        string queryText,
        string? rewrittenQueryText = null,
        IReadOnlyList<float>? queryVector = null,
        int topK = 10,
        int tokenBudget = 1200,
        int candidateTake = 50,
        int vectorTopK = 20,
        bool includeKeywordRecall = true,
        bool includeVectorRecall = true,
        bool includeRelationExpansion = true,
        CancellationToken cancellationToken = default)
    {
        var result = await _state.Retriever!.RetrieveAsync(new ContextRetrievalRequest
        {
            OperationId = Guid.NewGuid().ToString("N"),
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            QueryText = queryText,
            RewrittenQueryText = rewrittenQueryText,
            QueryVector = queryVector ?? Array.Empty<float>(),
            TopK = topK,
            TokenBudget = tokenBudget,
            CandidateTake = candidateTake,
            VectorTopK = vectorTopK,
            IncludeKeywordRecall = includeKeywordRecall,
            IncludeVectorRecall = includeVectorRecall,
            IncludeRelationExpansion = includeRelationExpansion,
            IncludeWorkingMemory = true,
            IncludeStableMemory = true,
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "ControlRoom",
                ["debug"] = "true"
            }
        }, cancellationToken).ConfigureAwait(false);
        var package = BuildRetrievalDebugPackage(result, tokenBudget);
        _state.LastPackage = package;

        return new RetrievalDebugDetails
        {
            Result = result,
            Package = package,
            RecentTraces = await _state.RetrievalTraceStore!.QueryRecentAsync(
                _state.WorkspaceId,
                _state.CollectionId,
                10,
                cancellationToken).ConfigureAwait(false)
        };
    }

    private static ContextPackage BuildRetrievalDebugPackage(
        ContextRetrievalResult result,
        int tokenBudget)
    {
        var sections = result.SelectedItems
            .Select((item, index) => new ContextPackageSection
            {
                Name = $"{item.Kind}:{item.SourceId}",
                Priority = 100 - index,
                Content = item.Content,
                ContentFormat = item.ContentFormat,
                SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : [item.SourceId],
                ItemRefs = [item.SourceId],
                EstimatedTokens = item.EstimatedTokens
            })
            .ToArray();

        return new ContextPackage
        {
            PackageId = $"retrieval-debug-{result.OperationId}",
            WorkspaceId = result.Trace.WorkspaceId,
            CollectionId = result.Trace.CollectionId,
            Sections = sections,
            EstimatedTokens = sections.Sum(section => section.EstimatedTokens),
            SourceRefs = result.SelectedItems.Select(item => item.SourceId).ToArray(),
            Metadata = new Dictionary<string, string>
            {
                ["source"] = "ControlRoom Retrieval Debug",
                ["retrievalId"] = result.Trace.RetrievalId,
                ["tokenBudget"] = tokenBudget.ToString(),
                ["queryText"] = result.Trace.QueryText ?? "",
                ["rewrittenQueryText"] = result.Trace.RewrittenQueryText ?? ""
            },
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public Task<IReadOnlyList<ContextJob>> QueryJobsAsync(
        ContextJobState? state,
        int take,
        CancellationToken cancellationToken = default)
    {
        return _state.JobQueryStore!.QueryAsync(new ContextJobQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            State = state,
            Take = take
        }, cancellationToken);
    }

    public async Task<ContextPromotionRecord> PromoteAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return await _state.PromotionService!.PromoteAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            memoryId,
            "control-room",
            "由 ControlRoom 晋升。",
            1.0,
            cancellationToken,
            Environment.UserName).ConfigureAwait(false);
    }

    public async Task<ContextPromotionRecord> RejectAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return await _state.PromotionService!.RejectAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            memoryId,
            "control-room",
            "由 ControlRoom 拒绝。",
            1.0,
            cancellationToken,
            Environment.UserName).ConfigureAwait(false);
    }

    public async Task<ContextPromotionRecord> DeprecateAsync(
        string memoryId,
        CancellationToken cancellationToken = default)
    {
        return await _state.PromotionService!.DeprecateAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            memoryId,
            "control-room",
            "由 ControlRoom 标记废弃。",
            1.0,
            cancellationToken,
            Environment.UserName).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PromotionCandidate>> ListPromotionCandidatesAsync(
        PromotionCandidateStatus? status,
        int take,
        CancellationToken cancellationToken = default)
    {
        return _state.PromotionCandidateStore!.QueryPromotionCandidatesAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            status,
            take,
            cancellationToken);
    }

    public Task<PromotionCandidate?> GetPromotionCandidateAsync(
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        return _state.PromotionCandidateStore!.GetPromotionCandidateAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            cancellationToken);
    }

    public Task<PromotionCandidate?> UpdatePromotionCandidateStatusAsync(
        string candidateId,
        PromotionCandidateStatus status,
        string? reviewer = null,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        return _state.PromotionCandidateStore!.UpdatePromotionCandidateStatusAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            status,
            reviewer,
            reason,
            cancellationToken);
    }

    /// <summary>
    /// 接受 Promotion 候选项并执行实际记忆写入：
    /// - SourceKind="memory"：对已有记忆条目调用 PromoteAsync 晋升到 Stable 层，并生成审计日志。
    /// - 其他 SourceKind：将候选内容写入工作记忆 (WorkingMemoryItem)，供后续晋升使用。
    /// </summary>
    public async Task<(PromotionCandidate? Candidate, string PromotionDetail)> ExecuteAcceptAsync(
        string candidateId,
        string? reviewer,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var candidate = await _state.PromotionCandidateStore!.GetPromotionCandidateAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            cancellationToken).ConfigureAwait(false);

        if (candidate is null)
        {
            return (null, string.Empty);
        }

        // 先更新候选项状态为 Accepted
        var updated = await _state.PromotionCandidateStore!.UpdatePromotionCandidateStatusAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            candidateId,
            PromotionCandidateStatus.Accepted,
            reviewer,
            reason,
            cancellationToken).ConfigureAwait(false);

        if (updated is null)
        {
            return (null, string.Empty);
        }

        var detail = new StringBuilder();
        var effectiveReason = reason ?? "候选项已接受";
        var effectiveReviewer = reviewer ?? Environment.UserName;

        if (!string.IsNullOrWhiteSpace(candidate.SourceId) &&
            string.Equals(candidate.SourceKind, "memory", StringComparison.OrdinalIgnoreCase))
        {
            // 已有记忆条目：通过 PromotionService 晋升并生成审计日志
            try
            {
                var record = await _state.PromotionService!.PromoteAsync(
                    _state.WorkspaceId,
                    _state.CollectionId,
                    candidate.SourceId,
                    "manual-accept",
                    effectiveReason,
                    candidate.Confidence,
                    cancellationToken,
                    effectiveReviewer).ConfigureAwait(false);

                var targetLayerName = record.TargetLayer.ToString();
                detail.AppendLine($"记忆条目已晋升：{record.SourceMemoryId} → {targetLayerName} 层");
                detail.AppendLine($"审计记录：{record.Id}");
            }
            catch (Exception ex)
            {
                detail.AppendLine($"记忆晋升失败（候选状态已更新）：{ex.Message}");
            }
        }
        else
        {
            // 无已有记忆条目（来源为 context / external）：写入工作记忆
            var now = DateTimeOffset.UtcNow;
            var newItemId = $"mem:promoted-{candidateId}";
            var newItem = new WorkingMemoryItem
            {
                Id = newItemId,
                WorkspaceId = _state.WorkspaceId,
                CollectionId = _state.CollectionId,
                Type = candidate.Category.Length > 0 ? candidate.Category : "promoted",
                Content = candidate.Content,
                Tags = candidate.MatchedRules.Take(5).ToList(),
                SourceRefs = candidate.SourceRefs,
                Importance = candidate.Confidence,
                Confidence = candidate.Confidence,
                Metadata = new Dictionary<string, string>
                {
                    ["promotionCandidateId"] = candidateId,
                    ["promotedBy"] = effectiveReviewer,
                    ["promotedAt"] = now.ToString("O"),
                    ["promotionReason"] = effectiveReason,
                    ["sourceKind"] = candidate.SourceKind,
                },
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _state.WorkingMemory!.AddAsync(newItem, cancellationToken).ConfigureAwait(false);
            detail.AppendLine($"已写入工作记忆：{newItemId}");
            detail.AppendLine($"来源类型：{candidate.SourceKind}");
        }

        return (updated, detail.ToString().TrimEnd());
    }

    public Task<IReadOnlyList<WorkingMemoryItem>> GetRecentWorkingMemoryAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory!.GetRecentAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            take,
            cancellationToken);
    }

    public Task ClearWorkingMemoryAsync(CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory!.ClearAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<WorkingMemoryActiveContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory!.GetActiveContextAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<WorkingMemoryActiveContext> SetActiveContextAsync(
        WorkingMemoryActiveContext activeContext,
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory!.SetActiveContextAsync(activeContext, cancellationToken);
    }

    public Task<WorkingMemoryCurrentTask?> GetCurrentTaskAsync(
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory!.GetCurrentTaskAsync(
            _state.WorkspaceId,
            _state.CollectionId,
            cancellationToken);
    }

    public Task<WorkingMemoryCurrentTask> SetCurrentTaskAsync(
        WorkingMemoryCurrentTask currentTask,
        CancellationToken cancellationToken = default)
    {
        return _state.WorkingMemory!.SetCurrentTaskAsync(currentTask, cancellationToken);
    }

    public async Task<IReadOnlyList<IndexSearchResult>> SearchIndexAsync(
        string keyword,
        CancellationToken cancellationToken = default)
    {
        var entries = await _state.Index!.SearchAsync(new IndexQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Key = keyword,
            Take = 50
        }, cancellationToken).ConfigureAwait(false);

        var results = new List<IndexSearchResult>();
        foreach (var entry in entries)
        {
            var items = new List<ContextItem>();
            foreach (var contextRef in entry.ContextRefs)
            {
                var item = await _state.ContextStore!.GetAsync(
                    _state.WorkspaceId,
                    _state.CollectionId,
                    contextRef,
                    cancellationToken).ConfigureAwait(false);

                if (item is not null)
                {
                    items.Add(item);
                }
            }

            results.Add(new IndexSearchResult { Entry = entry, Items = items });
        }

        return results;
    }

    private Task<IReadOnlyList<ContextItem>> QueryRawAsync(int take, CancellationToken cancellationToken)
    {
        return _state.ContextStore!.QueryAsync(new ContextQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = take,
            IncludeContent = true
        }, cancellationToken);
    }

    private async Task<IReadOnlyList<PackageCandidateItem>> GetPackageCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<PackageCandidateItem>();
        var rawItems = await QueryRawAsync(200, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(rawItems.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = "raw",
            Type = item.Type,
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        var memories = await QueryMemoryAsync(null, null, 200, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(memories.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = item.Layer.ToString(),
            Type = item.Type,
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        var constraints = await QueryConstraintsAsync(null, 200, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(constraints.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = item.Level.ToString(),
            Type = "constraint",
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        var globals = await _state.GlobalContextStore!.QueryAsync(new ContextGlobalQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Take = 200
        }, cancellationToken).ConfigureAwait(false);
        candidates.AddRange(globals.Select(item => new PackageCandidateItem
        {
            Id = item.Id,
            Kind = "global",
            Type = item.Type,
            SourceRefs = item.SourceRefs.Count > 0 ? item.SourceRefs : new[] { item.Id },
            EstimatedTokens = _state.TokenizerResolver.Estimate(item.Content).TokenCount
        }));

        return candidates;
    }

    private Task<IReadOnlyList<ContextMemoryItem>> QueryMemoryAsync(
        ContextMemoryLayer? layer,
        ContextMemoryStatus? status,
        int take,
        CancellationToken cancellationToken)
    {
        return _state.MemoryStore!.QueryAsync(new ContextMemoryQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Layer = layer,
            Status = status,
            Take = take
        }, cancellationToken);
    }

    private Task<IReadOnlyList<ContextConstraint>> QueryConstraintsAsync(
        ConstraintLevel? level,
        int take,
        CancellationToken cancellationToken)
    {
        return _state.ConstraintStore!.QueryAsync(new ContextConstraintQuery
        {
            WorkspaceId = _state.WorkspaceId,
            CollectionId = _state.CollectionId,
            Level = level,
            Take = take
        }, cancellationToken);
    }

    private static ContextMemoryLayer? ParseMemoryLayer(string layer)
    {
        if (layer is "candidate")
        {
            return null;
        }

        return Enum.TryParse<ContextMemoryLayer>(layer, ignoreCase: true, out var parsed)
            ? parsed
            : ContextMemoryLayer.Working;
    }

    private static ContextMemoryStatus? ParseMemoryStatus(string? status)
    {
        if (Enum.TryParse<ContextMemoryStatus>(status, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static VectorQueryExpansionShadowResult? SelectBestQueryExpansionResult(
        VectorQueryExpansionShadowReport? a3,
        VectorQueryExpansionShadowReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorQueryExpansionShadowResult>()
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.RiskAfterPolicy)
            .ThenBy(item => item.MustNotHitRiskAfterPolicy)
            .ThenBy(item => item.LifecycleRiskAfterPolicy)
            .ThenBy(item => item.NewRiskCount)
            .ThenByDescending(item => item.RecallAfterExpansion)
            .ThenByDescending(item => item.MrrAfterExpansion)
            .FirstOrDefault();
    }

    private static VectorRepresentationBenchmarkResult? SelectBestRepresentationResult(
        VectorRepresentationBenchmarkReport? a3,
        VectorRepresentationBenchmarkReport? extended)
    {
        return new[] { a3?.BestResult, extended?.BestResult }
            .Where(item => item is not null)
            .Cast<VectorRepresentationBenchmarkResult>()
            .OrderByDescending(item => item.Recommendation == VectorQueryShadowRecommendations.ReadyForRetrievalShadow)
            .ThenBy(item => item.RiskAfterPolicy)
            .ThenBy(item => item.MustNotHitRisk)
            .ThenBy(item => item.LifecycleRisk)
            .ThenByDescending(item => item.Recall)
            .ThenByDescending(item => item.Mrr)
            .FirstOrDefault();
    }

    private static string Preview(string? content, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "";
        }

        var normalized = content.ReplaceLineEndings(" ");
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private static ControlRoomDetail DetailFromRaw(
        ContextItem item,
        IReadOnlyList<ContextRelation> relations)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextItem {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "raw",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["type"] = item.Type,
                ["format"] = item.ContentFormat.ToString(),
                ["importance"] = item.Importance.ToString("0.00"),
                ["version"] = item.Version.ToString(),
                ["created"] = item.CreatedAt.ToString("u"),
                ["updated"] = item.UpdatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            Tags = item.Tags,
            SourceRefs = item.SourceRefs,
            Relations = relations,
            Content = item.Content
        };
    }

    private static ControlRoomDetail DetailFromMemory(
        ContextMemoryItem item,
        IReadOnlyList<ContextRelation> relations)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextMemoryItem {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "memory",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["layer"] = item.Layer.ToString(),
                ["status"] = item.Status.ToString(),
                ["type"] = item.Type,
                ["format"] = item.ContentFormat.ToString(),
                ["importance"] = item.Importance.ToString("0.00"),
                ["confidence"] = item.Confidence.ToString("0.00"),
                ["version"] = item.Version.ToString(),
                ["created"] = item.CreatedAt.ToString("u"),
                ["updated"] = item.UpdatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            Tags = item.Tags,
            SourceRefs = item.SourceRefs,
            Relations = relations,
            Content = item.Content
        };
    }

    private static ControlRoomDetail DetailFromConstraint(ContextConstraint item)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextConstraint {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "constraint",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId ?? "",
                ["scope"] = item.Scope.ToString(),
                ["level"] = item.Level.ToString(),
                ["status"] = item.Status.ToString(),
                ["confidence"] = item.Confidence.ToString("0.00"),
                ["created"] = item.CreatedAt.ToString("u"),
                ["updated"] = item.UpdatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            Tags = item.AppliesToRefs,
            SourceRefs = item.SourceRefs,
            Content = item.Content
        };
    }

    private static ControlRoomDetail DetailFromRelation(ContextRelation item)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextRelation {item.Id}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "relation",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["source"] = item.SourceId,
                ["target"] = item.TargetId,
                ["type"] = item.RelationType,
                ["weight"] = item.Weight.ToString("0.00"),
                ["confidence"] = item.Confidence.ToString("0.00"),
                ["created"] = item.CreatedAt.ToString("u")
            },
            Metadata = item.Metadata,
            SourceRefs = item.SourceRefs,
            Content = $"{item.SourceId} --{item.RelationType}--> {item.TargetId}"
        };
    }

    private static ControlRoomDetail DetailFromJob(ContextJob item)
    {
        return new ControlRoomDetail
        {
            Title = $"ContextJob {item.JobId}",
            Fields = new Dictionary<string, string>
            {
                ["kind"] = "job",
                ["workspace"] = item.WorkspaceId,
                ["collection"] = item.CollectionId,
                ["jobKind"] = item.Kind.ToString(),
                ["state"] = item.State.ToString(),
                ["priority"] = item.Priority.ToString(),
                ["retry"] = $"{item.RetryCount}/{item.MaxRetryCount}",
                ["created"] = item.CreatedAt.ToString("u"),
                ["started"] = item.StartedAt?.ToString("u") ?? "",
                ["completed"] = item.CompletedAt?.ToString("u") ?? "",
                ["error"] = item.ErrorMessage ?? ""
            },
            Content = item.PayloadJson
        };
    }
}
