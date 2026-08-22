#!/bin/bash
# RatFiend（鼠煞）独立 Godot 回归矩阵。显式启动 ratfiend_sandbox.tscn，不改、也不经过
# 其它物种沙盒。每个场景由宿主输出硬断言后的 [RATFIEND-RESULT]；哈希基线钉在下表，
# 只有有意改变 RatFiend 内核轨迹时才更新（更新方式：置空对应变量跑一遍取实跑值）。
# 断肢路线（sever-*）走脚本化固定 tick Sever——普通路线从不调它，两族基线正交。
set -uo pipefail
export LC_ALL=C

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_ratfiend_matrix}"
LOG="${OUT}/godot.log"
SCENE="res://scenes/ratfiend_sandbox.tscn"

# —— 哈希基线（2026-08-22 实跑钉定；空 = 本轮不校验绝对值，仅跑行为门）——
HASH_WALK=625B97FAD70BED0F
HASH_RUN=00BA7EF50C692B8E
HASH_YANK=F4070876FDAF9F73
HASH_SEVER_LEG=CB82CE6DA74CA277
HASH_CRAWL_STEP=6E280145634FEDE2
HASH_SEVER_ARM_WALK=7E85A31A5881B221
HASH_SEVER_BOTH_LEGS=4586B32A423F7286
HASH_SEVER_ALL=FDE1C2761876DCC5
HASH_ATTACK=4C406BEAE36A70DB
HASH_SEVER_DURING_ATTACK=A0005B996FC695D2
HASH_DUSK_WALK=625B97FAD70BED0F   # 与 gaunt 逐位相同（dusk = 同体格调色变体，等值即断言）
HASH_BROAD_WALK=C9D836F3CB7B23BC
HASH_BROAD_SEVER_LEG=D1791E8BD0801F81
HASH_WHELP_WALK=BBD6AAC32204BA97
HASH_WHELP_SEVER_LEG=DEB3A52A2B740824

mkdir -p "$OUT"
if ! dotnet build proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "RATFIEND BUILD FAILED"
    tail -20 "$OUT/build.txt"
    exit 1
fi

fail=0

# run <name> <preset> <route> <ticks> <tps> <expected-hash|-> [额外参数...]
run() {
    local name="$1" preset="$2" route="$3" ticks="$4" tps="$5" expected="$6"
    shift 6
    local file="$OUT/$name.txt"
    local extra=()
    if [ "$expected" != "-" ] && [ -n "$expected" ]; then
        extra+=("--ratfiend-expect-hash=$expected")
    fi
    "$GODOT" --headless --path . --log-file "$LOG" --fixed-fps 40 "$SCENE" -- \
        --ratfiend-determinism="$ticks" \
        --ratfiend-tps="$tps" \
        --ratfiend-preset="$preset" \
        --ratfiend-route="$route" \
        "${extra[@]+"${extra[@]}"}" "$@" > "$file" 2>&1
    local code=$?
    if grep -q '^\[RATFIEND-RESULT\] PASS' "$file"; then
        if [ "$code" -eq 0 ]; then
            echo "[$name] PASS"
        else
            # 与既有矩阵一致：Godot macOS 偶发在 teardown 后崩溃；场景硬断言与
            # [RATFIEND-RESULT] 均在 teardown 前完成，非零退出只作提示。
            echo "[$name] PASS（teardown exit=$code，判定以 [RATFIEND-RESULT] 为准）"
        fi
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[RATFIEND-(RESULT|METRIC|DET)\]' "$file" | tail -6 | sed 's/^/    /'
        fail=1
    fi
}

final_hash() {
    grep '^\[RATFIEND-DET\]' "$OUT/$1.txt" | tail -1 | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p'
}

# 基准巡走：双跑 + 40/400Hz 时基不变性 + 1mm 微扰灵敏度。
run walk-a  gaunt walk 2000 400 "$HASH_WALK"
run walk-b  gaunt walk 2000 400 "$HASH_WALK"
run walk-40 gaunt walk 2000 40  "$HASH_WALK"
run perturb gaunt walk 2000 400 - --ratfiend-perturb=0.001

