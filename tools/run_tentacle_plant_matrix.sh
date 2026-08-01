#!/bin/bash
# TentaclePlant 专项 Godot 回归矩阵：独立场景、独立 CLI、宿主 mock prey 和硬行为门。
# 每项以场景在 teardown 前打印的 [TENTACLE-PLANT-RESULT] 为准。
set -uo pipefail
export LC_ALL=C

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_tentacle_plant_matrix}"
LOG="${OUT}/godot.log"
SCENE="res://scenes/tentacle_plant_sandbox.tscn"
SEED="0x54454E5441434C45"
HASH_ORIGINAL_IDLE=8804DD4C88BC6FAD
HASH_ORIGINAL_WALL_IDLE=1EAD6114DE60A39B
HASH_ORIGINAL_CEILING_IDLE=DCBC701336431CA1
HASH_ORIGINAL_HIT=7BD562F0B2D1190A
HASH_ORIGINAL_MISS=8C3E64D51A418194
HASH_ORIGINAL_OCCLUDED=AB0D36CFF9433ECC
HASH_SHORT_CEILING_HIT=8845630C7EB196F1
HASH_HUNTER_WALL_HIT=CD1DF3995CD2DB74
HASH_SHORT_FLOOR_IDLE=6E0302451CA7AA76
HASH_HUNTER_FLOOR_IDLE=DCBDE94A15C958B3

mkdir -p "$OUT"
if ! dotnet build proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "TENTACLE PLANT BUILD FAILED"
    tail -30 "$OUT/build.txt"
    exit 1
fi
if ! dotnet build core/tentacle_plant_smoke/ProcAnim.Core.TentaclePlantSmoke.csproj \
        > "$OUT/smoke-build.txt" 2>&1; then
    echo "TENTACLE PLANT SMOKE BUILD FAILED"
    tail -30 "$OUT/smoke-build.txt"
    exit 1
fi

fail=0

# run <name> <preset> <mount> <route> <ticks> <tps> [额外参数...]
run() {
    local name="$1" preset="$2" mount="$3" route="$4" ticks="$5" tps="$6"
    shift 6
    local file="$OUT/$name.txt"

    "$GODOT" --headless --rendering-method gl_compatibility \
        --rendering-driver opengl3 --path . --log-file "$LOG" \
        --fixed-fps 40 "$SCENE" -- \
        --plant-determinism="$ticks" \
        --plant-tps="$tps" \
        --plant-seed="$SEED" \
        --plant-preset="$preset" \
        --plant-mount="$mount" \
        --plant-route="$route" \
        "$@" > "$file" 2>&1
    local code=$?

    if [ "$code" -eq 0 ] &&
            grep -q '^\[TENTACLE-PLANT-RESULT\] PASS' "$file"; then
        echo "[$name] PASS"
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[TENTACLE-PLANT-(RESULT|METRIC|DET|SANDBOX)\]' "$file" \
            | tail -30 | sed 's/^/    /'
        fail=1
    fi
}

det_stream() {
    grep '^\[TENTACLE-PLANT-DET\]' "$OUT/$1.txt"
}

final_hash() {
    det_stream "$1" | tail -1 | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p'
}

# 原型无猎物游走：双跑、40/400 Hz、1mm 微扰。
run original-idle-a  tentacle-plant/original floor idle 900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_IDLE"
run original-idle-b  tentacle-plant/original floor idle 900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_IDLE"
run original-idle-40 tentacle-plant/original floor idle 900 40 \
    --plant-expect-hash="$HASH_ORIGINAL_IDLE"
run original-perturb tentacle-plant/original floor idle 900 400 --plant-perturb=0.001

# 同一原型三种安装 frame；核心不允许偷读世界 Up。
run original-wall-idle    tentacle-plant/original wall    idle 900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_WALL_IDLE"
run original-ceiling-idle tentacle-plant/original ceiling idle 900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_CEILING_IDLE"

