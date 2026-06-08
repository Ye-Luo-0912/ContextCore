# Extended Eval Failure Triage Report

Generated: 2026-06-04 17:25:03 +00:00

## Summary

- Total samples: `113`
- Failed samples: `15`

### Category Counts

| Category | Count |
|---|---:|
| MissingUncertainty | 9 |
| BudgetDroppedImportantItem | 5 |
| ConstraintMiss | 5 |
| MustHitSelectedButTooLow | 5 |
| TooManyLowValueSelected | 5 |
| EntityMiss | 4 |
| MissingMustHit | 3 |

### Mode Counts

| Mode | Failed |
|---|---:|
| AutomationMode | 5 |
| ChatMode | 4 |
| CodingMode | 2 |
| NovelMode | 4 |

## Failed Sample Fix Plan

| Sample | Failure Type | Suspected Root Cause | Fix Type | Expected Regression Test |
|---|---|---|---|---|
| automation-20260529-003 | MissingUncertainty | Expected uncertainty is not mapped from uncertainty, diagnostic, evidence, historical, excluded, or risk surfaces. | uncertainty mapping | Extended sample automation-20260529-003 should satisfy expected uncertainty aliases. |
| automation-20260529-004 | MissingUncertainty | Expected uncertainty is not mapped from uncertainty, diagnostic, evidence, historical, excluded, or risk surfaces. | uncertainty mapping | Extended sample automation-20260529-004 should satisfy expected uncertainty aliases. |
| automation-20260529-008 | EntityMiss | Expected entity is not represented in the selected package text. | uncertainty mapping | Extended sample automation-20260529-008 should satisfy expected uncertainty aliases. |
| automation-20260529-010 | EntityMiss | Expected entity is not represented in the selected package text. | uncertainty mapping | Extended sample automation-20260529-010 should satisfy expected uncertainty aliases. |
| automation-sample-001 | MissingMustHit | Must-hit evidence is selected but ranked below the top-10 package quality gate. | section priority | Extended sample automation-sample-001 should rank selected must-hit evidence inside top 10. |
| chat-20260529-003 | ConstraintMiss | Expected constraint text is not represented in constraints/package sections. | uncertainty mapping | Extended sample chat-20260529-003 should satisfy expected uncertainty aliases. |
| chat-sample-003 | BudgetDroppedImportantItem | Must-hit evidence is selected but ranked below the top-10 package quality gate. | section priority | Extended sample chat-sample-003 should keep must-hit/important evidence under budget pressure. |
| chat-sample-004 | BudgetDroppedImportantItem | Must-hit evidence is selected but ranked below the top-10 package quality gate. | section priority | Extended sample chat-sample-004 should keep must-hit/important evidence under budget pressure. |
| chat-sample-005 | MissingMustHit | Important must-hit evidence was dropped or diluted under package budget pressure. | budget diagnostics | Extended sample chat-sample-005 should keep must-hit/important evidence under budget pressure. |
| coding-20260529-002 | ConstraintMiss | Expected constraint text is not represented in constraints/package sections. | uncertainty mapping | Extended sample coding-20260529-002 should satisfy expected uncertainty aliases. |
| coding-20260529-010 | EntityMiss | Expected entity is not represented in the selected package text. | uncertainty mapping | Extended sample coding-20260529-010 should satisfy expected uncertainty aliases. |
| novel-20260529-001 | MissingMustHit | Expected constraint text is not represented in constraints/package sections. | uncertainty mapping | Extended sample novel-20260529-001 should satisfy expected uncertainty aliases. |
| novel-20260529-009 | ConstraintMiss | Expected constraint text is not represented in constraints/package sections. | uncertainty mapping | Extended sample novel-20260529-009 should satisfy expected uncertainty aliases. |
| novel-20260529-013 | EntityMiss | Expected entity is not represented in the selected package text. | section priority | Extended sample novel-20260529-013 should preserve expected entity text in package output. |
| novel-20260529-017 | ConstraintMiss | Expected constraint text is not represented in constraints/package sections. | section priority | Extended sample novel-20260529-017 should preserve expected constraint text in package output. |

## Failed Samples

