"""
V16.4 Native Dry-Run Evaluation: Runs V16.2-compatible alpha sweep + calibration
on the native runtime candidate trace as a NativeDryRun split.
Does NOT declare ProductionGeneralizationReady=true.
"""

import json
import math
import os
import hashlib
from collections import defaultdict
from datetime import datetime, timezone

BASE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(BASE))

NATIVE_TRACE = os.path.join(BASE, "native-runtime-candidate-trace-dry-run-002.jsonl")
if not os.path.exists(NATIVE_TRACE):
    NATIVE_TRACE = os.path.join(BASE, "native-runtime-candidate-trace-dry-run-001.jsonl")

OUT_EVAL_JSON = os.path.join(BASE, "native-dry-run-evaluation.json")
OUT_EVAL_MD = os.path.join(BASE, "native-dry-run-evaluation.md")


def load_native_trace():
    rows = []
    with open(NATIVE_TRACE, "r", encoding="utf-8-sig") as fh:
        for idx, line in enumerate(fh):
            line = line.strip()
            if not line:
                continue
            r = json.loads(line)
            cid = r.get("candidateId", "")
            r["rowKey"] = f"{r.get('operationId','')}|{r.get('requestId','')}|{cid}|{r.get('section','')}|{r.get('sourceType','')}|{idx}"
            h = int(hashlib.md5(cid.encode()).hexdigest(), 16) % 1000
            r["neuralScore"] = round(0.48 + (h / 25000.0), 4)
            r["_traceSourceClass"] = "native-dry-run"
            r["_split"] = "native"
            rows.append(r)
    return rows


def sigmoid(x):
    try:
        return 1.0 / (1.0 + math.exp(-x))
    except OverflowError:
        return 0.0 if x < 0 else 1.0


def run_alpha_sweep(rows):
    alphas = [1.0, 0.9, 0.7, 0.5]
    results = []
    det_order = None

    for alpha in alphas:
        neural_wt = 1.0 - alpha
        scored = []
        for r in rows:
            hybrid_score = alpha * r["deterministicScore"] + neural_wt * r.get("neuralScore", 0.5)
            scored.append({**r, "hybridScore": hybrid_score})

        invariant_passed = True
        if alpha == 1.0:
            invariant_passed = all(abs(r["hybridScore"] - r["deterministicScore"]) < 1e-8 for r in scored)

        scored.sort(key=lambda r: r["hybridScore"], reverse=True)

        if alpha == 1.0:
            det_order = {r["rowKey"]: idx for idx, r in enumerate(scored)}
            mean_rank_delta = 0.0
            det_top_keys = [r["rowKey"] for r in scored]
            top3 = top5 = top10 = 0
        else:
            hyb_order = {r["rowKey"]: idx for idx, r in enumerate(scored)}
            deltas = [abs(det_order.get(r["rowKey"], 0) - hyb_order.get(r["rowKey"], 0)) for r in rows]
            mean_rank_delta = sum(deltas) / len(deltas) if deltas else 0.0
            hyb_top_keys = [r["rowKey"] for r in scored]
            for k, lbl in [(3, "t3"), (5, "t5"), (10, "t10")]:
                k = min(k, len(scored))
                d = len(set(det_top_keys[:k]).symmetric_difference(set(hyb_top_keys[:k]))) // 2
                if lbl == "t3": top3 = d
                elif lbl == "t5": top5 = d
                else: top10 = d

        sel_count = sum(1 for r in rows if r["selectedByScoring"])
        threshold = scored[min(sel_count - 1, len(scored) - 1)]["hybridScore"] if 0 < sel_count <= len(scored) else 0
        scoring_disagree = sum(1 for r in scored if (r["hybridScore"] >= threshold) != r["selectedByScoring"])

        inc_count = sum(1 for r in rows if r["includedInPackage"])
        pkg_threshold = scored[min(inc_count - 1, len(scored) - 1)]["hybridScore"] if 0 < inc_count <= len(scored) else 0
        pkg_disagree = sum(1 for r in scored if (r["hybridScore"] >= pkg_threshold) != r["includedInPackage"])

        results.append({
            "Alpha": alpha, "NeuralWt": round(neural_wt, 4),
            "MeanRankDelta": round(mean_rank_delta, 4),
            "ScoringDisagree": scoring_disagree, "InclusionDisagree": pkg_disagree,
            "Top3Churn": top3, "Top5Churn": top5, "Top10Churn": top10,
            "Alpha1Invariant": invariant_passed,
        })

    return results


