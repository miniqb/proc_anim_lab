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
HASH_WALK=7F39EB42FAC652CA
HASH_SLOPE=44F6B66339B31648
HASH_HOP=4F78562369462D6A
HASH_STUCK=0DEB39F77772FC6A
HASH_BACKWARD=8E0ABA9BE65C6E2B
HASH_HANG=26043E55D742F57D
HASH_HANG_EXIT=15076904393D2261
HASH_DIVE=1C4E4C0A4AADDBCE
HASH_POUNCE=C23AAC70FC480D82
HASH_POUNCE_ABANDON=6C2001B35BC97226
HASH_CARRY=0C90C1D848F54666
HASH_LAUNCH=63FEE582755F319E
HASH_LIFECYCLE=625C32F279C10F71
HASH_NIMBLE=14E3086C9561F50B
HASH_BULKY=6DB83D00D84E9631
HASH_HANG_LAUNCH=1DEBF1D6250EEA99
HASH_HANG_NIMBLE=302954AF5C9C82C3
HASH_HANG_BULKY=3C5E467F44EDA7A1
HASH_DIVE_NIMBLE=8CC11C9B3DB064F8
HASH_DIVE_BULKY=EC5812B897E09DCD
HASH_POUNCE_NIMBLE=115E7FA20024A77C
HASH_POUNCE_BULKY=C15E3095EBD61CC0

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
run hang-launch    original hang-launch    500 400 "$HASH_HANG_LAUNCH"
run dive           original dive           500 400 "$HASH_DIVE"
run pounce         original pounce         400 400 "$HASH_POUNCE"
run pounce-abandon original pounce-abandon 300 400 "$HASH_POUNCE_ABANDON"
run carry          original carry          470 400 "$HASH_CARRY"
run launch         original launch         500 400 "$HASH_LAUNCH"
run lifecycle      original lifecycle      500 400 "$HASH_LIFECYCLE"

# 变体预设：巡走 + 各自的悬挂/俯冲/扑击行为门（变体恰好覆写这些机制的参数，
# 只跑 walk 时回归会漏检——外部评审 P2）。
run nimble        nimble walk   1200 400 "$HASH_NIMBLE"
run bulky         bulky  walk   1200 400 "$HASH_BULKY"
run hang-nimble   nimble hang   400  400 "$HASH_HANG_NIMBLE"
run hang-bulky    bulky  hang   400  400 "$HASH_HANG_BULKY"
run dive-nimble   nimble dive   500  400 "$HASH_DIVE_NIMBLE"
run dive-bulky    bulky  dive   500  400 "$HASH_DIVE_BULKY"
run pounce-nimble nimble pounce 400  400 "$HASH_POUNCE_NIMBLE"
run pounce-bulky  bulky  pounce 400  400 "$HASH_POUNCE_BULKY"

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
