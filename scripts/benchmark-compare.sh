#!/usr/bin/env bash
# BenchmarkDotNet 回归对比脚本（真实解析 JSON，带噪声门控避免假阳性）。
#
# 用法： benchmark-compare.sh <current-dir> <baseline-dir>
#   current-dir  包含本次运行产出的 *-report-full.json
#   baseline-dir 包含基线 *-report-full.json（按文件名配对）
#
# 门控（regression %，正值 = 变慢/变大）：
#   LATENCY       Median    超过 LATENCY_THRESHOLD_PCT（默认 10%）
#   P95           P95       超过 P95_THRESHOLD_PCT（默认 15%，P95 噪声更大）
#   ALLOCATION    Bytes/Op  超过 ALLOCATION_THRESHOLD_PCT（默认 5%，分配稳定）
#   STORE         StoreCalls 超过 STORECALL_THRESHOLD_PCT（默认 0%，仅当 benchmark 发射 StoreCalls 字段时生效）
#
# 假阳性抑制（环境噪声 / 样本不足 / I/O 变异性 → 误报）：
#   NOISE_FLOOR_PCT        最小回归百分比（默认 3%）。低于此值的回归视为噪声，忽略。
#                          例：+2% 回归低于 3% 噪声底 → 不报告，即使超过 0% 的严格阈值。
#   MIN_SAMPLE_COUNT       基线最小样本数 N（默认 5）。低于此值跳过该 case 对比
#                          （统计不可靠，StdErr 未收敛）。基准数据缺失 N 时视为不满足。
#   CONFIDENCE_SIGMA       置信区间 sigma 倍数（默认 2）。延迟门控要求回归量绝对值
#                          超过 sigma × baseline_StandardError 才视为显著。
#                          例：baseline=1000ns SE=80ns，sigma=2 → 回归量需 >160ns（16%）才显著，
#                          +12%（120ns）在置信区间内 → 不报告。
#   IO_BOUND_THRESHOLD_PCT I/O 密集型 benchmark 的宽松阈值（默认 30%），覆盖 LATENCY/P95 阈值。
#                          I/O 受 OS 文件缓存 / 磁盘调度影响，CI 上天然变异大，需更宽阈值。
#   IO_BOUND_PATTERNS      I/O 密集型 benchmark 的 Type 名 glob 模式（默认 "FileSystem*|*Io*|FileIo*"），
#                          以 "|" 分隔。匹配的 case 应用 IO_BOUND_THRESHOLD_PCT。
#   REQUIRE_BASELINE       1 = 基线缺失时拒绝通过（PR 门禁专用，避免"无基线即通过"假阴性）；
#                          0 = 兼容旧行为，基线缺失时跳过对比并退出 0（main 首次建立基线场景）。默认 0。
#                          基线文件存在但损坏（空 / 非法 JSON）时无论此值如何都会退出 4。
#
# CaseKey = Type.Method | Parameters | Runtime（Runtime 取自 HostEnvironmentInfo.RuntimeVersion）
# Regression = Current / Baseline - 1
#
# 退出码：0 = 无回归；1 = 至少一个门控触发；2 = 输入错误；
#        3 = 基线缺失且 REQUIRE_BASELINE=1（PR 门禁拒绝"无基线即通过"）；
#        4 = 基线文件存在但损坏（空文件 / 非法 JSON）。
set -euo pipefail

if [ "$#" -ne 2 ]; then
  echo "usage: benchmark-compare.sh <current-dir> <baseline-dir>" >&2
  exit 2
fi

CURRENT_DIR="$1"
BASELINE_DIR="$2"

LATENCY_THRESHOLD_PCT="${LATENCY_THRESHOLD_PCT:-10}"
P95_THRESHOLD_PCT="${P95_THRESHOLD_PCT:-15}"
ALLOCATION_THRESHOLD_PCT="${ALLOCATION_THRESHOLD_PCT:-5}"
STORECALL_THRESHOLD_PCT="${STORECALL_THRESHOLD_PCT:-0}"

# 假阳性抑制参数
NOISE_FLOOR_PCT="${NOISE_FLOOR_PCT:-3}"
MIN_SAMPLE_COUNT="${MIN_SAMPLE_COUNT:-5}"
CONFIDENCE_SIGMA="${CONFIDENCE_SIGMA:-2}"
IO_BOUND_THRESHOLD_PCT="${IO_BOUND_THRESHOLD_PCT:-30}"
IO_BOUND_PATTERNS="${IO_BOUND_PATTERNS:-FileSystem*|*Io*|FileIo*}"

