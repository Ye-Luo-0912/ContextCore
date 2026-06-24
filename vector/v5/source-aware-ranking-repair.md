# Source-aware Ranking Repair

OperationId: `vector-source-aware-ranking-repair-015dea28587b4b2fa253619b8a663e5f`
CreatedAt: `2026-06-19T09:52:42.3756769+00:00`

## Summary
- ReportPassed: `True`
- GatePassed: `False`
- Recommendation: `ReadyForSourceAwareRankingFreeze`
- SelectedProfile: `combined-safe`
- Train/dev improvement: recall `+0.4359`, MRR `+0.6009`, precision `+0.0872`
- Test/Holdout/Blind recall deltas: `+0.8889` / `+0.3333` / `+0.0417`
- DenseWinnerLostCount: `0`
- UniqueSourceRecoveryCount: `58`
- SourceNoiseCount: `0`
- FallbackRate: `0.0000`
- Risk/mustNot/lifecycle: `0` / `0` / `0`

## Split Metrics
| split | dense recall | selected recall | dense MRR | selected MRR | dense precision | selected precision | samples |
|---|---:|---:|---:|---:|---:|---:|---:|
| `blind-holdout` | 0.9583 | 1.0000 | 0.9583 | 1.0000 | 0.1917 | 0.2000 | 24 |
| `dev` | 0.5556 | 1.0000 | 0.2537 | 0.7778 | 0.1111 | 0.2000 | 18 |
| `holdout` | 0.6250 | 0.9583 | 0.4167 | 0.8153 | 0.1250 | 0.1917 | 24 |
| `test` | 0.1111 | 1.0000 | 0.0250 | 0.6028 | 0.0222 | 0.2000 | 18 |
| `train` | 0.4667 | 0.9000 | 0.1161 | 0.7400 | 0.0933 | 0.1800 | 60 |

## Profiles
| profile | train/dev recall | test recall | holdout recall | blind recall | dense lost | fallback | risk |
|---|---:|---:|---:|---:|---:|---:|---:|
| `combined-safe` | 0.9231 | 1.0000 | 0.9583 | 1.0000 | 0 | 0.0000 | 0 |
| `confidence-gated` | 0.9231 | 1.0000 | 0.9583 | 1.0000 | 13 | 0.0000 | 0 |
| `dense-baseline` | 0.4872 | 0.1111 | 0.6250 | 0.9583 | 0 | 0.0000 | 0 |
| `dense-preserving` | 0.9231 | 1.0000 | 0.9583 | 1.0000 | 0 | 0.0000 | 0 |
| `normalized-source` | 0.9231 | 1.0000 | 0.9583 | 1.0000 | 13 | 0.0000 | 0 |

## Blind Holdout
- CorpusItemCount: `24`
- SampleCount: `24`
- QueryLeakageCount: `0`
- ItemLeakageCount: `0`
- TemplateLeakageCount: `0`
- ContractIssueCount: `0`

## Invariants
- FormalOutputChanged: `0`
- FormalPackageWritten: `False`
- PackageOutputChanged: `False`
- PackingPolicyChanged: `False`
- RuntimeMutated: `False`
- VectorStoreBindingChanged: `False`
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- UseForRuntime: `False`

## Blocked Reasons
- (none)
