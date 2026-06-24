# ContextCore Mainline Gap Map

- CurrentOverallStatus: `FoundationFrozen_FormalRetrievalPlanOnly`
- Recommendation: `ReadyForMainlineGapRepairPlanning`
## Mainline Gaps
| Area | Severity | Gap | Bucket | Recommended Action |
| --- | --- | --- | --- | --- |
| Graph | High | Graph recall, noise, and relation quality still need a mainline formal-candidate audit. | must-do before formal retrieval | Run relation quality and noise audit before graph candidates can influence formal retrieval. |
| Vector | High | Vector recall and ranking are validated through preview/shadow gates but remain outside formal retrieval. | must-do before formal retrieval | Build shadow adapter and compare vector candidates against package assembly without mutation. |
| Input | High | Input ingestion evidence, provenance, and lifecycle metadata remain the strongest formal-readiness dependency. | must-do before formal retrieval | Require Dataset V2 metadata contract and backfill checks before formal adapter input use. |
| Output | High | Output package assembly, token budget, and priority policy have not accepted formal vector changes. | must-do before formal retrieval | Add formal adapter package comparison with explicit token budget and priority invariants. |
| Learning | Medium | Learning feedback has approved-data surfaces but no runtime training or negative-sample promotion path. | can-defer | Define approved feedback and negative sample shadow-training readiness before any learning-driven ranking switch. |
| Foundation | Medium | Phase report runners and artifact readers are duplicated across many gates. | optimization later | Consolidate report reading and markdown helpers after the V5 adapter gate is stable. |
| Service | Low | Superseded side-branch reports and smoke artifacts should be archived after mainline freeze points are tagged. | side-branch cleanup later | Prune or archive obsolete generated artifacts later without touching gate inputs. |

## Must Do Before Formal Retrieval
- Build ShadowFormalRetrievalAdapter as the next V5 phase.
- Run formal adapter shadow comparison against package assembly without package output mutation.
- Recheck graph relation quality, relation noise, and graph contribution before formal candidate use.
- Enforce ingestion evidence/provenance/lifecycle metadata contract for formal retrieval inputs.
- Define output package token budget and priority policy shadow checks before any PackingPolicy integration.

## Can Defer
- Legacy corpus recall repair that lacks evidence/provenance.
- Additional service API surfaces beyond the frozen read-only foundation API.
- Broad provider comparison refresh unless the formal adapter requires it.

## Optimization Later
- Consolidate repeated report/gate runner boilerplate after mainline adapter gates are stable.
- Add focused performance baselines for shadow adapter candidate generation.
- Reduce artifact reader duplication in ControlRoom status rendering.

## Side Branch Cleanup Later
- Prune obsolete smoke traces and superseded generated reports after a release tag.
- Compact older phase notes that are already represented by freeze reports.
- Archive exploratory side-branch eval outputs outside the mainline gate chain.

## Boundary
- FormalRetrievalAllowed: `False`
- RuntimeSwitchAllowed: `False`
- ReadyForRuntimeSwitch: `False`
- PackingPolicyChanged: `False`
- PackageOutputChanged: `False`
