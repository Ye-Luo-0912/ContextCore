#!/usr/bin/env python3
"""CI 证据（步骤 3）—— 写入 head-evidence.json（HEAD 可追溯 + policy manifest 快照）。

用法：write-head-evidence.py --manifest-dir ci-manifests --out evidence/head-evidence.json

环境变量（CI 步骤注入）：
  HEAD_SHA / HEAD_SHORT_SHA / RUN_ID / RUN_URL / REPOSITORY / BRANCH
  NEEDS_<JOB>（每个必需 job 的 needs.<job>.result，与 assert-required-jobs.py 同规则）

语义：
  - policy 块从 ci-manifests/*.json 读取（requiredJobs / requiredArtifacts /
    requiredTestCategories），保证 head-evidence.json 与仓库 manifest 单一来源一致；
  - jobs 块记录每个必需 job 的实际结果（含非 success 的失败证据）；
  - gate 固定为 "evidence-manifest"。
"""

import argparse
import json
import os
import sys


def load_json(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception as ex:  # noqa: BLE001 - 配置错误一律视为证据不可判定
        print(f"ERROR: 读取 manifest 失败 {path}: {ex}")
        sys.exit(2)


def env_name(job: str) -> str:
    return "NEEDS_" + job.upper().replace("-", "_")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-dir", default="ci-manifests",
                        help="ci-manifests 目录（含 required-*.json）。")
    parser.add_argument("--out", default="evidence/head-evidence.json",
                        help="输出文件路径。")
    args = parser.parse_args()

    jobs_manifest = load_json(os.path.join(args.manifest_dir, "required-jobs.json"))
    artifacts_manifest = load_json(os.path.join(args.manifest_dir, "required-artifacts.json"))
    categories_manifest = load_json(os.path.join(args.manifest_dir, "required-test-categories.json"))

    jobs = jobs_manifest.get("jobs") or []
    artifacts = artifacts_manifest.get("artifacts") or []
    categories = categories_manifest.get("categories") or []

    job_results = {}
    for job in jobs:
        job_results[job] = os.environ.get(env_name(job), "unknown")

    document = {
        "headSha": os.environ.get("HEAD_SHA", ""),
        "headShortSha": os.environ.get("HEAD_SHORT_SHA", ""),
        "runId": os.environ.get("RUN_ID", ""),
        "runUrl": os.environ.get("RUN_URL", ""),
        "repository": os.environ.get("REPOSITORY", ""),
        "branch": os.environ.get("BRANCH", ""),
        "jobs": job_results,
        "policy": {
            "manifestVersion": str(jobs_manifest.get("version", 1)),
            "requiredJobs": jobs,
            "requiredArtifacts": [a.get("name") for a in artifacts],
            "requiredTestCategories": [c.get("name") for c in categories]
        },
        "gate": "evidence-manifest"
    }

    out_dir = os.path.dirname(args.out)
    if out_dir:
        os.makedirs(out_dir, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(document, f, ensure_ascii=False, indent=2)
        f.write("\n")

    print(f"OK: 已写入 {args.out}（{len(jobs)} 个 job 结果，policy manifest v{job_results and jobs_manifest.get('version', 1)}）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
