#!/bin/bash
# DropBug 独立 Godot 回归矩阵。显式启动 dropbug_sandbox.tscn，不改、也不经过
# 其它物种沙盒。每个场景由宿主输出硬断言后的 [DROPBUG-RESULT]；哈希基线钉在下表，
# 只有有意改变 DropBug 内核轨迹时才更新（更新方式：置空对应变量跑一遍取实跑值）。
set -uo pipefail
export LC_ALL=C

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_dropbug_matrix}"
LOG="${OUT}/godot.log"
SCENE="res://scenes/dropbug_sandbox.tscn"

# —— 哈希基线（2026-08-04 实跑钉定；空 = 本轮不校验绝对值，仅跑行为门）——
HASH_WALK=8C182AF34288285A
HASH_SLOPE=94F0E379F89DA988
HASH_HOP=08ADE1DECF0B2E9A
HASH_STUCK=89973DDA534BD8EA
HASH_BACKWARD=E374EC22E6B2442B
HASH_HANG=9E32321C6CAE901D
HASH_HANG_EXIT=D86F8C4A87F873D1
HASH_DIVE=77AED28CE82DF79E
HASH_POUNCE=97792F496F3440C2
HASH_POUNCE_ABANDON=DB6DCEF1D998B876
HASH_CARRY=F4A3719EE869C446
HASH_LAUNCH=77DC085503F2F0B6
HASH_LIFECYCLE=1A6C28A2D924A571
HASH_NIMBLE=8ACBF43362435CAB
HASH_BULKY=2D47FFA890CF6CC1

mkdir -p "$OUT"
if ! dotnet build proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "DROPBUG BUILD FAILED"
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
        extra+=("--dropbug-expect-hash=$expected")
    fi
    "$GODOT" --headless --path . --log-file "$LOG" --fixed-fps 40 "$SCENE" -- \
        --dropbug-determinism="$ticks" \
        --dropbug-tps="$tps" \
        --dropbug-preset="$preset" \
        --dropbug-route="$route" \
        "${extra[@]+"${extra[@]}"}" "$@" > "$file" 2>&1
    local code=$?
    if grep -q '^\[DROPBUG-RESULT\] PASS' "$file"; then
        if [ "$code" -eq 0 ]; then
            echo "[$name] PASS"
        else
            # 与既有矩阵一致：Godot macOS 偶发在 teardown 后崩溃；场景硬断言与
            # [DROPBUG-RESULT] 均在 teardown 前完成，非零退出只作提示。
            echo "[$name] PASS（teardown exit=$code，判定以 [DROPBUG-RESULT] 为准）"
        fi
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[DROPBUG-(RESULT|METRIC|DET)\]' "$file" | tail -6 | sed 's/^/    /'
        fail=1
    fi
}

final_hash() {
    grep '^\[DROPBUG-DET\]' "$OUT/$1.txt" | tail -1 | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p'
}

# 基准巡走：双跑 + 40/400Hz 时基不变性 + 1mm 微扰灵敏度。
run walk-a  original walk 1200 400 "$HASH_WALK"
run walk-b  original walk 1200 400 "$HASH_WALK"
run walk-40 original walk 1200 40  "$HASH_WALK"
run perturb original walk 1200 400 - --dropbug-perturb=0.001

# 地形与机制专项；各场景有独立行为门，不只看哈希。
run slope          original slope          500 400 "$HASH_SLOPE"
run hop            original hop            600 400 "$HASH_HOP"
run stuck          original stuck          500 400 "$HASH_STUCK"
run backward       original backward       400 400 "$HASH_BACKWARD"
run hang           original hang           400 400 "$HASH_HANG"
run hang-exit      original hang-exit      600 400 "$HASH_HANG_EXIT"
run dive           original dive           500 400 "$HASH_DIVE"
run pounce         original pounce         400 400 "$HASH_POUNCE"
run pounce-abandon original pounce-abandon 300 400 "$HASH_POUNCE_ABANDON"
run carry          original carry          470 400 "$HASH_CARRY"
run launch         original launch         500 400 "$HASH_LAUNCH"
run lifecycle      original lifecycle      500 400 "$HASH_LIFECYCLE"

# 变体预设走同一巡走路线；参数差异应产生不同轨迹。
run nimble nimble walk 1200 400 "$HASH_NIMBLE"
run bulky  bulky  walk 1200 400 "$HASH_BULKY"

if diff <(grep '^\[DROPBUG-DET\]' "$OUT/walk-a.txt") \
        <(grep '^\[DROPBUG-DET\]' "$OUT/walk-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL：两次 walk 的 [DROPBUG-DET] 序列不一致"
    fail=1
fi

if diff <(grep '^\[DROPBUG-DET\]' "$OUT/walk-a.txt") \
        <(grep '^\[DROPBUG-DET\]' "$OUT/walk-40.txt") > /dev/null; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL：逻辑 tick 轨迹受宿主执行频率影响"
    fail=1
fi

base_hash="$(final_hash walk-a)"
perturb_hash="$(final_hash perturb)"
nimble_hash="$(final_hash nimble)"
bulky_hash="$(final_hash bulky)"
if [ -n "$base_hash" ] && [ -n "$perturb_hash" ] && [ "$base_hash" != "$perturb_hash" ]; then
    echo "[perturb] PASS（$base_hash -> $perturb_hash）"
else
    echo "[perturb] FAIL：1mm 微扰未改变哈希，或未能解析终值"
    fail=1
fi

if [ -n "$nimble_hash" ] && [ -n "$bulky_hash" ] && \
   [ "$nimble_hash" != "$bulky_hash" ] && [ "$nimble_hash" != "$base_hash" ]; then
    echo "[preset-difference] PASS（original=$base_hash nimble=$nimble_hash bulky=$bulky_hash）"
else
    echo "[preset-difference] FAIL：预设未产生可观测差异，或未能解析终值"
    fail=1
fi

# 专项无引擎 smoke 自带固定哈希、行为门与消融红灯验证。
if dotnet run --no-restore --project core/dropbug_smoke > "$OUT/dropbug-smoke.txt" 2>&1; then
    echo "[dropbug-smoke] PASS"
else
    echo "[dropbug-smoke] FAIL"
    grep -E '^\[DROPBUG-CORE-' "$OUT/dropbug-smoke.txt" | sed 's/^/    /'
    fail=1
fi

if [ "$fail" -eq 0 ]; then
    echo "== DROPBUG MATRIX GREEN =="
else
    echo "== DROPBUG MATRIX RED =="
fi
exit "$fail"
