# Extended Eval Failure Triage Report

Generated: 2026-06-26 06:51:25 +00:00

## Summary

- Total samples: `113`
- Failed samples: `0`

### Category Counts

| Category | Count |
|---|---:|

### Mode Counts

| Mode | Failed |
|---|---:|

## Failed Sample Fix Plan

| Sample | Failure Type | Suspected Root Cause | Fix Type | Expected Regression Test |
|---|---|---|---|---|

## Failed Samples

| Sample | Mode | Categories | Uncertainty Failure | Selected | Budget | MustHit | Constraint | Entity | Uncertainty | Fix Type |
|---|---|---|---|---:|---:|---|---|---|---|---|

## Details

## P15 Baseline Freeze

- Baseline doc: `docs/eval-baseline-p15.md`
- A3 report: `eval/eval-report-p15-a3.json`
- Extended report: `eval/eval-report-p15-extended.json`
- A3 baseline: `50 total / 0 failed / 100.00%`
- Extended baseline: `113 total / 0 failed / 100.00%`

Regression gate:

- A3 must remain `100.00%`
- Extended must remain `100.00%`
- mustNotHit violation = `0`
- lifecycle violation = `0`
- hard constraint missing = `0`

`chat-20260529-003` was closed through the formal constraint activation path: `ConstraintGapCandidate accept -> CandidateConstraint activate -> Active/Hard Constraint -> package constraints section`. It was not closed through resolver aliasing or eval special casing.
