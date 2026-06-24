# Vector Retrieval Dataset Alignment Audit Summary

Generated: 2026-06-15T08:18:32.2940631+00:00
- Recommendation: `KeepPreviewOnly`
- AlignmentIssueCount: `50`
- FormalRetrievalAllowed: `False`
- UseForRuntime: `False`

| Dataset | Samples | MustHit | Corpus Coverage | Provider Scope | Eligibility Blocks | Query Tokens | Token Overlap | Anchor Coverage | SourceKind Coverage | Issues | Recommendation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| A3 | 50 | 66 | 100.00% | 100.00% | 25 | 100.00% | 72.00% | 100.00% | 100.00% | 25 | KeepPreviewOnly |
| Extended | 113 | 160 | 100.00% | 100.00% | 25 | 100.00% | 79.96% | 100.00% | 100.00% | 25 | KeepPreviewOnly |

### Issue Breakdown

| Issue | Count |
|---|---:|
| MustHitLifecycleFiltered | 50 |

## A3

- ProviderId: `deterministic-hash`
- EmbeddingModel: `deterministic-hash-v1`
- Dimension: `16`
- SampleCount: `50`
- QueryCount: `50`
- MustHitCount: `66`
- MustNotCount: `57`
- MustHitPresentInCorpusCount: `66`
- MustHitMissingFromCorpusCount: `0`
- MustHitPresentInProviderScopeCount: `66`
- MustHitBlockedByEligibilityCount: `25`
- QueryTokenCoverageAverage: `100.00%`
- QueryCorpusTokenOverlapAverage: `72.00%`
- AnchorCoverageRate: `100.00%`
- SourceKindCoverageRate: `100.00%`
- CorpusEntryCount: `474`
- ProviderScopedEntryCount: `158`
- AlignmentIssueCount: `25`
- Recommendation: `KeepPreviewOnly`

### Issue Breakdown

| Issue | Count |
|---|---:|
| MustHitLifecycleFiltered | 25 |

| Issue | Sample | MustHit | QueryOverlap | SourceKind | ItemKind | Tags | Notes |
|---|---|---|---|---|---|---|---|
| MustHitLifecycleFiltered | automation-sample-001 | doc:automation-guide | 一,作,作流,到,到错,工,工作,执 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-002 | memory:automation-noise-keyword | 上,上周,作,作废,作流,周,工,工作 | memory | run-log | deprecated,error,log | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-003 | doc:automation-guide | 作,作流,工,工作,执,执行,流,流执 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-007 | doc:automation-guide | 作,作流,工,工作,流 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-009 | memory:automation-stopped-cron | 任,任务,停,停止,务,动,定,定时 | memory | cron-log | cron,deprecated,log | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-010 | doc:automation-guide | 到,动,执,执行,确,确认,自,自动 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | chat-sample-009 | memory:chat-deprecated-draft | 之,之前,作,作废,决,前,前讨,否 | memory | draft | deprecated,draft | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-001 | doc:ipromotioncandidatestore | ipromotioncandidatestore,元,元测,单,单元,口,套,实 | context | interface | interface,promotion | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-002 | doc:coding-noise-keyword | ipromotioncandidatestore,口,口设,已,已废,废,废弃,弃 | context | documentation | deprecated,interface,legacy | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-003 | doc:ipromotioncandidatestore | ipromotioncandidatestore,元,元测,单,单元,口,和,套 | context | interface | interface,promotion | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-007 | doc:ipromotioncandidatestore | 包,测,测试,试 | context | interface | interface,promotion | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-009 | memory:coding-deprecated-logger | 不,不再,使,使用,再,再使,印,印适 | memory | documentation | deprecated,legacy,logging | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-001 | novel:character-linfeng | 丹,九,九转,大,林,林风,转,转金 | context | character | character,novel,protagonist,小说 | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-001 | novel:world-cangqiong | 丹,大,大陆,的,穹,穹大,苍,苍穹 | context | world-setting | setting,world | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-002 | memory:novel-plot-old-draft | 丹,丹的,九,九转,取,取时,大,大纲 | memory | plot | deprecated,plot | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-003 | memory:novel-plot-old-draft | 丹,九,九转,大,废,废弃,弃,转 | memory | plot | deprecated,plot | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-007 | novel:character-linfeng | 制,力,小,小说,说 | context | character | character,novel,protagonist,小说 | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-009 | memory:novel-plot-deprecated-villain | 减,删,删减,反,反派,噬,噬魂,大 | memory | plot | deprecated,plot,villain | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-010 | novel:character-linfeng | 主,主角,有,林,林风,角,角林,风 | context | character | character,novel,protagonist,小说 | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-001 | doc:local-alpha-runbook | contextcore,久,久化,化,化后,后,后端,在 | context | documentation | filesystem,runbook | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-002 | doc:postgres-not-ready | postgresql,为,产,产后,作,作为,储,储当 | context | documentation | experimental,postgres | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-003 | doc:local-alpha-runbook | 久,久化,化,地,地运,持,持久,据 | context | documentation | filesystem,runbook | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-003 | doc:postgres-not-ready | postgresql | context | documentation | experimental,postgres | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-009 | memory:project-deprecated-gateway | ocelot,于,关,基,基于,已,弃,弃的 | memory | runbook | deprecated,gateway,ocelot | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-010 | doc:local-alpha-runbook | contextcore,于,件,地,地运,有,本,本地 | context | documentation | filesystem,runbook | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |

