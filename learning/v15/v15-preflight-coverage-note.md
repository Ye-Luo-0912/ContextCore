# V14→V15 Preflight Coverage Note

Generated: 2026-07-03T03:03:15.3380258+00:00

## V14 Smoke Trace Coverage Gaps
- Total trace rows: 17
- Covered sections: current_task, hard_constraints, working_memory, global_context, recent_context, stable_memory, soft_constraints
- **Missing sections: related_context, legacy/raw**

## Impact on V15 Neural Dry-Run
- Feature vectors derived only from covered sections
- related_context: relation expansion candidates not present in training feature set
- legacy/raw: raw document candidates not present in training feature set

## Warning
V15 dry-run selection agreement statistics are computed on a **smoke corpus with known coverage gaps**.
Do not interpret these statistics as production generalization capability.
Re-run against a full production trace before reducing BlendAlpha below 1.0.
