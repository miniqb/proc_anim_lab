#!/bin/bash
# Deer 独立 Godot 回归矩阵。只启动 deer_sandbox.tscn；场景内的行为门和本脚本的
# 跨进程确定性比较都以退出码决定成败，不借用 Lizard 或其它物种的沙盒入口。
set -uo pipefail
export LC_ALL=C

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_deer_matrix}"
LOG="${OUT}/godot.log"
SCENE="res://scenes/deer_sandbox.tscn"

# 每个配置都把脚本实跑得到的终态哈希传回场景，由场景以退出码钉死；跨进程比较
# 仍另外证明同参数双跑与 40/400Hz 完整 [DEER-DET] 序列一致。
HASH_ORIGINAL="B02B1A5648208F02"
HASH_PERTURB="FABFED3F0BB0F3DF"
HASH_COMPACT="C817EC40407B66FB"
HASH_STRIDER="B5F40B8AA66CFFA1"
HASH_SLOPE="40D599ED04216720"
HASH_STEPS="7C1FFF9B6792FE98"
HASH_WALL="079C1C8DD02EE82A"
HASH_TURN="E5883FBAF6C5CC69"
HASH_REVERSE_ORIGINAL="D4FA378502BD7394"
HASH_REVERSE_COMPACT="50035DB70AD14A2F"
HASH_REVERSE_STRIDER="D6494BFCC50770DE"
HASH_ROUGH="F82576BDBF79DA85"
HASH_REST="6166233D7FBCE179"
HASH_LAUNCH="2F0C8FA0609676B6"
HASH_TARGET="95847339455ED62A"
HASH_LIFECYCLE="90C2A98FA2211208"

mkdir -p "$OUT"
if ! dotnet build --no-restore proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "DEER BUILD FAILED"
    tail -30 "$OUT/build.txt"
    exit 1
fi
if ! dotnet build --no-restore core/deer_smoke/ProcAnim.Core.DeerSmoke.csproj \
        > "$OUT/deer-smoke-build.txt" 2>&1; then
    echo "DEER SMOKE BUILD FAILED"
    tail -30 "$OUT/deer-smoke-build.txt"
    exit 1
fi

fail=0

# run <name> <preset> <route> <ticks> <tps> [额外参数...]
run() {
    local name="$1" preset="$2" route="$3" ticks="$4" tps="$5"
    shift 5
    local file="$OUT/$name.txt"
    "$GODOT" --headless --path . --log-file "$LOG" --fixed-fps 40 "$SCENE" -- \
        --deer-determinism="$ticks" \
        --deer-tps="$tps" \
        --deer-preset="$preset" \
        --deer-route="$route" \
        "$@" > "$file" 2>&1
    local code=$?
    if [ "$code" -eq 0 ] && grep -q '^\[DEER-RESULT\] PASS' "$file"; then
        echo "[$name] PASS"
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[DEER-(RESULT|METRIC|SCENARIO|DET)\]' "$file" | tail -30 | sed 's/^/    /'
        fail=1
    fi
}

final_hash() {
    grep '^\[DEER-DET\]' "$OUT/$1.txt" | tail -1 | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p'
}

# 三个正式预设的平地行为；original 同时覆盖双跑、40/400Hz 与 1mm 微扰。
run original-a  original flat 900 400 --deer-expect-hash="$HASH_ORIGINAL"
run original-b  original flat 900 400 --deer-expect-hash="$HASH_ORIGINAL"
run original-40 original flat 900 40  --deer-expect-hash="$HASH_ORIGINAL"
run perturb     original flat 900 400 --deer-perturb=0.001 --deer-expect-hash="$HASH_PERTURB"
run compact     compact  flat 900 400 --deer-expect-hash="$HASH_COMPACT"
run strider     strider  flat 900 400 --deer-expect-hash="$HASH_STRIDER"

