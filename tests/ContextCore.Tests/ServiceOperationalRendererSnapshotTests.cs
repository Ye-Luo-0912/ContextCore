using System.Text;
using ContextCore.Abstractions;
using ContextCore.Abstractions.Models;
using ContextCore.ControlRoom.Rendering;
using ContextCore.ControlRoom.Services;

namespace ContextCore.Tests;

/// <summary>ServiceOperationalRenderer 各渲染方法的快照测试，确保重构后输出 byte-exact 一致。</summary>
[TestClass]
[TestCategory("Rendering")]
public sealed class ServiceOperationalRendererSnapshotTests
{
    private static readonly DateTimeOffset SnapshotTime =
        new DateTimeOffset(2024, 1, 15, 10, 30, 0, TimeSpan.Zero);

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").Replace("\r", "\n");

    [TestMethod]
    public void RenderJobs_WithEmptyJobs_RendersHeaderAndSummary()
    {
        var snapshot = new ServiceJobsSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/",
            Jobs = Array.Empty<ContextJob>()
        };

        var rendered = ServiceOperationalRenderer.RenderJobs(snapshot);

        var expected = @"Service Jobs
============
时间   : 2024-01-15 10:30:00
服务   : http://localhost:5079/
作业数 : 0

";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderCandidateMemoryReviewResult_WithWarnings_RendersReviewResult()
    {
        var result = new CandidateMemoryReviewResult
        {
            OperationId = "op-cmr-001",
            CandidateId = "candidate-001",
            CandidateKind = "Memory",
            Action = "Reject",
            FromStatus = ContextMemoryStatus.Candidate,
            ToStatus = ContextMemoryStatus.Rejected,
            ReviewId = "cmr-001",
            Reviewer = "reviewer-alice",
            Reason = "insufficient evidence",
            ReviewedAt = SnapshotTime,
            SupersedeTargetCandidateId = null,
            Warnings = new[] { "warning-1" },
            Errors = Array.Empty<string>()
        };

        var rendered = ServiceOperationalRenderer.RenderCandidateMemoryReviewResult(result);

        var expected = @"Candidate Memory Review Result
==============================
OperationId : op-cmr-001
CandidateId : candidate-001
Kind        : Memory
Action      : Reject
Status      : Candidate -> Rejected
ReviewId    : cmr-001
Reviewer    : reviewer-alice
Reason      : insufficient evidence
ReviewedAt  : 2024-01-15 10:30:00
Supersedes  : -
Warnings
- warning-1
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderConstraintGapReviewResult_WithErrors_RendersReviewResult()
    {
        var response = new ConstraintGapReviewResult
        {
            OperationId = "op-cgr-001",
            GapId = "gap-001",
            Action = "accept",
            Status = "Accepted",
            ReviewId = "cgr-001",
            Reviewer = "reviewer-bob",
            Reason = "constraint confirmed",
            ReviewedAt = SnapshotTime,
            CreatedConstraintId = "constraint-001",
            TargetItemId = null,
            TargetItemKind = "constraint",
            TargetLayer = "CandidateConstraint",
            Warnings = Array.Empty<string>(),
            Errors = new[] { "error-1" }
        };

        var rendered = ServiceOperationalRenderer.RenderConstraintGapReviewResult(response);

        var expected = @"Constraint Gap Review Result
============================
OperationId         : op-cgr-001
GapId               : gap-001
Action              : accept
Status              : Accepted
ReviewId            : cgr-001
Reviewer            : reviewer-bob
Reason              : constraint confirmed
ReviewedAt          : 2024-01-15 10:30:00
CreatedConstraintId : constraint-001
TargetKind          : constraint
TargetLayer         : CandidateConstraint
Errors
- error-1
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderStableMemory_WithEmptySnapshot_RendersHeaderAndZeroCounts()
    {
        var snapshot = new ServiceStableMemorySnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/",
            Snapshot = new StableMemorySnapshot
            {
                WorkspaceId = "ws-1",
                CollectionId = "col-1"
            },
            Diagnostics = new StableMemoryDiagnosticsReport
            {
                WorkspaceId = "ws-1",
                CollectionId = "col-1"
            }
        };

        var rendered = ServiceOperationalRenderer.RenderStableMemory(snapshot);

        var expected = @"Service Stable Memory
=====================
时间       : 2024-01-15 10:30:00
服务       : http://localhost:5079/
Workspace  : ws-1
Collection : col-1

Snapshot
- StableMemoryCount        : 0
- StableConstraintCount    : 0
- DecisionRecordCount      : 0
- GlobalMemoryCount        : 0
- ActiveCount              : 0
- SupersededCount          : 0
- DeprecatedCount          : 0
- RejectedCount            : 0
- MissingProvenanceCount   : 0
- DuplicateCandidateCount  : 0
- ConflictCandidateCount   : 0
- WeakEvidenceCount        : 0

Recent Stable Items

Diagnostics
- Total                         : 0
- DuplicateStableMemory         : 0
- PossibleConflict              : 0
- MissingProvenance             : 0
- MissingEvidenceRefs           : 0
- StableWithoutReviewSource     : 0
- StableConstraintWithoutScope  : 0
- DecisionRecordWithoutSource   : 0
- DeprecatedStillActive         : 0
- SupersededWithoutReplacement  : 0
- GlobalMemoryScopeRisk         : 0
- SupersededWithoutRelation     : 0
- MetadataRelationMismatch      : 0
- BrokenReplacementLink         : 0
- ReplacementTargetMissing      : 0
- ReplacementTargetInactive     : 0
- ReplacementCycle              : 0
- MultipleActiveReplacements    : 0
- ScopeMismatchInReplacement    : 0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderPolicy_WithDefaultPolicy_RendersPolicyPage()
    {
        var snapshot = new ServicePolicySnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/",
            Policies = Array.Empty<ContextPackagePolicy>(),
            DefaultPolicy = new ContextPackagePolicy
            {
                Name = "default",
                TokenBudget = 1200,
                SectionPriorities = new Dictionary<string, int>()
            },
            ProviderCapabilities = new[]
            {
                new ProviderCapabilityResponse
                {
                    Name = "filesystem",
                    State = "AlphaSupported",
                    Active = true
                }
            },
            LifecycleNotes = new[] { "note-1" }
        };

        var rendered = ServiceOperationalRenderer.RenderPolicy(snapshot);

        var expected = @"Service Policy
==============
PersistedPolicies : 0
DefaultPolicy     : default
TokenBudget       : 1200
SectionPriorities : (default)
LifecyclePolicy
- note-1
ProviderCapabilities
- filesystem [AlphaSupported] active=yes
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderModel_WithEmptySnapshot_RendersHeaderProvidersAndRoutes()
    {
        var snapshot = new ServiceModelSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderModel(snapshot);

        var expected = @"Service Model Status
====================
时间    : 2024-01-15 10:30:00
服务    : http://localhost:5079/

Providers

Routes
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderAdminRuntime_WithEmptySnapshot_RendersHeaderAndDefaults()
    {
        var snapshot = new ServiceAdminRuntimeSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderAdminRuntime(snapshot);

        var expected = @"Service Admin / Runtime
=======================
时间          : 2024-01-15 10:30:00
服务          : http://localhost:5079/
RuntimeStatus : /
Storage       : 
RootPath      : 未返回
Retrieval     : 
BackupRoot    : 无
BackupExists  : 
BackupHealthy : False
BackupMessage : 无

File Layout Status
DataRoot      : 
Categories    : 0
ManifestCount : 0
ReportCount   : 0

Memory Layout Status
DataRoot      : 
ShortTerm     : 0
Candidate     : 0
Stable        : 0
TemporalReady : False
LegacyFallback: 0
MissingDirs   : 0

Trace Layout Status
TraceRoot     : 
Retrieval     : 0
ToolCallReady : False
LegacyFallback: 0

Report Layout Status
DataRoot       : 
ManifestCount  : 0
LatestReports  : 0
LegacyMirrored : 0
MissingStandard: 0
MissingLegacy  : 0
DuplicateHash  : 0

Storage Boundary Status
ArtifactKinds : 0
ArtifactOnly  : 0
Operational   : 0
IndexState    : 0
DbRecommended : 0
FsPreferred   : 0
Migrations    : 0
HighPriority  : 0

Postgres Operational Store Status
Enabled       : False
ProviderId    : 
Status        : 
Connection    : False
SchemaVersion : 未应用
Pending       : 0
TableCount    : 0
MissingTables : 0
Capability    : 
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderLearning_WithEmptySnapshot_RendersHeaderAndEmptySections()
    {
        var snapshot = new ServiceLearningSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderLearning(snapshot);

        var expected = @"Service Context Learning
========================
时间     : 2024-01-15 10:30:00
服务     : http://localhost:5079/
Feedback : 0
Records  : 0
Cases    : 0
Signals  : positive=0 negative=0 stale=0

Failure Types
- (empty)

Case Kinds
- (empty)

Active Regression Cases
- (empty)

Promotion Feedback Signals
- (empty)

Recent Feedback
- (empty)

Learning Cases
- (empty)
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderMemory_WithEmptySnapshot_RendersHeaderAndLayoutStatus()
    {
        var snapshot = new ServiceMemorySnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderMemory(snapshot);

        var expected = @"Service Memory
==============
时间    : 2024-01-15 10:30:00
服务    : http://localhost:5079/
Working : 0
Candidate: 0
Stable  : 0
Global  : 0

Memory Layout Status
DataRoot      : 
ShortTerm     : 0
Candidate     : 0
Stable        : 0
TemporalReady : False
LegacyFallback: 0
MissingDirs   : 0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderConstraints_WithEmptySnapshot_RendersHeaderAndCount()
    {
        var snapshot = new ServiceConstraintsSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderConstraints(snapshot);

        var expected = @"Service Constraints
===================
Count: 0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderConstraintGaps_WithEmptySnapshot_RendersHeaderAndFilter()
    {
        var snapshot = new ServiceConstraintGapsSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderConstraintGaps(snapshot);

        var expected = @"Service Constraint Gaps
=======================
时间    : 2024-01-15 10:30:00
服务    : http://localhost:5079/
Count   : 0
Filter  : status=- severity=- limit=20 offset=0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderCandidateConstraints_WithEmptySnapshot_RendersHeaderAndFilter()
    {
        var snapshot = new ServiceCandidateConstraintsSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderCandidateConstraints(snapshot);

        var expected = @"Service Candidate Constraints
=============================
时间    : 2024-01-15 10:30:00
服务    : http://localhost:5079/
Count   : 0
Filter  : status=Candidate limit=20 offset=0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderRelations_WithEmptySnapshot_RendersHeaderAndDiagnostics()
    {
        var snapshot = new ServiceRelationsSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderRelations(snapshot);

        var expected = @"Service Relations
=================
时间       : 2024-01-15 10:30:00
服务       : http://localhost:5079/

Relation Types
Count: 0

Global Relation Diagnostics
Relations=0 Diagnostics=0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderShortTermMemory_WithEmptySnapshot_RendersHeaderAndEmbeddedSections()
    {
        var snapshot = new ServiceShortTermMemorySnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderShortTermMemory(snapshot);

        var expected = @"Service Short-Term Memory
=========================
RawEventCount    : 0
WorkingItemCount : 0
ActiveTasks      : 0
RecentDecisions  : 0
OpenQuestions    : 0
KnownIssues      : 0
RecentWarnings   : 0
Maintenance
- (unavailable)
ActiveTasks
- (empty)
RecentDecisions
- (empty)
OpenQuestions
- (empty)
KnownIssues
- (empty)
RecentWarnings
- (empty)
LatestRawEvents

Short-Term Archive Summary
==========================
Scope                   : /- session=-
ArchivedRawEvents       : 0
ArchivedWorkingItems    : 0
ArchivedResolvedItems   : 0
ArchivedActiveTasks     : 0
ArchivedDecisions       : 0
ArchivedOpenQuestions   : 0
ArchivedKnownIssues     : 0
ArchivedRecentWarnings  : 0
LatestArchivedAt        : -


Short-Term Archive Items
========================
ArchivedRawCount        : 0
ArchivedWorkingCount    : 0


Short-Term Compaction Runs
==========================
(empty)

";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderStableReviewCandidates_WithEmptySnapshot_RendersHeaderFiltersAndEmpty()
    {
        var snapshot = new ServiceStableReviewCandidatesSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderStableReviewCandidates(snapshot);

        var expected = @"Service Stable Review Candidates
================================
时间        : 2024-01-15 10:30:00
服务        : http://localhost:5079/
Candidates  : 0
Filters     : status=- validation=- kind=- target=- limit=20 offset=0
(empty)
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderCandidateMemory_WithEmptySnapshot_RendersHeaderSnapshotAndDiagnostics()
    {
        var snapshot = new ServiceCandidateMemorySnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/",
            Snapshot = new CandidateMemorySnapshot
            {
                WorkspaceId = "ws-1",
                CollectionId = "col-1"
            },
            Diagnostics = new CandidateMemoryDiagnosticsReport
            {
                WorkspaceId = "ws-1",
                CollectionId = "col-1"
            }
        };

        var rendered = ServiceOperationalRenderer.RenderCandidateMemory(snapshot);

        var expected = @"Service Candidate Memory
========================
时间       : 2024-01-15 10:30:00
服务       : http://localhost:5079/
Workspace  : ws-1
Collection : col-1

Snapshot
- CandidateMemoryCount        : 0
- CandidateConstraintCount    : 0
- CandidateDecisionCount      : 0
- PendingReviewCount          : 0
- AcceptedFromPromotionCount  : 0
- ExpiredCandidateCount       : 0
- DuplicateCandidateCount     : 0
- ConflictCandidateCount      : 0

Recent Candidates

Diagnostics
- Total                 : 0
- Duplicate             : 0
- Stale                 : 0
- WithoutEvidence       : 0
- RejectedSource        : 0
- StableConflict        : 0
- Superseded            : 0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderVectorIndex_WithEmptySnapshot_RendersHeaderAndDefaults()
    {
        var snapshot = new ServiceVectorIndexSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderVectorIndex(snapshot);

        var expected = @"Service Vector Index
====================
时间       : 2024-01-15 10:30:00
服务       : http://localhost:5079/
Workspace  : 
Collection : 
Provider   : -
Model      : -
Dimension  : 0
Available  : store=no generator=no
Counts     : indexed=0 stale=0 missing=0 duplicate=0 orphan=0

Coverage Summary
- source items : 0
- indexed      : 0
- coverage     : 0.00%
- missing      : 0
- stale        : 0
- duplicate    : 0
- orphan       : 0
- recommendation: NeedsInitialIndexing


Diagnostics
- total          : 0
- dimensionMismatch: 0
- unsupportedModel : 0
- providerUnavailable: 0

Recent Diagnostics
- (empty)

Reindex Preview
- sources : 0
- create  : 0
- update  : 0
- current : 0
- orphan  : 0

Actions
- P Reindex Plan
- A Apply Reindex (requires YES)
- R Reindex Reports
- Q Query Preview
- D Diagnostics
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderPromotionCandidates_WithEmptySnapshot_RendersHeaderFiltersAndEmpty()
    {
        var snapshot = new ServicePromotionCandidatesSnapshot
        {
            CurrentTime = SnapshotTime,
            BaseUrl = "http://localhost:5079/"
        };

        var rendered = ServiceOperationalRenderer.RenderPromotionCandidates(snapshot);

        var expected = @"Service Promotion Candidates
============================
时间        : 2024-01-15 10:30:00
服务        : http://localhost:5079/
Candidates  : 0
Filters     : status=- kind=- target=- minConf=- minImp=- limit=20 offset=0
(empty)
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderProvenance_WithMinimalResponse_RendersHeaderAndCounts()
    {
        var provenance = new ContextProvenanceResponse
        {
            ItemId = "item-1"
        };

        var rendered = ServiceOperationalRenderer.RenderProvenance(provenance);

        var expected = @"Service Provenance
==================
ItemId     : item-1
TargetKind : -
EvidenceRefs : -
StableReviews: 0
PromotionReviews: 0
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderShortTermCompactionResult_WithMinimalResult_RendersHeaderAndCounts()
    {
        var result = new ShortTermMemoryCompactionResult
        {
            WorkspaceId = "ws-1",
            CollectionId = "col-1",
            CompletedAt = SnapshotTime
        };

        var rendered = ServiceOperationalRenderer.RenderShortTermCompactionResult(result);

        var expected = @"Short-Term Compaction Result
============================
Scope                  : ws-1/col-1 session=-
ActiveRawEvents        : 0 -> 0
ActiveWorkingItems     : 0 -> 0
MergedWorkingItems     : 0
MergedByWorkingKey     : 0
MergedByTitle          : 0
ArchivedRawEvents      : 0
ArchivedWorkingItems   : 0
ArchivedResolvedItems  : 0
EvidenceRefsTrimmed    : 0
CompletedAt            : 2024-01-15 10:30:00
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderCandidateConstraintReviewResult_WithMinimalData_RendersReviewResult()
    {
        var response = new CandidateConstraintReviewResult
        {
            OperationId = "op-ccr-001",
            ConstraintId = "constraint-001",
            Action = "Accept",
            Status = ContextMemoryStatus.Active,
            ReviewId = "ccr-001",
            Reviewer = "reviewer-carol",
            Reason = "constraint validated",
            ReviewedAt = SnapshotTime,
            ActivatedConstraintId = "constraint-001",
            TargetLayer = "Stable",
            Warnings = Array.Empty<string>(),
            Errors = Array.Empty<string>()
        };

        var rendered = ServiceOperationalRenderer.RenderCandidateConstraintReviewResult(response);

        var expected = @"Candidate Constraint Review Result
==================================
OperationId           : op-ccr-001
ConstraintId          : constraint-001
Action                : Accept
Status                : Active
ReviewId              : ccr-001
Reviewer              : reviewer-carol
Reason                : constraint validated
ReviewedAt            : 2024-01-15 10:30:00
ActivatedConstraintId : constraint-001
TargetLayer           : Stable
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderPromotionCandidateReviewResult_WithMinimalData_RendersReviewResult()
    {
        var response = new PromotionCandidateReviewResult
        {
            OperationId = "op-pcr-001",
            CandidateId = "candidate-001",
            Action = "Accept",
            Status = PromotionCandidateStatus.Accepted,
            ReviewId = "pcr-001",
            Reviewer = "reviewer-dave",
            Reason = "promotion accepted",
            ReviewedAt = SnapshotTime,
            CreatedTargetItemId = "target-001",
            TargetItemKind = "Memory",
            TargetLayer = "Stable",
            Warnings = Array.Empty<string>(),
            Errors = Array.Empty<string>()
        };

        var rendered = ServiceOperationalRenderer.RenderPromotionCandidateReviewResult(response);

        var expected = @"Promotion Candidate Review Result
=================================
OperationId : op-pcr-001
CandidateId : candidate-001
Action      : Accept
Status      : Accepted
ReviewId    : pcr-001
Reviewer    : reviewer-dave
Reason      : promotion accepted
ReviewedAt  : 2024-01-15 10:30:00
TargetId    : target-001
TargetKind  : Memory
TargetLayer : Stable
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderRelationReviewResult_WithMinimalData_RendersReviewResult()
    {
        var result = new RelationReviewResult
        {
            OperationId = "op-rrr-001",
            RelationId = "relation-001",
            Action = "Review",
            FromLifecycle = "active",
            ToLifecycle = "deprecated",
            FromReviewStatus = "Pending",
            ToReviewStatus = "Reviewed",
            Reviewer = "reviewer-eve",
            Reason = "relation deprecated",
            ReviewedAt = SnapshotTime,
            Relation = new ContextRelation
            {
                SourceId = "src-1",
                TargetId = "tgt-1",
                RelationType = "references"
            },
            Warnings = Array.Empty<string>(),
            Errors = Array.Empty<string>()
        };

        var rendered = ServiceOperationalRenderer.RenderRelationReviewResult(result);

        var expected = @"Service Relation Review Result
==============================
Operation  : op-rrr-001
RelationId : relation-001
Action     : Review
Lifecycle  : active -> deprecated
Review     : Pending -> Reviewed
Reviewer   : reviewer-eve
Reason     : relation deprecated
ReviewedAt : 2024-01-15 10:30:00
Relation   : src-1 --references--> tgt-1
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderStableLifecycleReviewResult_WithMinimalData_RendersReviewResult()
    {
        var result = new StableLifecycleReviewResult
        {
            OperationId = "op-slr-001",
            StableItemId = "stable-001",
            StableKind = "Memory",
            Action = "Deprecate",
            FromStatus = ContextMemoryStatus.Stable,
            ToStatus = ContextMemoryStatus.Deprecated,
            FromLifecycle = "Current",
            ToLifecycle = "Deprecated",
            ReviewId = "slr-001",
            Reviewer = "reviewer-frank",
            Reason = "stable deprecated",
            ReviewedAt = SnapshotTime,
            Warnings = Array.Empty<string>(),
            Errors = Array.Empty<string>()
        };

        var rendered = ServiceOperationalRenderer.RenderStableLifecycleReviewResult(result);

        var expected = @"Stable Lifecycle Review Result
==============================
OperationId : op-slr-001
StableItem  : stable-001
Kind        : Memory
Action      : Deprecate
Status      : Stable -> Deprecated
Lifecycle   : Current -> Deprecated
ReviewId    : slr-001
Reviewer    : reviewer-frank
Reason      : stable deprecated
Replacement : -
ReviewedAt  : 2024-01-15 10:30:00
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }

    [TestMethod]
    public void RenderStableReviewDecisionResult_WithMinimalData_RendersDecisionResult()
    {
        var response = new StableReviewDecisionResult
        {
            OperationId = "op-srd-001",
            StableReviewCandidateId = "src-001",
            Action = "Accept",
            Status = "Accepted",
            ReviewId = "srd-001",
            Reviewer = "reviewer-grace",
            Reason = "stable review accepted",
            ReviewedAt = SnapshotTime,
            CreatedStableTargetItemId = "stable-target-001",
            StableTargetItemKind = "Memory",
            TargetLayer = "Stable",
            ValidationStatus = "ReadyForReview",
            Warnings = Array.Empty<string>(),
            Errors = Array.Empty<string>()
        };

        var rendered = ServiceOperationalRenderer.RenderStableReviewDecisionResult(response);

        var expected = @"Stable Review Decision Result
=============================
OperationId             : op-srd-001
StableReviewCandidateId : src-001
Action                  : Accept
Status                  : Accepted
ValidationStatus        : ReadyForReview
ReviewId                : srd-001
Reviewer                : reviewer-grace
Reason                  : stable review accepted
ReviewedAt              : 2024-01-15 10:30:00
StableTargetId          : stable-target-001
StableTargetKind        : Memory
TargetLayer             : Stable
";
        Assert.AreEqual(Normalize(expected), Normalize(rendered));
    }
}