# 攻击行为：抓中、扑空后回收，以及隔墙绝不远程抓取。
run original-hit      tentacle-plant/original floor hit      900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_HIT"
run original-hit-40   tentacle-plant/original floor hit      900 40 \
    --plant-expect-hash="$HASH_ORIGINAL_HIT"
run original-miss     tentacle-plant/original floor miss     900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_MISS"
run original-occluded tentacle-plant/original floor occluded 900 400 \
    --plant-expect-hash="$HASH_ORIGINAL_OCCLUDED"

# 两个参数变体走不同安装面和完整抓持路径。
run short-ceiling-hit tentacle-plant/short  ceiling hit 900 400 \
    --plant-expect-hash="$HASH_SHORT_CEILING_HIT"
run hunter-wall-hit   tentacle-plant/hunter wall    hit 900 400 \
    --plant-expect-hash="$HASH_HUNTER_WALL_HIT"
# hunter 长度球边缘：中心略越过 9m，但半径 0.16m 的猎物表面仍在可达包络内。
# hit 钉住真实抓取/吞入，miss 钉住首次扑击时序；旧中心点 gate 两项都会永久红灯。
run hunter-wall-edge-hit tentacle-plant/hunter wall hit 450 400 \
    --plant-target-local=9,0.9,0.45
run hunter-wall-edge-miss tentacle-plant/hunter wall miss 450 400 \
    --plant-target-local=9,0.9,0.45
# 同环境再跑一组，避免“预设差异”其实只来自安装 frame。
run short-floor-idle  tentacle-plant/short  floor idle 900 400 \
    --plant-expect-hash="$HASH_SHORT_FLOOR_IDLE"
run hunter-floor-idle tentacle-plant/hunter floor idle 900 400 \
    --plant-expect-hash="$HASH_HUNTER_FLOOR_IDLE"

if diff <(det_stream original-idle-a) \
        <(det_stream original-idle-b) > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL: 同 seed 双跑的逐点哈希不一致"
    fail=1
fi

if diff <(det_stream original-idle-a) \
        <(det_stream original-idle-40) > /dev/null; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL: 固定 tick 轨迹受宿主执行频率影响"
    fail=1
fi

if diff <(det_stream original-hit) \
        <(det_stream original-hit-40) > /dev/null; then
    echo "[hit-40-vs-400] PASS"
else
    echo "[hit-40-vs-400] FAIL: 攻击/TargetEffect 固定 tick 轨迹受宿主执行频率影响"
    fail=1
fi

base_hash="$(final_hash original-idle-a)"
perturb_hash="$(final_hash original-perturb)"
short_hash="$(final_hash short-floor-idle)"
hunter_hash="$(final_hash hunter-floor-idle)"
if [ -n "$base_hash" ] && [ -n "$perturb_hash" ] && \
        [ "$base_hash" != "$perturb_hash" ]; then
    echo "[perturb] PASS ($base_hash -> $perturb_hash)"
else
    echo "[perturb] FAIL: 1mm 初态微扰未改变哈希，或无法解析终值"
    fail=1
fi

if [ -n "$short_hash" ] && [ -n "$hunter_hash" ] && \
        [ "$short_hash" != "$hunter_hash" ]; then
    echo "[preset-difference] PASS (short=$short_hash hunter=$hunter_hash)"
else
    echo "[preset-difference] FAIL: 两个变体没有可观测差异，或无法解析终值"
    fail=1
fi

# 纯 .NET smoke 钉住内核固定哈希和无引擎边界；Godot 矩阵负责真实 collider 接缝。
if dotnet run --no-restore --project core/tentacle_plant_smoke \
        > "$OUT/tentacle-plant-smoke.txt" 2>&1; then
    echo "[tentacle-plant-smoke] PASS"
else
    echo "[tentacle-plant-smoke] FAIL"
    grep -E '^\[TENTACLE-PLANT-' "$OUT/tentacle-plant-smoke.txt" | sed 's/^/    /'
    fail=1
fi

if [ "$fail" -eq 0 ]; then
    echo "== TENTACLE PLANT MATRIX GREEN =="
else
    echo "== TENTACLE PLANT MATRIX RED =="
fi
exit "$fail"
