# Ranker Hard Negative Dataset Report

Generated: 2026-06-06 09:57:47 +00:00
Source audit: `D:\Users\Ye_Luo\AppData\Local\Context\learning\baselines\ranker-residual-audit-report.json`
Output: `D:\Users\Ye_Luo\AppData\Local\Context\learning\features\hard-negatives.jsonl`
Policy: `learning-offline-baseline/v1`
Status: `Ready`
Source failures: `3`
Hard negatives: `18`

## Type Counts
| Key | Count |
|---|---:|
| DeprecatedSameKeyword | 3 |
| HistoricalSelectedNoise | 3 |
| KeywordNoise | 3 |
| SemanticAnchorOvermatch | 3 |
| VersionConflict | 3 |
| WeakLifecycleMarker | 3 |

## Cluster Counts
| Key | Count |
|---|---:|
| DeprecatedNoise | 18 |

## Examples
| Type | Sample | Positive | Negative | Reason |
|---|---|---|---|---|
| DeprecatedSameKeyword | chat-sample-002 | memory:chat-active-plan | memory:chat-noise-keyword-1 | Deprecated or historical negative shares strong keyword/semantic surface with the positive. |
| HistoricalSelectedNoise | chat-sample-002 | memory:chat-active-plan | memory:chat-noise-keyword-1 | Historical negative remains competitive despite selected/rank evidence favoring the positive. |
| KeywordNoise | chat-sample-002 | memory:chat-active-plan | memory:chat-noise-keyword-1 | Keyword overlap favored a low-value or noisy negative candidate. |
| SemanticAnchorOvermatch | chat-sample-002 | memory:chat-active-plan | memory:chat-noise-keyword-1 | Semantic anchor score overmatched a deprecated or historical negative. |
| VersionConflict | chat-sample-002 | memory:chat-active-plan | memory:chat-noise-keyword-1 | Older version or historical negative outranked current positive evidence. |
| WeakLifecycleMarker | chat-sample-002 | memory:chat-active-plan | memory:chat-noise-keyword-1 | Lifecycle marker was not strong enough to demote the negative in the offline baseline. |
| DeprecatedSameKeyword | chat-sample-004 | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | Deprecated or historical negative shares strong keyword/semantic surface with the positive. |
| HistoricalSelectedNoise | chat-sample-004 | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | Historical negative remains competitive despite selected/rank evidence favoring the positive. |
| KeywordNoise | chat-sample-004 | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | Keyword overlap favored a low-value or noisy negative candidate. |
| SemanticAnchorOvermatch | chat-sample-004 | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | Semantic anchor score overmatched a deprecated or historical negative. |
| VersionConflict | chat-sample-004 | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | Older version or historical negative outranked current positive evidence. |
| WeakLifecycleMarker | chat-sample-004 | memory:chat-version-conflict-v2 | memory:chat-version-conflict-v1 | Lifecycle marker was not strong enough to demote the negative in the offline baseline. |
| DeprecatedSameKeyword | novel-sample-008 | memory:novel-weapon-v2 | memory:novel-weapon-v1 | Deprecated or historical negative shares strong keyword/semantic surface with the positive. |
| HistoricalSelectedNoise | novel-sample-008 | memory:novel-weapon-v2 | memory:novel-weapon-v1 | Historical negative remains competitive despite selected/rank evidence favoring the positive. |
| KeywordNoise | novel-sample-008 | memory:novel-weapon-v2 | memory:novel-weapon-v1 | Keyword overlap favored a low-value or noisy negative candidate. |
| SemanticAnchorOvermatch | novel-sample-008 | memory:novel-weapon-v2 | memory:novel-weapon-v1 | Semantic anchor score overmatched a deprecated or historical negative. |
| VersionConflict | novel-sample-008 | memory:novel-weapon-v2 | memory:novel-weapon-v1 | Older version or historical negative outranked current positive evidence. |
| WeakLifecycleMarker | novel-sample-008 | memory:novel-weapon-v2 | memory:novel-weapon-v1 | Lifecycle marker was not strong enough to demote the negative in the offline baseline. |