| Sample | Mode | Categories | Uncertainty Failure | Selected | Budget | MustHit | Constraint | Entity | Uncertainty | Fix Type |
|---|---|---|---|---:|---:|---|---|---|---|---|
| automation-20260529-003 | AutomationMode | MissingUncertainty, MustHitSelectedButTooLow, TooManyLowValueSelected | - | 20 | 4000 | worker:stats-latest@2<br>event:recent-failures@12 | ok | ok | missing | uncertainty mapping |
| automation-20260529-004 | AutomationMode | MissingUncertainty, MustHitSelectedButTooLow, TooManyLowValueSelected | - | 20 | 4000 | run:latest-recovery-point@2<br>step:last-succeeded@18 | ok | ok | missing | uncertainty mapping |
| automation-20260529-008 | AutomationMode | EntityMiss, MissingUncertainty | - | 19 | 4000 | job:max-retry-exceeded@2<br>queue:dead-letter@5 | ok | missing | missing | uncertainty mapping |
| automation-20260529-010 | AutomationMode | EntityMiss, MissingUncertainty | - | 20 | 4000 | run:current-failed-steps@3<br>report:actionable-fix@2 | ok | missing | missing | uncertainty mapping |
| automation-sample-001 | AutomationMode | MissingMustHit, MustHitSelectedButTooLow, TooManyLowValueSelected | - | 20 | 4000 | memory:automation-last-error@12<br>memory:automation-recovery:dropped=no<br>doc:automation-guide@2 | ok | - | - | section priority |
| chat-20260529-003 | ChatMode | ConstraintMiss, MissingUncertainty, BudgetDroppedImportantItem | - | 24 | 4000 | memory:session-conclusion@6<br>candidate:promotion-working@9 | missing | ok | missing | uncertainty mapping |
| chat-sample-003 | ChatMode | BudgetDroppedImportantItem, MustHitSelectedButTooLow, TooManyLowValueSelected | - | 26 | 4000 | memory:chat-active-plan@5<br>stable:preference-language@17 | - | - | - | section priority |
| chat-sample-004 | ChatMode | BudgetDroppedImportantItem, MustHitSelectedButTooLow, TooManyLowValueSelected | - | 27 | 4000 | memory:chat-version-conflict-v2@16 | - | ok | ok | section priority |
| chat-sample-005 | ChatMode | MissingMustHit, BudgetDroppedImportantItem | - | 3 | 100 | memory:chat-active-plan:dropped=yes | ok | - | - | budget diagnostics |
| coding-20260529-002 | CodingMode | ConstraintMiss, MissingUncertainty | - | 23 | 4000 | test:last-failure@2<br>assertion:expected-output@6 | missing | ok | missing | uncertainty mapping |
| coding-20260529-010 | CodingMode | EntityMiss, MissingUncertainty | - | 22 | 4000 | verification:build@4<br>verification:test@3<br>verification:secret-scan@5 | ok | missing | missing | uncertainty mapping |
| novel-20260529-001 | NovelMode | MissingMustHit, ConstraintMiss, MissingUncertainty | - | 12 | 4000 | plot:previous-chapter-hook@2<br>memory:active-character-state:dropped=no | missing | ok | missing | uncertainty mapping |
| novel-20260529-009 | NovelMode | ConstraintMiss, MissingUncertainty, BudgetDroppedImportantItem | - | 17 | 4000 | foreshadow:bell-sound@2 | missing | ok | missing | uncertainty mapping |
| novel-20260529-013 | NovelMode | EntityMiss | - | 9 | 4000 | item:sword-broken@2 | ok | missing | - | section priority |
| novel-20260529-017 | NovelMode | ConstraintMiss | - | 13 | 4000 | ending:current-plan@3 | missing | ok | - | section priority |

## Details

### automation-20260529-003

- Mode: `AutomationMode`
- Failed reason: Recall@10=50%; uncertainty missing
- Suspected root cause: Expected uncertainty is not mapped from uncertainty, diagnostic, evidence, historical, excluded, or risk surfaces.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=19, constraints=19, working=600, stable=30, evidence=0, diagnostics=277, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| worker:stats-latest | yes | no | 2 |  | 30 |
| event:recent-failures | yes | no | 12 |  | 30 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:automation-backup-strategy-v1 | historical_context | config-detail | 60.49 | 36 | deprecated memory is excluded in non-audit mode |
| memory:automation-stopped-cron | historical_context | cron-log | 58.99 | 45 | deprecated memory is excluded in non-audit mode |
| memory:automation-conflict-v1 | historical_context | config-detail | 39.00 | 26 | deprecated memory is excluded in non-audit mode |

### automation-20260529-004

