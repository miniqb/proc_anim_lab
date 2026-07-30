#!/bin/bash
# 蜘蛛专项矩阵：独立场景、独立哈希与真实换面断言。
# 任何一项缺少 [SPIDER-RESULT] PASS、哈希漂移或场景覆盖退化都会非零退出。
set -uo pipefail
export LC_ALL=C

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_spider_matrix}"
LOG="/private/tmp/proc_anim_spider_godot.log"
SCENE="res://scenes/spider_sandbox.tscn"

# 400 Hz 宿主加速、逻辑仍固定一 tick 的场景基线。初次调教完成后由本脚本钉住。
HASH_SMALL_COURSE=69DEB05AC320C4BC
HASH_LARGE_COURSE=66F861E565B58C4B
HASH_GAIT_LARGE=167D6AE8195E2E59
HASH_NARROW_SMALL=B58A9D1CBDA64D9E
HASH_NARROW_LARGE=4B8AFC2E950349F9
HASH_WALL_L=5A835682CDA69BCF
HASH_WALL_CEILING=9827B54E9EBF34F2
HASH_TURN_SMALL_LEFT=F170B2DA98847B67
HASH_TURN_LARGE_LEFT=A025C969931A2F6D
HASH_TURN_SMALL_RIGHT=B5F42B427FD8F9FA
HASH_TURN_LARGE_RIGHT=BF61849DE2058E4E
HASH_TURN_SMALL_AROUND=BE9AA7000EE3735E
HASH_TURN_LARGE_AROUND=89112B983B07CC73

mkdir -p "$OUT"
if ! dotnet build proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "SPIDER BUILD FAILED"
    tail -30 "$OUT/build.txt"
    exit 1
fi
if ! dotnet build core/spider_smoke/ProcAnim.Core.SpiderSmoke.csproj \
        > "$OUT/spider-smoke-build.txt" 2>&1; then
    echo "SPIDER SMOKE BUILD FAILED"
    tail -30 "$OUT/spider-smoke-build.txt"
    exit 1
fi

fail=0

# run <name> <期望哈希|-> <tick> <参数...>
run() {
    local name="$1" expect="$2" ticks="$3"
    shift 3
    local file="$OUT/$name.txt"
    local extra=()
    [ "$expect" != "-" ] && extra+=("--expect-hash=$expect")

    "$GODOT" --headless --rendering-method gl_compatibility \
        --rendering-driver opengl3 --path . --log-file "$LOG" \
        --fixed-fps 40 "$SCENE" -- --determinism="$ticks" \
        "${extra[@]+"${extra[@]}"}" "$@" > "$file" 2>&1
    local code=$?

    if grep -q '^\[SPIDER-RESULT\] PASS' "$file"; then
        if [ "$code" -eq 0 ]; then
            echo "[$name] PASS"
        else
            # Godot 4.7/macOS 偶发只在 teardown 崩溃；正式断言已在退出前打印。
            echo "[$name] PASS (teardown exit=$code)"
        fi
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[SPIDER-(METRIC|SCENARIO|NARROW|GAIT|TURN|RESULT|FINAL)\]' "$file" | sed 's/^/    /'
        fail=1
    fi
}

final_hash() {
    grep '^\[SPIDER-DET\]' "$OUT/$1.txt" | tail -1 | sed 's/.*hash=//'
}

# 标准路线：小/大蜘蛛各自覆盖地面、18° 斜坡、地墙内角、墙面、墙顶外角与顶面。
run small-course-a "$HASH_SMALL_COURSE" 2400 --tps=400 --route=course --breed=spider-small
run small-course-b "$HASH_SMALL_COURSE" 2400 --tps=400 --route=course --breed=spider-small
run small-course-40 "$HASH_SMALL_COURSE" 2400 --route=course --breed=spider-small
run small-perturb - 2400 --tps=400 --route=course --breed=spider-small --perturb=0.001
run large-course "$HASH_LARGE_COURSE" 2400 --tps=400 --route=course --breed=spider-large

# 大蜘蛛无障碍地面直行：每条腿必须持续参与换步，长期拖后比例不能退回旧表现。
run gait-large "$HASH_GAIT_LARGE" 500 --tps=400 --route=gait --breed=spider-large

# 平地急转：同一输入先沿 -X 走 120 tick，再左转、右转或 180° 掉头。
# 小/大体型都必须让每条腿完成一次本侧重植，并连续恢复足端、冻结 AEP、左右站距与身体朝向。
run turn-small-left "$HASH_TURN_SMALL_LEFT" 600 --tps=400 --route=turn --turn=left --breed=spider-small
run turn-large-left "$HASH_TURN_LARGE_LEFT" 600 --tps=400 --route=turn --turn=left --breed=spider-large
run turn-small-right "$HASH_TURN_SMALL_RIGHT" 600 --tps=400 --route=turn --turn=right --breed=spider-small
run turn-large-right "$HASH_TURN_LARGE_RIGHT" 600 --tps=400 --route=turn --turn=right --breed=spider-large
run turn-small-around "$HASH_TURN_SMALL_AROUND" 600 --tps=400 --route=turn --turn=around --breed=spider-small
run turn-large-around "$HASH_TURN_LARGE_AROUND" 600 --tps=400 --route=turn --turn=around --breed=spider-large

# 窄端面柱：稳定后多数腿抓稳，且至少四条腿同时抱住左右相邻侧面。
# 小/大型都跑，避免侧向抓点只在某一种腿长/身体尺度上成立。
run narrow-wall-small "$HASH_NARROW_SMALL" 1000 --tps=400 --route=narrow-wall --breed=spider-small
run narrow-wall-large "$HASH_NARROW_LARGE" 1000 --tps=400 --route=narrow-wall --breed=spider-large

# 两个独立换面几何：竖直墙—竖直墙 L 角，以及竖墙—天花板内角。
run wall-l "$HASH_WALL_L" 1200 --tps=400 --route=wall-l --breed=spider-small
run wall-ceiling "$HASH_WALL_CEILING" 1800 --tps=400 --route=wall-ceiling --breed=spider-small

if diff <(grep '^\[SPIDER-DET\]' "$OUT/small-course-a.txt") \
        <(grep '^\[SPIDER-DET\]' "$OUT/small-course-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL: 小蜘蛛双跑不一致"
    fail=1
fi

if [ "$(final_hash small-course-a)" = "$(final_hash small-course-40)" ]; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL: 宿主频率改变了固定 tick 结果"
    fail=1
fi

if [ "$(final_hash small-perturb)" != "$(final_hash small-course-a)" ]; then
    echo "[perturb] PASS"
else
    echo "[perturb] FAIL: 1mm 微扰未改变哈希"
    fail=1
fi

if dotnet run --project core/spider_smoke --no-build \
        > "$OUT/spider-smoke.txt" 2>&1; then
    echo "[spider-smoke] PASS"
else
    echo "[spider-smoke] FAIL"
    grep -E '^\[SPIDER-' "$OUT/spider-smoke.txt" | sed 's/^/    /'
    fail=1
fi

if [ "$fail" -eq 0 ]; then
    echo "== SPIDER MATRIX GREEN =="
else
    echo "== SPIDER MATRIX RED =="
fi
exit "$fail"