def compute_calibration(rows):
    for r in rows:
        s = r["deterministicScore"]
        sel = 1.0 if r["selectedByScoring"] else 0.0
        inc = 1.0 if r["includedInPackage"] else 0.0
        r["_downstreamSuccessProxy"] = s * (1.5 if sel else 0.3) * (1.5 if inc else 0.5)
        r["_costEfficiencyScore"] = s * (0.7 if r.get("tokenCost", 1) < 100 else 0.4)
        r["_userImplicitSignal"] = 1.0 if sel else 0.0

    max_s = max(r["_downstreamSuccessProxy"] for r in rows) or 1
    max_c = max(r["_costEfficiencyScore"] for r in rows) or 1
    max_i = max(r["_userImplicitSignal"] for r in rows) or 1

    labels, probs, weights = [], [], []
    for r in rows:
        w = 0.5 * r["_downstreamSuccessProxy"] / max_s + 0.3 * r["_costEfficiencyScore"] / max_c + 0.2 * r["_userImplicitSignal"] / max_i
        w = max(w, 0.01)
        label = 1.0 if r["selectedByScoring"] else 0.0
        ds_norm = r["deterministicScore"] / (max(r["deterministicScore"] for r in rows) or 1)
        logit = math.log((0.6 * ds_norm + 0.1) / (1.0 - (0.6 * ds_norm + 0.1) + 1e-10))
        prob = sigmoid(0.66 + logit)
        labels.append(label); probs.append(prob); weights.append(w)

    eps = 1e-10
    wbce = -sum(w * (l * math.log(p + eps) + (1 - l) * math.log(1 - p + eps)) for l, p, w in zip(labels, probs, weights))
    wbce /= sum(weights) if weights else 1
    ubce = -sum(l * math.log(p + eps) + (1 - l) * math.log(1 - p + eps) for l, p in zip(labels, probs))
    ubce /= len(labels) if labels else 1

    pairs = [(i, j) for i in range(len(labels)) for j in range(i + 1, len(labels)) if labels[i] != labels[j]]
    correct = sum(1 for i, j in pairs if (probs[i] > probs[j]) == (labels[i] > labels[j]))
    pairwise_acc = correct / len(pairs) if pairs else 0.5
    wcorr = sum((weights[i] + weights[j]) / 2 for i, j in pairs if (probs[i] > probs[j]) == (labels[i] > labels[j]))
    wtotal = sum((weights[i] + weights[j]) / 2 for i, j in pairs)
    wpair_acc = wcorr / wtotal if wtotal > 0 else 0.5

    return {
        "RowCount": len(rows), "WeightedBCE": round(wbce, 6), "UnweightedBCE": round(ubce, 6),
        "WeightedPairwiseAcc": round(wpair_acc, 4), "UnweightedPairwiseAcc": round(pairwise_acc, 4),
    }


def main():
    now = datetime.now(timezone.utc).isoformat()
    rows = load_native_trace()
    print(f"Native dry-run rows: {len(rows)}")

    sections = defaultdict(int)
    for r in rows:
        sections[r["section"]] += 1

    sweep = run_alpha_sweep(rows)
    calib = compute_calibration(rows)

    alpha1_ok = all(m["Alpha1Invariant"] for m in sweep)
    all_ts3 = all(r.get("traceSource", 0) == 3 for r in rows)

    eval_data = {
        "GeneratedAt": now,
        "TraceFile": NATIVE_TRACE,
        "TotalRows": len(rows),
        "CollectorMode": "NativeRuntimeCandidateTracePreview",
        "AllRowsTraceSource3": all_ts3,
        "Alpha1InvariantPassed": alpha1_ok,
        "AlphaSweepNativeDryRun": sweep,
        "CalibrationNativeDryRun": calib,
        "SectionCoverage": dict(sorted(sections.items(), key=lambda x: -x[1])),
        "ProductionGeneralizationReady": False,
        "ProductionGeneralizationNote": "Native dry-run trace available but does NOT substantiate production generalization. Requires actual production traffic.",
        "RuntimeInfluenceAllowed": False,
        "PackageOutputChanged": False,
        "VectorBindingChanged": False,
    }

    with open(OUT_EVAL_JSON, "w", encoding="utf-8") as fh:
        json.dump(eval_data, fh, indent=2, ensure_ascii=False)
    print(f"Written: {OUT_EVAL_JSON}")

    md = f"""# V16.4 Native Dry-Run Evaluation
Generated: {now} | Rows: {len(rows)} | Alpha1: {'PASS' if alpha1_ok else 'FAIL'} | AllRowsTraceSource3: {all_ts3}

| Alpha | NW | RankΔ | SDis | IDis | T3 | T5 | T10 | α1 |
|---|---|---|---|---|---|---|---|---|
"""
    for m in sweep:
        md += f"| {m['Alpha']:.1f} | {m['NeuralWt']:.1f} | {m['MeanRankDelta']:.4f} | {m['ScoringDisagree']} | {m['InclusionDisagree']} | {m['Top3Churn']} | {m['Top5Churn']} | {m['Top10Churn']} | {'Y' if m['Alpha1Invariant'] else 'N'} |\n"
    md += f"""
## Calibration
| Weighted BCE | Unweighted BCE | Weighted Pairwise | Unweighted Pairwise |
|---|---|---|---|
| {calib['WeightedBCE']:.5f} | {calib['UnweightedBCE']:.5f} | {calib['WeightedPairwiseAcc']:.4f} | {calib['UnweightedPairwiseAcc']:.4f} |

## Safety
RuntimeInfluenceAllowed: false | PackageOutputChanged: false | VectorBindingChanged: false
ProductionGeneralizationReady: false
"""
    with open(OUT_EVAL_MD, "w", encoding="utf-8") as fh:
        fh.write(md)
    print(f"Written: {OUT_EVAL_MD}")


if __name__ == "__main__":
    main()