- Mode: `AutomationMode`
- Failed reason: Recall@10=50%; uncertainty missing
- Suspected root cause: Expected uncertainty is not mapped from uncertainty, diagnostic, evidence, historical, excluded, or risk surfaces.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=19, constraints=19, working=592, stable=30, evidence=0, diagnostics=277, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| run:latest-recovery-point | yes | no | 2 |  | 39 |
| step:last-succeeded | yes | no | 18 |  | 28 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:automation-backup-strategy-v1 | historical_context | config-detail | 67.08 | 36 | deprecated memory is excluded in non-audit mode |
| memory:automation-stopped-cron | historical_context | cron-log | 51.38 | 45 | deprecated memory is excluded in non-audit mode |
| memory:automation-conflict-v1 | historical_context | config-detail | 39.00 | 26 | deprecated memory is excluded in non-audit mode |

### automation-20260529-008

- Mode: `AutomationMode`
- Failed reason: entity missing; uncertainty missing
- Suspected root cause: Expected entity is not represented in the selected package text.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=19, constraints=19, working=592, stable=30, evidence=0, diagnostics=277, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| job:max-retry-exceeded | yes | no | 2 |  | 37 |
| queue:dead-letter | yes | no | 5 |  | 37 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:automation-stopped-cron | historical_context | cron-log | 45.85 | 45 | deprecated memory is excluded in non-audit mode |
| memory:automation-backup-strategy-v1 | historical_context | config-detail | 39.00 | 36 | deprecated memory is excluded in non-audit mode |
| memory:automation-conflict-v1 | historical_context | config-detail | 39.00 | 26 | deprecated memory is excluded in non-audit mode |

### automation-20260529-010

- Mode: `AutomationMode`
- Failed reason: entity missing; uncertainty missing
- Suspected root cause: Expected entity is not represented in the selected package text.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=19, constraints=19, working=596, stable=30, evidence=0, diagnostics=277, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| run:current-failed-steps | yes | no | 3 |  | 37 |
| report:actionable-fix | yes | no | 2 |  | 40 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:automation-backup-strategy-v1 | historical_context | config-detail | 61.73 | 36 | deprecated memory is excluded in non-audit mode |
| memory:automation-stopped-cron | historical_context | cron-log | 60.23 | 45 | deprecated memory is excluded in non-audit mode |
| memory:automation-conflict-v1 | historical_context | config-detail | 39.00 | 26 | deprecated memory is excluded in non-audit mode |

### automation-sample-001

- Mode: `AutomationMode`
- Failed reason: Recall@10=33%
- Suspected root cause: Must-hit evidence is selected but ranked below the top-10 package quality gate.
- Suggested fix type: `section priority`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=19, constraints=19, working=586, stable=30, evidence=0, diagnostics=277, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| memory:automation-last-error | yes | no | 12 |  | 44 |
| memory:automation-recovery | no | no | - |  | 0 |
| doc:automation-guide | yes | no | 2 |  | 63 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:automation-backup-strategy-v1 | historical_context | config-detail | 68.80 | 36 | deprecated memory is excluded in non-audit mode |
| memory:automation-stopped-cron | historical_context | cron-log | 53.10 | 45 | deprecated memory is excluded in non-audit mode |
| memory:automation-conflict-v1 | historical_context | config-detail | 39.00 | 26 | deprecated memory is excluded in non-audit mode |

### chat-20260529-003

- Mode: `ChatMode`
- Failed reason: constraint missing; uncertainty missing
- Suspected root cause: Expected constraint text is not represented in constraints/package sections.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `True`
- BudgetPressureBreakdown: mandatory=9, constraints=42, working=463, stable=330, evidence=0, diagnostics=480, historical=0, droppedMustHit=0, droppedLowPriority=97

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| memory:session-conclusion | yes | no | 6 |  | 32 |
| candidate:promotion-working | yes | no | 9 |  | 35 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| pref:interaction-style | stable_memory | preference | 20.80 | 31 | token budget exhausted |
| rule:naming-confirmed | stable_memory | confirmed-rule | 20.80 | 31 | token budget exhausted |
| policy:no-promote-joke | stable_memory | promotion-policy | 20.40 | 35 | token budget exhausted |
| memory:chat-deprecated-draft | historical_context | draft | 49.75 | 36 | deprecated memory is excluded in non-audit mode |
| memory:chat-drink-preference-v1 | historical_context | preference | 39.00 | 26 | deprecated memory is excluded in non-audit mode |
| memory:chat-noise-keyword-1 | historical_context | preference | 39.00 | 42 | deprecated memory is excluded in non-audit mode |
| memory:chat-old-topic | historical_context | plan | 39.00 | 30 | deprecated memory is excluded in non-audit mode |
| memory:chat-version-conflict-v1 | historical_context | user-location | 39.00 | 25 | deprecated memory is excluded in non-audit mode |