if [ ! -d "$CURRENT_DIR" ]; then
  echo "current-dir 不存在：$CURRENT_DIR" >&2
  exit 2
fi
# REQUIRE_BASELINE=1 时基线缺失必须失败（PR 门禁不允许"无基线即通过"的假阴性）。
# 默认 0：保持向后兼容，首次 main 运行无基线时跳过对比并退出 0，由后续 push 建立基线。
REQUIRE_BASELINE="${REQUIRE_BASELINE:-0}"
if [ ! -d "$BASELINE_DIR" ] || [ -z "$(ls -A "$BASELINE_DIR" 2>/dev/null)" ]; then
  if [ "$REQUIRE_BASELINE" = "1" ]; then
    echo "::error::Baseline not found at $BASELINE_DIR — 拒绝通过。请先运行 benchmark-main.yml 生成基线缓存，或设置 REQUIRE_BASELINE=0 显式跳过。"
    echo "regression_found=unknown"
    exit 3
  fi
  echo "No baseline found — 跳过对比（首次运行建立基线）。"
  echo "regression_found=false"
  exit 0
fi

# 基线完整性校验：每个 *-report-full.json 必须存在、非空、JSON 合法。
# cache hit 不等于基线可用——cache 可能被部分恢复或损坏。校验失败时退出 4（区分于"无基线"的 3 与"有回归"的 1）。
for baseline_file in "$BASELINE_DIR"/*-report-full.json; do
  [ -f "$baseline_file" ] || continue
  if [ ! -s "$baseline_file" ]; then
    echo "::error::Baseline file is empty: $baseline_file" >&2
    exit 4
  fi
  if ! jq empty "$baseline_file" >/dev/null 2>&1; then
    echo "::error::Baseline file is not valid JSON: $baseline_file" >&2
    exit 4
  fi
done

REGRESSIONS=()
SKIPPED=()
NEW_CASES=0
COMPARED=0

# 判断 benchmark Type 是否属于 I/O 密集型（应用宽松阈值，避免磁盘/OS 缓存噪声误报）。
# IO_BOUND_PATTERNS 以 "|" 分隔，每段作为 bash case glob 匹配 Type 名。
#
# 修复：原实现 `for pat in $IO_BOUND_PATTERNS` 在 IFS='|' 下拆分后，
# bash 将每段（如 FileSystem*、*Io*）视为文件名 glob。由于脚本上方 `shopt -s nullglob`
# 已开启，当 CWD 无匹配文件时这些 pattern 消失 → for 循环零迭代 → 函数恒返回 1（false），
# I/O 宽松阈值永远不生效。修复：用 read -ra 将 pattern 拆入数组（不触发 glob），
# 再以 "${arr[@]}" 引号遍历（禁用路径展开），仅在 case 模式处保留 glob 匹配语义。
is_io_bound() {
  local type="$1"
  local -a patterns
  IFS='|' read -ra patterns <<< "$IO_BOUND_PATTERNS"
  local pat
  for pat in "${patterns[@]}"; do
    # shellcheck disable=SC2254
    case "$type" in
      $pat) return 0;;
    esac
  done
  return 1
}

# 评估单个 case 的单一指标是否超过门控（含噪声抑制）。
# 参数： gate  type  key  base_val  cur_val  threshold_pct  base_stderr  is_alloc
#   gate:        LATENCY / P95 / ALLOC / STORE（仅用于输出标签）
#   base_stderr: 基线 StandardError（延迟门控置信检查用）；分配/Store 门控传空串跳过置信检查
#   is_alloc:    1 = 分配/Store 门控（确定性指标，只应用噪声底，不应用置信区间）；
#                0 = 延迟门控（应用噪声底 + 置信区间双重抑制）
evaluate_gate() {
  local gate="$1" type="$2" key="$3" b="$4" c="$5" thr="$6" bse="$7" is_alloc="$8"

  # 基线值非正则跳过（无意义对比）
  if ! awk -v b="$b" 'BEGIN{exit !(b+0>0)}'; then
    return 1
  fi

  # I/O 密集型 benchmark 应用宽松阈值（覆盖 LATENCY/P95 阈值）
  if is_io_bound "$type"; then
    thr="$IO_BOUND_THRESHOLD_PCT"
  fi

  local reg
  reg=$(awk -v b="$b" -v c="$c" 'BEGIN{printf "%.4f", (c/b-1)*100}')

  # 噪声底 1：回归百分比低于 NOISE_FLOOR_PCT 直接忽略（环境微抖动）
  if awk -v r="$reg" -v n="$NOISE_FLOOR_PCT" 'BEGIN{exit !(r<n)}'; then
    return 1
  fi

  # 噪声底 2（延迟门控）：回归量绝对值必须超过 CONFIDENCE_SIGMA × baseline_StandardError，
  # 否则视为落在置信区间内的统计噪声。
  if [ "$is_alloc" != "1" ] && [ -n "$bse" ] && awk -v bse="$bse" 'BEGIN{exit !(bse+0>0)}'; then
    local reg_abs
    reg_abs=$(awk -v b="$b" -v c="$c" 'BEGIN{printf "%.4f", c-b}')
    if awk -v ra="$reg_abs" -v sigma="$CONFIDENCE_SIGMA" -v se="$bse" 'BEGIN{exit !(ra < sigma*se)}'; then
      return 1
    fi
  fi

  # 超过阈值 → 报告回归
  if awk -v r="$reg" -v t="$thr" 'BEGIN{exit !(r>t)}'; then
    local pct
    pct=$(awk -v r="$reg" 'BEGIN{printf "%.2f", r}')
    case "$gate" in
      ALLOC)  REGRESSIONS+=("ALLOC     +${pct}%  $key  (bytes cur=${c} base=${b})");;
      P95)    REGRESSIONS+=("P95       +${pct}%  $key  (p95 cur=${c} base=${b})");;
      STORE)  REGRESSIONS+=("STORE     +${pct}%  $key  (calls cur=${c} base=${b})");;
      *)      REGRESSIONS+=("LATENCY   +${pct}%  $key  (median cur=${c} base=${b})");;
    esac
    return 0
  fi
  return 1
}

# 每个文件按文件名配对（Type.Method 维度跨文件聚合，避免不同 benchmark class 同名方法混淆）。
shopt -s nullglob
for current in "$CURRENT_DIR"/*-report-full.json; do
  filename=$(basename "$current")
  baseline="$BASELINE_DIR/$filename"
  if [ ! -f "$baseline" ]; then
    echo "NEW baseline-missing file: $filename（跳过该文件的对比）"
    continue
  fi

  # 匹配的 case：
  # emit type \t method \t key \t bm \t cm \t bp \t cp \t ba \t ca \t bs \t cs \t bn \t bse
  #   bn  = baseline Statistics.N（样本数）
  #   bse = baseline Statistics.StandardError（置信区间检查用）
  #
  # 修复：jq @tsv 中 StoreCalls 缺失时原输出 ""（空），导致 bs/cs 两个相邻空字段
  # 产生连续 tab。bash `read` 的 IFS=$'\t' 因 tab 属于空白字符会将连续 tab 折叠为单个
  # 分隔符，使 N/SE 值错位落入 bs/cs，bn/bse 变空 → 所有 case 误判 LOW-N(0<5) 被跳过。
  # 修复方式：StoreCalls 缺失时输出 0（数值语义正确：未发射 = 0 次调用），消除连续 tab。
  # 下游 STORE 门控条件改为检测非零值，仅在 benchmark 真实发射 StoreCalls 时才评估。
  while IFS=$'\t' read -r type method key bm cm bp cp ba ca bs cs bn bse; do
    [ -z "$key" ] && continue
    COMPARED=$((COMPARED + 1))

    # 样本数不足 → 跳过该 case（统计不可靠，避免假阳性）。
    # 基线 JSON 缺失 N 字段时 jq 输出 0（// 0 兜底），视为不满足 MIN_SAMPLE_COUNT。
    if [ -z "$bn" ] || awk -v bn="${bn:-0}" -v min="$MIN_SAMPLE_COUNT" 'BEGIN{exit !(bn+0 < min+0)}'; then
      SKIPPED+=("LOW-N(${bn:-0}<$MIN_SAMPLE_COUNT)  $key  (跳过，样本不足)")
      continue
    fi

    # Latency gate（Median）—— 延迟门控，应用噪声底 + 置信区间
    evaluate_gate "LATENCY" "$type" "$key" "$bm" "$cm" "$LATENCY_THRESHOLD_PCT" "$bse" "0" || true

    # P95 gate —— P95 噪声更大，但仍应用置信区间（用 baseline StdErr 作为噪声尺度）
    evaluate_gate "P95" "$type" "$key" "$bp" "$cp" "$P95_THRESHOLD_PCT" "$bse" "0" || true

    # Allocation gate —— 分配是确定性的（instrumented），只应用噪声底，不应用置信区间
    evaluate_gate "ALLOC" "$type" "$key" "$ba" "$ca" "$ALLOCATION_THRESHOLD_PCT" "" "1" || true

    # Store-call gate（仅当 benchmark 显式发射 StoreCalls 字段时生效；当前 benchmark 暂未发射，留作前向兼容）
    # bs/cs 缺失时为 0（jq // 0 兜底），此处仅在任一侧非零时评估，避免无意义的 0↔0 对比。
    if [ "${bs:-0}" != "0" ] || [ "${cs:-0}" != "0" ]; then
      evaluate_gate "STORE" "$type" "$key" "$bs" "$cs" "$STORECALL_THRESHOLD_PCT" "" "1" || true
    fi
  done < <(jq -n -r --slurpfile base "$baseline" --slurpfile cur "$current" '
    ($base[0].HostEnvironmentInfo.RuntimeVersion) as $rt |
    ($base[0].Benchmarks
      | map({ key: (.Type + "." + .Method + "|" + (.Parameters // "") + "|" + $rt),
              value: { m: (.Statistics.Median // 0),
                       p: (.Statistics.Percentiles.P95 // 0),
                       a: (.Memory.BytesAllocatedPerOperation // 0),
                       s: (.StoreCalls // 0),
                       n: (.Statistics.N // 0),
                       se: (.Statistics.StandardError // 0) } })
      | from_entries) as $bmap |
    $cur[0].Benchmarks[] |
      (.Type + "." + .Method + "|" + (.Parameters // "") + "|" + $rt) as $k |
      ($bmap[$k]) as $b |
      select($b != null) |
      [ .Type, .Method, $k, $b.m, (.Statistics.Median // 0), $b.p, (.Statistics.Percentiles.P95 // 0),
        $b.a, (.Memory.BytesAllocatedPerOperation // 0), $b.s, (.StoreCalls // 0),
        $b.n, $b.se ] | @tsv
  ')

  # 未匹配的 current case（新增 benchmark，无基线）
  while IFS=$'\t' read -r key; do
    [ -z "$key" ] && continue
    NEW_CASES=$((NEW_CASES + 1))
    echo "NEW case (no baseline): $key"
  done < <(jq -n -r --slurpfile base "$baseline" --slurpfile cur "$current" '
    ($base[0].HostEnvironmentInfo.RuntimeVersion) as $rt |
    ($base[0].Benchmarks
      | map({ key: (.Type + "." + .Method + "|" + (.Parameters // "") + "|" + $rt),
              value: true })
      | from_entries) as $bmap |
    $cur[0].Benchmarks[] |
      (.Type + "." + .Method + "|" + (.Parameters // "") + "|" + $rt) as $k |
      select(($bmap[$k] // null) == null) |
      $k
  ')
done

echo "----"
echo "对比 case 数：$COMPARED；新增 case 数：$NEW_CASES"
if [ "${#SKIPPED[@]}" -gt 0 ]; then
  echo "跳过 case 数：${#SKIPPED[@]}（样本不足，避免假阳性）："
  printf '  %s\n' "${SKIPPED[@]}"
fi
echo "门控阈值：latency>${LATENCY_THRESHOLD_PCT}% p95>${P95_THRESHOLD_PCT}% alloc>${ALLOCATION_THRESHOLD_PCT}% store>${STORECALL_THRESHOLD_PCT}%"
echo "噪声抑制：noiseFloor=${NOISE_FLOOR_PCT}% minSamples=${MIN_SAMPLE_COUNT} confidenceSigma=${CONFIDENCE_SIGMA}xStdErr ioBoundThreshold=${IO_BOUND_THRESHOLD_PCT}% ioBoundPatterns=${IO_BOUND_PATTERNS} requireBaseline=${REQUIRE_BASELINE}"

if [ "${#REGRESSIONS[@]}" -gt 0 ]; then
  echo "regression_found=true"
  echo "## 检测到 ${#REGRESSIONS[@]} 项性能回归："
  printf '  %s\n' "${REGRESSIONS[@]}"
  exit 1
fi

echo "regression_found=false"
echo "无性能回归。"
exit 0
