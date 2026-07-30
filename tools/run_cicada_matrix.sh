#!/bin/bash
# Cicada 独立 Godot 回归矩阵。显式启动 cicada_sandbox.tscn，不改、也不经过
# 既有 Lizard 沙盒。每个场景由宿主输出硬断言后的 [CICADA-RESULT]。
set -uo pipefail
export LC_ALL=C

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_cicada_matrix}"
LOG="${OUT}/godot.log"
SCENE="res://scenes/cicada_sandbox.tscn"

mkdir -p "$OUT"
if ! dotnet build proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "CICADA BUILD FAILED"
    tail -20 "$OUT/build.txt"
    exit 1
fi

fail=0

# run <name> <preset> <route> <ticks> <tps> [额外参数...]
run() {
    local name="$1" preset="$2" route="$3" ticks="$4" tps="$5"
    shift 5
    local file="$OUT/$name.txt"
    "$GODOT" --headless --path . --log-file "$LOG" --fixed-fps 40 "$SCENE" -- \
        --cicada-determinism="$ticks" \
        --cicada-tps="$tps" \
        --cicada-preset="$preset" \
        --cicada-route="$route" \
        "$@" > "$file" 2>&1
    local code=$?
    if grep -q '^\[CICADA-RESULT\] PASS' "$file"; then
        if [ "$code" -eq 0 ]; then
            echo "[$name] PASS"
        else
            # 与既有矩阵一致：Godot macOS 偶发在 teardown 后崩溃；场景硬断言及
            # [CICADA-RESULT] 均在 teardown 前完成，非零退出只作提示。
            echo "[$name] PASS（teardown exit=$code，判定以 [CICADA-RESULT] 为准）"
        fi
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[CICADA-(RESULT|METRIC|SCENARIO|DET)\]' "$file" | tail -20 | sed 's/^/    /'
        fail=1
    fi
}

final_hash() {
    grep '^\[CICADA-DET\]' "$OUT/$1.txt" | tail -1 | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p'
}

# 基准自由飞行：双跑 + 40/400Hz。
run light-a   light flight 800 400
run light-b   light flight 800 400
run light-40  light flight 800 40

# 1mm 初始位置微扰必须被 bit-exact 哈希看见。
run perturb light flight 800 400 --cicada-perturb=0.001

# 显式停驻、起飞和 Charge 地形交互；场景内另有行为门，不只看哈希。
run wall        light wall        520 400
run ceiling     light ceiling     520 400
run takeoff     light takeoff     360 400
run charge-wall light charge-wall 260 400

# 第二预设走同一自由飞行路线；参数差异应产生不同轨迹。
run dark dark flight 800 400

if diff <(grep '^\[CICADA-DET\]' "$OUT/light-a.txt") \
        <(grep '^\[CICADA-DET\]' "$OUT/light-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL：两次 light 的 [CICADA-DET] 序列不一致"
    fail=1
fi

if diff <(grep '^\[CICADA-DET\]' "$OUT/light-a.txt") \
        <(grep '^\[CICADA-DET\]' "$OUT/light-40.txt") > /dev/null; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL：逻辑 tick 轨迹受宿主执行频率影响"
    fail=1
fi

base_hash="$(final_hash light-a)"
perturb_hash="$(final_hash perturb)"
dark_hash="$(final_hash dark)"
if [ -n "$base_hash" ] && [ -n "$perturb_hash" ] && [ "$base_hash" != "$perturb_hash" ]; then
    echo "[perturb] PASS（$base_hash -> $perturb_hash）"
else
    echo "[perturb] FAIL：1mm 微扰未改变哈希，或未能解析终值"
    fail=1
fi

if [ -n "$base_hash" ] && [ -n "$dark_hash" ] && [ "$base_hash" != "$dark_hash" ]; then
    echo "[preset-difference] PASS（light=$base_hash dark=$dark_hash）"
else
    echo "[preset-difference] FAIL：light/dark 未产生可观测差异，或未能解析终值"
    fail=1
fi

# 专项无引擎 smoke 自带固定哈希与逐场景断言。
if dotnet run --no-restore --project core/cicada_smoke > "$OUT/cicada-smoke.txt" 2>&1; then
    echo "[cicada-smoke] PASS"
else
    echo "[cicada-smoke] FAIL"
    grep -E '^\[CICADA-CORE-' "$OUT/cicada-smoke.txt" | sed 's/^/    /'
    fail=1
fi

if [ "$fail" -eq 0 ]; then
    echo "== CICADA MATRIX GREEN =="
else
    echo "== CICADA MATRIX RED =="
fi
exit "$fail"