### chat-sample-003

- Mode: `ChatMode`
- Failed reason: Recall@10=50%
- Suspected root cause: Must-hit evidence is selected but ranked below the top-10 package quality gate.
- Suggested fix type: `section priority`
- Uncertainty failure types: `-`
- Budget pressure: `True`
- BudgetPressureBreakdown: mandatory=9, constraints=42, working=504, stable=291, evidence=0, diagnostics=480, historical=0, droppedMustHit=0, droppedLowPriority=105

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| memory:chat-active-plan | yes | no | 5 |  | 36 |
| stable:preference-language | yes | no | 17 |  | 30 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| policy:no-promote-temporary-emotion | stable_memory | promotion-policy | 21.20 | 35 | token budget exhausted |
| policy:no-promote-joke | stable_memory | promotion-policy | 20.40 | 35 | token budget exhausted |
| policy:no-promote-oneoff | stable_memory | promotion-policy | 20.40 | 35 | token budget exhausted |
| memory:chat-noise-keyword-1 | historical_context | preference | 71.10 | 42 | deprecated memory is excluded in non-audit mode |
| memory:chat-deprecated-draft | historical_context | draft | 49.23 | 36 | deprecated memory is excluded in non-audit mode |
| memory:chat-drink-preference-v1 | historical_context | preference | 47.35 | 26 | deprecated memory is excluded in non-audit mode |
| memory:chat-old-topic | historical_context | plan | 47.35 | 30 | deprecated memory is excluded in non-audit mode |
| memory:chat-version-conflict-v1 | historical_context | user-location | 39.00 | 25 | deprecated memory is excluded in non-audit mode |

### chat-sample-004

- Mode: `ChatMode`
- Failed reason: Recall@10=0%
- Suspected root cause: Must-hit evidence is selected but ranked below the top-10 package quality gate.
- Suggested fix type: `section priority`
- Uncertainty failure types: `-`
- Budget pressure: `True`
- BudgetPressureBreakdown: mandatory=9, constraints=42, working=497, stable=326, evidence=0, diagnostics=480, historical=0, droppedMustHit=0, droppedLowPriority=101

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| memory:chat-version-conflict-v2 | yes | no | 16 |  | 25 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| rule:naming-confirmed | stable_memory | confirmed-rule | 20.80 | 31 | token budget exhausted |
| policy:no-promote-joke | stable_memory | promotion-policy | 20.40 | 35 | token budget exhausted |
| policy:no-promote-oneoff | stable_memory | promotion-policy | 20.40 | 35 | token budget exhausted |
| memory:chat-version-conflict-v1 | historical_context | user-location | 58.05 | 25 | deprecated memory is excluded in non-audit mode |
| memory:chat-deprecated-draft | historical_context | draft | 49.23 | 36 | deprecated memory is excluded in non-audit mode |
| memory:chat-drink-preference-v1 | historical_context | preference | 47.35 | 26 | deprecated memory is excluded in non-audit mode |
| memory:chat-noise-keyword-1 | historical_context | preference | 39.00 | 42 | deprecated memory is excluded in non-audit mode |
| memory:chat-old-topic | historical_context | plan | 39.00 | 30 | deprecated memory is excluded in non-audit mode |

### chat-sample-005

- Mode: `ChatMode`
- Failed reason: Recall@10=0%
- Suspected root cause: Important must-hit evidence was dropped or diluted under package budget pressure.
- Suggested fix type: `budget diagnostics`
- Uncertainty failure types: `-`
- Budget pressure: `True`
- BudgetPressureBreakdown: mandatory=9, constraints=9, working=30, stable=35, evidence=0, diagnostics=12, historical=0, droppedMustHit=36, droppedLowPriority=553

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| memory:chat-active-plan | no | yes | - | token budget exhausted | 36 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:chat-active-plan | working_memory | plan | 18.66 | 36 | token budget exhausted |
| pref:answer-conclusion-first | stable_memory | preference | 27.20 | 36 | token budget exhausted |
| constraint:no-secret-commit | stable_memory | security-constraint | 21.60 | 33 | token budget exhausted |
| policy:no-promote-temporary-emotion | stable_memory | promotion-policy | 21.20 | 35 | token budget exhausted |
| pref:actionable-concise | stable_memory | preference | 21.20 | 32 | token budget exhausted |
| pref:admit-uncertainty | stable_memory | preference | 21.20 | 33 | token budget exhausted |
| pattern:repeated-user-preference | stable_memory | preference-pattern | 20.80 | 44 | token budget exhausted |
| rule:naming-confirmed | stable_memory | confirmed-rule | 20.80 | 31 | token budget exhausted |