## Extended

- ProviderId: `deterministic-hash`
- EmbeddingModel: `deterministic-hash-v1`
- Dimension: `16`
- SampleCount: `113`
- QueryCount: `113`
- MustHitCount: `160`
- MustNotCount: `122`
- MustHitPresentInCorpusCount: `160`
- MustHitMissingFromCorpusCount: `0`
- MustHitPresentInProviderScopeCount: `160`
- MustHitBlockedByEligibilityCount: `25`
- QueryTokenCoverageAverage: `100.00%`
- QueryCorpusTokenOverlapAverage: `79.96%`
- AnchorCoverageRate: `100.00%`
- SourceKindCoverageRate: `100.00%`
- CorpusEntryCount: `474`
- ProviderScopedEntryCount: `158`
- AlignmentIssueCount: `25`
- Recommendation: `KeepPreviewOnly`

### Issue Breakdown

| Issue | Count |
|---|---:|
| MustHitLifecycleFiltered | 25 |

| Issue | Sample | MustHit | QueryOverlap | SourceKind | ItemKind | Tags | Notes |
|---|---|---|---|---|---|---|---|
| MustHitLifecycleFiltered | automation-sample-001 | doc:automation-guide | 一,作,作流,到,到错,工,工作,执 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-002 | memory:automation-noise-keyword | 上,上周,作,作废,作流,周,工,工作 | memory | run-log | deprecated,error,log | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-003 | doc:automation-guide | 作,作流,工,工作,执,执行,流,流执 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-007 | doc:automation-guide | 作,作流,工,工作,流 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-009 | memory:automation-stopped-cron | 任,任务,停,停止,务,动,定,定时 | memory | cron-log | cron,deprecated,log | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | automation-sample-010 | doc:automation-guide | 到,动,执,执行,确,确认,自,自动 | context | guide | error-handling,guide | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | chat-sample-009 | memory:chat-deprecated-draft | 之,之前,作,作废,决,前,前讨,否 | memory | draft | deprecated,draft | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-001 | doc:ipromotioncandidatestore | ipromotioncandidatestore,元,元测,单,单元,口,套,实 | context | interface | interface,promotion | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-002 | doc:coding-noise-keyword | ipromotioncandidatestore,口,口设,已,已废,废,废弃,弃 | context | documentation | deprecated,interface,legacy | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-003 | doc:ipromotioncandidatestore | ipromotioncandidatestore,元,元测,单,单元,口,和,套 | context | interface | interface,promotion | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-007 | doc:ipromotioncandidatestore | 包,测,测试,试 | context | interface | interface,promotion | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | coding-sample-009 | memory:coding-deprecated-logger | 不,不再,使,使用,再,再使,印,印适 | memory | documentation | deprecated,legacy,logging | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-001 | novel:character-linfeng | 丹,九,九转,大,林,林风,转,转金 | context | character | character,novel,protagonist,小说 | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-001 | novel:world-cangqiong | 丹,大,大陆,的,穹,穹大,苍,苍穹 | context | world-setting | setting,world | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-002 | memory:novel-plot-old-draft | 丹,丹的,九,九转,取,取时,大,大纲 | memory | plot | deprecated,plot | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-003 | memory:novel-plot-old-draft | 丹,九,九转,大,废,废弃,弃,转 | memory | plot | deprecated,plot | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-007 | novel:character-linfeng | 制,力,小,小说,说 | context | character | character,novel,protagonist,小说 | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-009 | memory:novel-plot-deprecated-villain | 减,删,删减,反,反派,噬,噬魂,大 | memory | plot | deprecated,plot,villain | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | novel-sample-010 | novel:character-linfeng | 主,主角,有,林,林风,角,角林,风 | context | character | character,novel,protagonist,小说 | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-001 | doc:local-alpha-runbook | contextcore,久,久化,化,化后,后,后端,在 | context | documentation | filesystem,runbook | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-002 | doc:postgres-not-ready | postgresql,为,产,产后,作,作为,储,储当 | context | documentation | experimental,postgres | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-003 | doc:local-alpha-runbook | 久,久化,化,地,地运,持,持久,据 | context | documentation | filesystem,runbook | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-003 | doc:postgres-not-ready | postgresql | context | documentation | experimental,postgres | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-009 | memory:project-deprecated-gateway | ocelot,于,关,基,基于,已,弃,弃的 | memory | runbook | deprecated,gateway,ocelot | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
| MustHitLifecycleFiltered | project-sample-010 | doc:local-alpha-runbook | contextcore,于,件,地,地运,有,本,本地 | context | documentation | filesystem,runbook | mustHit 在 provider scope 中存在，但会被当前 eligibility/profile policy 阻断。 |
