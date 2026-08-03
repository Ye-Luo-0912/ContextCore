#!/usr/bin/env python3
"""CI 生产证据门禁（步骤 2）—— 拒绝一切"未真实执行"的测试结果。

用法：gate-evidence.py --manifest-dir ci-manifests [--trx-manifest evidence/trx-manifest.json] TRX_ROOT...

语义（WP-S6 第五节修复，替代 gate-no-inconclusive.py）：
  1. 按 required-artifacts.json 校验每个必需工件目录（TRX_ROOT/<dir>）存在且至少含 1 个 TRX；
     缺失/无法解析 → exit 2（证据不完整）。
  1b. 可选 --trx-manifest（write-trx-manifest.py 输出）：校验清单与磁盘实际扫描结果完全一致
      （清单条目存在、无遗漏、计数一致），防止清单与被门禁的 TRX 集漂移；
      不一致 → exit 2（证据不可判定）。
  2. 按 required-test-categories.json 校验每个必测类别 executed >= minExecuted：
     executed = outcome in {Passed, Failed}（真实执行的结果）；
     任一类别 executed < minExecuted → exit 1（环境跳过的必测项，不允许）。
  3. 全局 0 Failed / 0 Inconclusive：任一 Failed / Inconclusive → exit 1
     （不允许用失败或 Inconclusive 掩盖缺失的证据）。
  4. 非执行结果（NotExecuted / Skipped）：
     - 匹配所属类别 allowNotExecuted 白名单（文档化已知 [Ignore]，后缀匹配）→ 允许并计数；
     - 白名单外 → exit 1（任何未声明的跳过都视为环境跳过，不允许）。
  5. 输出每类别统计（executed/passed/failed/inconclusive/notExecuted/skipped）到 stdout，
     供 CI step summary 使用。

退出码：0 通过；1 策略违反；2 证据缺失/配置错误。
"""

import argparse
import json
import os
import sys
import xml.etree.ElementTree as ET

TRX_NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
EXECUTED_OUTCOMES = {"Passed", "Failed"}
NON_EXECUTED_OUTCOMES = {"NotExecuted", "Skipped"}


def load_json(path: str) -> dict:
    try:
        with open(path, encoding="utf-8") as f:
            return json.load(f)
    except Exception as ex:  # noqa: BLE001 - 配置错误一律视为证据不可判定
        print(f"ERROR: 读取 manifest 失败 {path}: {ex}")
        sys.exit(2)


def collect_trx_files(directory: str):
    """收集 directory 下的 .trx 文件（递归）。"""
    files = []
    for root, _dirs, names in os.walk(directory):
        for name in names:
            if name.endswith(".trx"):
                files.append(os.path.join(root, name))
    return files


def parse_trx(path: str):
    """解析 TRX，返回 (results, error)。results 为 (testName, outcome) 列表。"""
    try:
        tree = ET.parse(path)
    except Exception as ex:  # noqa: BLE001 - 解析失败视为证据不可判定
        return None, f"解析 TRX 失败 {path}: {ex}"
    root = tree.getroot()
    results = []
    for result in root.findall(".//t:UnitTestResult", TRX_NS):
        outcome = result.get("outcome", "")
        test_name = result.get("testName", "?")
        results.append((test_name, outcome))
    return results, None