# 机制专项：跑步姿态（抬头/大张嘴/前伸）、击飞恢复、断肢全族、攻击接缝。
run run                 gaunt run                 1400 400 "$HASH_RUN"
run yank                gaunt yank                1400 400 "$HASH_YANK"
run sever-leg           gaunt sever-leg           1600 400 "$HASH_SEVER_LEG"
run crawl-step          gaunt crawl-step          1600 400 "$HASH_CRAWL_STEP"
run sever-arm-walk      gaunt sever-arm-walk      1400 400 "$HASH_SEVER_ARM_WALK"
run sever-both-legs     gaunt sever-both-legs     1800 400 "$HASH_SEVER_BOTH_LEGS"
run sever-all           gaunt sever-all           2600 400 "$HASH_SEVER_ALL"
run attack              gaunt attack              900  400 "$HASH_ATTACK"
run sever-during-attack gaunt sever-during-attack 1200 400 "$HASH_SEVER_DURING_ATTACK"

# 变体预设：巡走 + 断腿爬行（断肢/爬行是本物种核心机制，变体只跑 walk 会漏检——
# DropBug 外部评审 P2 的同款教训）。dusk 是 gaunt 的调色变体，哈希必须逐位相同。
run dusk-walk       dusk  walk      2000 400 "$HASH_DUSK_WALK"
run broad-walk      broad walk      2000 400 "$HASH_BROAD_WALK"
run broad-sever-leg broad sever-leg 1600 400 "$HASH_BROAD_SEVER_LEG"
run whelp-walk      whelp walk      2000 400 "$HASH_WHELP_WALK"
run whelp-sever-leg whelp sever-leg 1600 400 "$HASH_WHELP_SEVER_LEG"

if diff <(grep '^\[RATFIEND-DET\]' "$OUT/walk-a.txt") \
        <(grep '^\[RATFIEND-DET\]' "$OUT/walk-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL：两次 walk 的 [RATFIEND-DET] 序列不一致"
    fail=1
fi

if diff <(grep '^\[RATFIEND-DET\]' "$OUT/walk-a.txt") \
        <(grep '^\[RATFIEND-DET\]' "$OUT/walk-40.txt") > /dev/null; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL：逻辑 tick 轨迹受宿主执行频率影响"
    fail=1
fi

base_hash="$(final_hash walk-a)"
perturb_hash="$(final_hash perturb)"
dusk_hash="$(final_hash dusk-walk)"
broad_hash="$(final_hash broad-walk)"
whelp_hash="$(final_hash whelp-walk)"
if [ -n "$base_hash" ] && [ -n "$perturb_hash" ] && [ "$base_hash" != "$perturb_hash" ]; then
    echo "[perturb] PASS（$base_hash -> $perturb_hash）"
else
    echo "[perturb] FAIL：1mm 微扰未改变哈希，或未能解析终值"
    fail=1
fi

if [ -n "$broad_hash" ] && [ -n "$whelp_hash" ] && \
   [ "$broad_hash" != "$whelp_hash" ] && [ "$broad_hash" != "$base_hash" ]; then
    echo "[preset-difference] PASS（gaunt=$base_hash broad=$broad_hash whelp=$whelp_hash）"
else
    echo "[preset-difference] FAIL：预设未产生可观测差异，或未能解析终值"
    fail=1
fi

if [ -n "$dusk_hash" ] && [ "$dusk_hash" == "$base_hash" ]; then
    echo "[dusk-parity] PASS（dusk 与 gaunt 体格逐位同构）"
else
    echo "[dusk-parity] FAIL：dusk 调色变体的物理轨迹偏离 gaunt（体格参数被意外改动）"
    fail=1
fi

# 专项无引擎 smoke 自带固定哈希、行为门与消融红灯验证。
if dotnet run --no-restore --project core/ratfiend_smoke > "$OUT/ratfiend-smoke.txt" 2>&1; then
    echo "[ratfiend-smoke] PASS"
else
    echo "[ratfiend-smoke] FAIL"
    grep -E '^\[RATFIEND-CORE-' "$OUT/ratfiend-smoke.txt" | sed 's/^/    /'
    fail=1
fi

if [ "$fail" -eq 0 ]; then
    echo "== RATFIEND MATRIX GREEN =="
else
    echo "== RATFIEND MATRIX RED =="
fi
exit "$fail"