# 地形、连续休息、击飞恢复及宿主目标/生命周期接缝。
run slope     original slope     900 400 --deer-expect-hash="$HASH_SLOPE"
run steps     original steps     900 400 --deer-expect-hash="$HASH_STEPS"
run wall      original wall      760 400 --deer-expect-hash="$HASH_WALL"
run turn      original turn      900 400 --deer-expect-hash="$HASH_TURN"
run reverse-original original reverse 900 400 --deer-expect-hash="$HASH_REVERSE_ORIGINAL"
run reverse-compact  compact  reverse 900 400 --deer-expect-hash="$HASH_REVERSE_COMPACT"
run reverse-strider  strider  reverse 900 400 --deer-expect-hash="$HASH_REVERSE_STRIDER"
run rough     original rough     900 400 --deer-expect-hash="$HASH_ROUGH"
run rest      original rest      520 400 --deer-expect-hash="$HASH_REST"
run launch    original launch    760 400 --deer-expect-hash="$HASH_LAUNCH"
run target    original target    760 400 --deer-expect-hash="$HASH_TARGET"
run lifecycle original lifecycle 760 400 --deer-expect-hash="$HASH_LIFECYCLE"

if diff <(grep '^\[DEER-DET\]' "$OUT/original-a.txt") \
        <(grep '^\[DEER-DET\]' "$OUT/original-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL：两次 original 的 [DEER-DET] 序列不一致"
    fail=1
fi

if diff <(grep '^\[DEER-DET\]' "$OUT/original-a.txt") \
        <(grep '^\[DEER-DET\]' "$OUT/original-40.txt") > /dev/null; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL：逻辑 tick 轨迹受宿主执行频率影响"
    fail=1
fi

original_hash="$(final_hash original-a)"
perturb_hash="$(final_hash perturb)"
compact_hash="$(final_hash compact)"
strider_hash="$(final_hash strider)"

if [ -n "$original_hash" ] && [ -n "$perturb_hash" ] \
        && [ "$original_hash" != "$perturb_hash" ]; then
    echo "[perturb] PASS（$original_hash -> $perturb_hash）"
else
    echo "[perturb] FAIL：1mm 微扰未改变哈希，或未能解析终值"
    fail=1
fi

if [ -n "$compact_hash" ] && [ -n "$strider_hash" ] \
        && [ "$original_hash" != "$compact_hash" ] \
        && [ "$original_hash" != "$strider_hash" ] \
        && [ "$compact_hash" != "$strider_hash" ]; then
    echo "[preset-difference] PASS（original=$original_hash compact=$compact_hash strider=$strider_hash）"
else
    echo "[preset-difference] FAIL：三个正式预设没有产生三个不同终态哈希"
    fail=1
fi

# 无引擎专项 smoke 自带固定哈希、行为门及失效注入门。
if dotnet run --no-build --no-restore --project core/deer_smoke > "$OUT/deer-smoke.txt" 2>&1; then
    echo "[deer-smoke] PASS"
else
    echo "[deer-smoke] FAIL"
    grep -E '^\[DEER-CORE-' "$OUT/deer-smoke.txt" | tail -30 | sed 's/^/    /'
    fail=1
fi

# 验证门本身必须有效：逐项关闭机制后，专项进程必须以 1 退出并明确报告 EXPECTED-FAIL。
for mechanism in support pair hesitation release balance stance antler bend; do
    ablation_file="$OUT/deer-ablate-$mechanism.txt"
    dotnet run --no-build --no-restore --project core/deer_smoke -- \
        "--ablate=$mechanism" > "$ablation_file" 2>&1
    code=$?
    if [ "$code" -eq 1 ] && grep -q 'EXPECTED-FAIL' "$ablation_file"; then
        echo "[ablate-$mechanism] PASS (expected exit=1)"
    else
        echo "[ablate-$mechanism] FAIL (exit=$code)"
        tail -10 "$ablation_file" | sed 's/^/    /'
        fail=1
    fi
done

if [ "$fail" -eq 0 ]; then
    echo "== DEER MATRIX GREEN =="
else
    echo "== DEER MATRIX RED =="
fi
exit "$fail"