def is_allowed(test_name: str, allowlist) -> bool:
    """白名单匹配：精确相等或后缀匹配（容忍 namespace/类前缀差异）。"""
    for entry in allowlist:
        if test_name == entry:
            return True
        if test_name.endswith("." + entry) or test_name.endswith(entry):
            return True
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-dir", default="ci-manifests",
                        help="ci-manifests 目录（含 required-*.json）。")
    parser.add_argument("--trx-manifest", default=None,
                        help="TRX 清单（write-trx-manifest.py 输出），校验与磁盘一致性。")
    parser.add_argument("roots", nargs="+", help="TRX 根目录（如 evidence/trx）。")
    args = parser.parse_args()

    artifacts_manifest = load_json(os.path.join(args.manifest_dir, "required-artifacts.json"))
    categories_manifest = load_json(os.path.join(args.manifest_dir, "required-test-categories.json"))

    artifacts = artifacts_manifest.get("artifacts")
    categories = categories_manifest.get("categories")
    if not isinstance(artifacts, list) or not isinstance(categories, list):
        print("ERROR: required-artifacts.json / required-test-categories.json 结构非法。")
        return 2

    # ── 1. 每类证据：定位 TRX 目录 ─────────────────────────────────────────
    category_dirs = {cat.get("dir"): cat for cat in categories if cat.get("dir")}
    for artifact in artifacts:
        cat_dir = artifact.get("dir")
        if cat_dir and cat_dir not in category_dirs:
            print(f"ERROR: required-artifacts.json 的 dir '{cat_dir}' 未在 required-test-categories.json 中定义。")
            return 2

    # 汇总：dir → (trx_files, category)
    dir_trx = {}
    missing = []
    for cat_dir, cat in category_dirs.items():
        files = []
        for root in args.roots:
            candidate = os.path.join(root, cat_dir)
            if os.path.isdir(candidate):
                files.extend(collect_trx_files(candidate))
        dir_trx[cat_dir] = (files, cat)
        if not files:
            missing.append(f"{cat.get('name')} ({cat_dir})")

    if missing:
        print(f"ERROR: 以下必测类别的 TRX 证据缺失（上游 job 未上传或下载失败）：")
        for name in missing:
            print(f"  - {name}")
        return 2

    # ── 2/3/4. 解析并统计 ─────────────────────────────────────────────────
    violations = []          # 策略违反：Failed / Inconclusive / 白名单外 NotExecuted-Skipped
    missing_evidence = []    # 证据缺失：解析失败
    summary = {}             # dir → 统计
    for cat_dir, (files, cat) in dir_trx.items():
        name = cat.get("name", cat_dir)
        allowlist = cat.get("allowNotExecuted") or []
        tally = {"executed": 0, "passed": 0, "failed": 0,
                 "inconclusive": 0, "notExecuted": 0, "skipped": 0}
        for path in files:
            results, error = parse_trx(path)
            if error:
                missing_evidence.append(error)
                continue
            for test_name, outcome in results:
                if outcome in EXECUTED_OUTCOMES:
                    tally["executed"] += 1
                    if outcome == "Passed":
                        tally["passed"] += 1
                    else:
                        tally["failed"] += 1
                        violations.append(f"[{name}] Failed: {test_name} ({path})")
                elif outcome == "Inconclusive":
                    tally["inconclusive"] += 1
                    violations.append(f"[{name}] Inconclusive: {test_name} ({path})")
                elif outcome in NON_EXECUTED_OUTCOMES:
                    if is_allowed(test_name, allowlist):
                        if outcome == "NotExecuted":
                            tally["notExecuted"] += 1
                        else:
                            tally["skipped"] += 1
                    else:
                        violations.append(f"[{name}] 未声明跳过 ({outcome}): {test_name} ({path})")
                # 其他 outcome（如 Pending/Completed）不参与判定，仅忽略
        summary[name] = tally

    # ── 1b. TRX 清单一致性（--trx-manifest 提供时）────────────────────────
    # 清单由 write-trx-manifest.py 在 gate 前生成；此处校验清单与磁盘实际
    # 扫描结果完全一致（清单条目存在、无遗漏、计数一致），防止清单与
    # 被门禁的 TRX 集漂移（如清单由陈旧目录生成后 TRX 被替换）。
    # 清单文件路径相对第一个 TRX root（与 write-trx-manifest.py 的定位一致）。
    if args.trx_manifest:
        manifest = load_json(args.trx_manifest)
        manifest_cats = manifest.get("categories")
        if not isinstance(manifest_cats, dict):
            print("ERROR: TRX 清单 categories 结构非法。")
            return 2
        for cat_dir, (files, cat) in dir_trx.items():
            entry = manifest_cats.get(cat_dir)
            if not isinstance(entry, dict):
                print(f"ERROR: TRX 清单缺少类别 '{cat_dir}'（清单与被门禁 TRX 集不一致）。")
                return 2
            manifest_files = {os.path.normpath(f.get("file", "")) for f in entry.get("trxFiles") or []}
            actual_files = {os.path.normpath(os.path.relpath(f, args.roots[0])) for f in files}
            if manifest_files != actual_files:
                missing_in_manifest = sorted(actual_files - manifest_files)
                stale_in_manifest = sorted(manifest_files - actual_files)
                print(f"ERROR: TRX 清单与磁盘不一致（类别 '{cat_dir}'）：")
                if missing_in_manifest:
                    print(f"  - 磁盘存在但清单缺失: {missing_in_manifest}")
                if stale_in_manifest:
                    print(f"  - 清单存在但磁盘缺失: {stale_in_manifest}")
                return 2
            tally_key = cat.get("name", cat_dir)
            if (entry.get("executed") != summary[tally_key]["executed"]
                    or entry.get("passed") != summary[tally_key]["passed"]
                    or entry.get("failed") != summary[tally_key]["failed"]):
                print(f"ERROR: TRX 清单计数与门禁统计不一致（类别 '{cat_dir}'）："
                      f"清单 executed/passed/failed = "
                      f"{entry.get('executed')}/{entry.get('passed')}/{entry.get('failed')}，"
                      f"门禁 = {summary[tally_key]['executed']}/"
                      f"{summary[tally_key]['passed']}/{summary[tally_key]['failed']}。")
                return 2

    # 证据缺失（TRX 解析失败）优先于策略违反：证据不完整
    if missing_evidence:
        print("ERROR: TRX 证据不完整：")
        for line in missing_evidence:
            print(f"  - {line}")
        return 2

    # 必测类 executed 下限（环境跳过的必测项）
    for cat_dir, (files, cat) in dir_trx.items():
        name = cat.get("name", cat_dir)
        min_executed = int(cat.get("minExecuted", 1) or 1)
        executed = summary[name]["executed"]
        if executed < min_executed:
            violations.append(
                f"[{name}] 必测类别真实执行 {executed} 条 < minExecuted {min_executed} "
                f"（环境跳过的必测项，不允许）")

    # ── 5. 输出统计 + 判定 ────────────────────────────────────────────────
    print("Evidence 汇总（每必测类别）：")
    for name in summary:
        t = summary[name]
        print(
            f"  {name}: executed={t['executed']} passed={t['passed']} failed={t['failed']} "
            f"inconclusive={t['inconclusive']} notExecuted={t['notExecuted']} skipped={t['skipped']}")

    if violations:
        print(f"ERROR: 发现 {len(violations)} 项证据策略违反：")
        for line in violations[:30]:
            print(f"  - {line}")
        if len(violations) > 30:
            print(f"  ... 及另外 {len(violations) - 30} 项")
        return 1

    total_executed = sum(t["executed"] for t in summary.values())
    print(f"OK: {len(summary)} 个必测类别共 {total_executed} 条真实执行，"
          "0 Failed / 0 Inconclusive / 0 未声明跳过。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