### coding-20260529-002

- Mode: `CodingMode`
- Failed reason: constraint missing; uncertainty missing
- Suspected root cause: Expected constraint text is not represented in constraints/package sections.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=22, constraints=22, working=603, stable=136, evidence=0, diagnostics=265, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| test:last-failure | yes | no | 2 |  | 31 |
| assertion:expected-output | yes | no | 6 |  | 29 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:coding-conflict-v1 | historical_context | signature | 51.25 | 32 | deprecated memory is excluded in non-audit mode |
| memory:coding-deprecated-logger | historical_context | documentation | 39.00 | 50 | deprecated memory is excluded in non-audit mode |
| memory:coding-timeout-v1 | historical_context | signature | 39.00 | 36 | deprecated memory is excluded in non-audit mode |

### coding-20260529-010

- Mode: `CodingMode`
- Failed reason: entity missing; uncertainty missing
- Suspected root cause: Expected entity is not represented in the selected package text.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=22, constraints=22, working=603, stable=103, evidence=0, diagnostics=265, historical=0, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| verification:build | yes | no | 4 |  | 33 |
| verification:test | yes | no | 3 |  | 34 |
| verification:secret-scan | yes | no | 5 |  | 23 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| memory:coding-conflict-v1 | historical_context | signature | 50.73 | 32 | deprecated memory is excluded in non-audit mode |
| memory:coding-deprecated-logger | historical_context | documentation | 39.00 | 50 | deprecated memory is excluded in non-audit mode |
| memory:coding-timeout-v1 | historical_context | signature | 39.00 | 36 | deprecated memory is excluded in non-audit mode |

### novel-20260529-001

- Mode: `NovelMode`
- Failed reason: Recall@10=50%; constraint missing; uncertainty missing
- Suspected root cause: Expected constraint text is not represented in constraints/package sections.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=26, constraints=26, working=133, stable=104, evidence=0, diagnostics=0, historical=124, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| plot:previous-chapter-hook | yes | no | 2 |  | 34 |
| memory:active-character-state | no | no | - |  | 0 |

### novel-20260529-009

- Mode: `NovelMode`
- Failed reason: constraint missing; uncertainty missing
- Suspected root cause: Expected constraint text is not represented in constraints/package sections.
- Suggested fix type: `uncertainty mapping`
- Uncertainty failure types: `-`
- Budget pressure: `True`
- BudgetPressureBreakdown: mandatory=26, constraints=26, working=476, stable=32, evidence=0, diagnostics=461, historical=0, droppedMustHit=0, droppedLowPriority=24

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| foreshadow:bell-sound | yes | no | 2 |  | 30 |

| Top Dropped Important Item | Kind | Type | Score | Tokens | Reason |
|---|---|---|---:|---:|---|
| ending:current-plan | working_memory | ending-plan | 18.72 | 24 | token budget exhausted |
| memory:novel-conflict-v1 | historical_context | character-detail | 39.00 | 32 | deprecated memory is excluded in non-audit mode |
| memory:novel-plot-deprecated-villain | historical_context | plot | 39.00 | 59 | deprecated memory is excluded in non-audit mode |
| memory:novel-plot-old-draft | historical_context | plot | 39.00 | 62 | deprecated memory is excluded in non-audit mode |
| memory:novel-weapon-v1 | historical_context | character-detail | 39.00 | 30 | deprecated memory is excluded in non-audit mode |

### novel-20260529-013

- Mode: `NovelMode`
- Failed reason: entity missing
- Suspected root cause: Expected entity is not represented in the selected package text.
- Suggested fix type: `section priority`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=26, constraints=26, working=73, stable=68, evidence=0, diagnostics=0, historical=124, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| item:sword-broken | yes | no | 2 |  | 29 |

### novel-20260529-017

- Mode: `NovelMode`
- Failed reason: constraint missing
- Suspected root cause: Expected constraint text is not represented in constraints/package sections.
- Suggested fix type: `section priority`
- Uncertainty failure types: `-`
- Budget pressure: `False`
- BudgetPressureBreakdown: mandatory=26, constraints=26, working=132, stable=104, evidence=0, diagnostics=0, historical=124, droppedMustHit=0, droppedLowPriority=0

| MustHit | Selected | Dropped | Rank | Dropped Reason | Tokens |
|---|---:|---:|---:|---|---:|
| ending:current-plan | yes | no | 3 |  | 24 |
