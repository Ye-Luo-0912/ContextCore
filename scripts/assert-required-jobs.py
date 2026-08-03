#!/usr/bin/env python3
"""CI 证据门禁（步骤 1）—— 断言所有必需上游 job 均为 success。

用法：assert-required-jobs.py --manifest-dir ci-manifests

语义（对应 WP-S6 第五节修复）：
  - 读取 ci-manifests/required-jobs.json 中的必需 job 名单；
  - 每个 job 的结果从环境变量 NEEDS_<JOB 大写、'-' 转 '_'> 读取
    （例如 job "integration-postgres" → NEEDS_INTEGRATION_POSTGRES），
    由 CI 步骤 env 注入 needs.<job>.result；
  - 任一 job 结果 != "success" → exit 1：上游失败不允许 Evidence 单独绿；
  - 全部 success → exit 0。

退出码：0 通过；1 策略违反；2 配置错误（manifest 缺失/解析失败）。
"""

import argparse
import json
import os
import sys


def load_manifest(manifest_dir: str) -> dict:
    path = os.path.join(manifest_dir, "required-jobs.json")
    try:
        with open(path, encoding="utf-8") as f:
            manifest = json.load(f)
    except Exception as ex:  # noqa: BLE001 - 配置错误一律视为证据不可判定
        print(f"ERROR: 读取 required-jobs.json 失败 {path}: {ex}")
        sys.exit(2)
    jobs = manifest.get("jobs")
    if not isinstance(jobs, list) or not jobs:
        print(f"ERROR: required-jobs.json 缺少非空 jobs 数组：{path}")
        sys.exit(2)
    return manifest


def env_name(job: str) -> str:
    return "NEEDS_" + job.upper().replace("-", "_")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-dir", default="ci-manifests",
                        help="ci-manifests 目录（含 required-jobs.json）。")
    args = parser.parse_args()

    manifest = load_manifest(args.manifest_dir)
    jobs = manifest["jobs"]

    failed = []
    missing_env = []
    for job in jobs:
        name = env_name(job)
        result = os.environ.get(name)
        if result is None:
            missing_env.append(f"{job} (env {name} 未注入)")
        elif result != "success":
            failed.append(f"{job}={result}")

    if missing_env:
        print(f"ERROR: 以下必需 job 的结果环境变量未注入：{', '.join(missing_env)}")
        return 2

    if failed:
        print(f"ERROR: 必需上游 job 未全部 success（Evidence 不允许单独绿）：")
        for entry in failed:
            print(f"  - {entry}")
        return 1

    print(f"OK: {len(jobs)} 个必需 job 全部 success（{', '.join(jobs)}）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
