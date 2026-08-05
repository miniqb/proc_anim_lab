#!/bin/bash
# DaddyLongLegs 独立 Godot 回归矩阵：可变 seed 形态、连续支撑、全向贴附、职责预算、
# 打断/外部目标/生命周期，以及无引擎 smoke 的机制消融。每项都以退出码和
# [DADDY-RESULT] 真断言判定，不复用其它物种沙盒。
set -uo pipefail
export LC_ALL=C

UPDATE_HASHES="${DADDY_UPDATE_HASHES:-0}"
case "$UPDATE_HASHES" in
    0|1) ;;
    *)
        echo "DADDY_UPDATE_HASHES must be 0 or 1 (got: $UPDATE_HASHES)"
        exit 2
        ;;
esac

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_daddy_long_legs_matrix}"
LOG="${OUT}/godot.log"
SCENE="res://scenes/daddy_long_legs_sandbox.tscn"

HASH_FLAT="FCC938B3D329B276"
HASH_PERTURB="9657299AD24335D4"
HASH_IDLE_START="BFBB3CA888FBDA27"
HASH_TAP="7F0F86B29732E549"
HASH_HEIGHT_RETENTION_SEED1="EA42857092EAD141"
HASH_HEIGHT_RETENTION_SEED33="C5FC57888C1B9B9D"
HASH_HEIGHT_RETENTION_SEED93="E119C5292F55EC4C"
HASH_SPARSE_GAIT="F549762BF1946CF1"
HASH_BROTHER="0BF2E03635391D10"
HASH_DADDY_SEED2="82997A92A1E835DD"
HASH_TERROR_SEED1="129DE0B95ADC049D"
HASH_TERROR_SEED7="D2C88F9E158B64A7"
HASH_COURSE="62929677D9BAF882"
HASH_WALL="59978F2841B22DFF"
HASH_WALL_IDLE="F512B2EC88BA8D1D"
HASH_CEILING="A84D01BA70B8481E"
HASH_CORNER="CB4EA4EE89AB4B55"
HASH_OUTER="8AFF2B3C0E4DA24D"
HASH_STUN="F6AF3992FDC4AFBC"
HASH_STUCK="9571A10382DCA7F9"
HASH_STUCK_SEED3="04C61682E6A5EB23"
HASH_STUCK_SEED4="2E970A61CEFF209F"
HASH_STUCK_SEED7="26EFE0D9C05F28EE"
HASH_STUCK_SEED93="B716BA68D8A292CD"
HASH_TARGET="88DF62ECD19328EF"
HASH_LAUNCH="3F6DD11D137EF75D"
HASH_LIFECYCLE="E780C63E72CAA66C"
HASH_TERROR_WALL="6D278DB824B59052"
HASH_TERROR_CEILING="3AF41B02ECB9B99A"
HASH_TERROR_CORNER="CC9DD724893CA50A"
HASH_TERROR_OUTER="1139B972C767B71E"
HASH_TERROR_STUCK="50E410DC7DD74A53"
HASH_BROTHER_WALL="FF204D02414A04F8"
HASH_BROTHER_CORNER="9323249FB7AA2A4B"

mkdir -p "$OUT"
if [ "$UPDATE_HASHES" -eq 1 ]; then
    echo "== DADDY HASH COLLECTION MODE =="
    echo "Godot expected hashes are intentionally omitted; behavioral assertions and exit codes remain active."
    echo "This mode is evidence collection only and does not replace a normal pinned-hash matrix run."
fi
if ! dotnet build --no-restore proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "DADDY LONG LEGS BUILD FAILED"
    tail -30 "$OUT/build.txt"
    exit 1
fi
if ! dotnet build --no-restore core/daddy_long_legs_smoke/ProcAnim.Core.DaddyLongLegsSmoke.csproj \
        > "$OUT/smoke-build.txt" 2>&1; then
    echo "DADDY LONG LEGS SMOKE BUILD FAILED"
    tail -30 "$OUT/smoke-build.txt"
    exit 1
fi

fail=0

# run <name> <preset> <seed> <route> <ticks> <tps> <expected-hash> [额外参数...]
run() {
    local name="$1" preset="$2" seed="$3" route="$4" ticks="$5" tps="$6" expected="$7"
    shift 7
    local file="$OUT/$name.txt"
    local daddy_args=(
        "--daddy-determinism=$ticks"
        "--daddy-tps=$tps"
        "--daddy-preset=$preset"
        "--daddy-seed=$seed"
        "--daddy-route=$route"
    )

    if [ "$UPDATE_HASHES" -eq 0 ]; then
        if [[ ! "$expected" =~ ^[0-9A-Fa-f]{16}$ ]]; then
            echo "[$name] FAIL: normal mode requires a pinned 16-hex expected hash (got: $expected)"
            fail=1
            return
        fi
        daddy_args+=("--daddy-expect-hash=$expected")
    fi
    daddy_args+=("$@")

    "$GODOT" --headless --rendering-method gl_compatibility \
        --rendering-driver opengl3 --path . --log-file "$LOG" \
        --fixed-fps 40 "$SCENE" -- \
        "${daddy_args[@]}" > "$file" 2>&1
    local code=$?

    if [ "$code" -eq 0 ] && grep -q '^\[DADDY-RESULT\] PASS' "$file"; then
        if [ "$UPDATE_HASHES" -eq 1 ]; then
            local actual
            actual="$(grep '^\[DADDY-DET\]' "$file" | tail -1 \
                | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p')"
            if [[ "$actual" =~ ^[0-9A-Fa-f]{16}$ ]]; then
                echo "[$name] COLLECT PASS hash=$actual"
            else
                echo "[$name] FAIL: behavior passed but no final 16-hex hash was emitted"
                fail=1
            fi
        else
            echo "[$name] PASS"
        fi
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[DADDY-(RESULT|METRIC|DET|SANDBOX)\]' "$file" \
            | tail -30 | sed 's/^/    /'
        fail=1
    fi
}

run_height_ablation() {
    local mechanism="$1" seed="$2"
    local name="height-ablate-$mechanism"
    local file="$OUT/$name.txt"
    "$GODOT" --headless --rendering-method gl_compatibility \
        --rendering-driver opengl3 --path . --log-file "$LOG" \
        --fixed-fps 40 "$SCENE" -- \
        --daddy-determinism=1800 --daddy-tps=400 --daddy-preset=daddy \
        "--daddy-seed=$seed" --daddy-route=height-retention \
        "--daddy-ablate=$mechanism" > "$file" 2>&1
    local code=$?
    if [ "$code" -eq 1 ] \
            && grep -q '^\[DADDY-HEIGHT-RETENTION\]' "$file" \
            && grep -q '^\[DADDY-RESULT\] FAIL' "$file"; then
        echo "[height-ablate-$mechanism] PASS (expected exit=1, seed=$seed)"
    else
        echo "[height-ablate-$mechanism] FAIL (exit=$code, seed=$seed)"
        grep -E '^\[DADDY-(RESULT|HEIGHT-RETENTION|METRIC|DET|SANDBOX)\]' "$file" \
            | tail -20 | sed 's/^/    /'
        fail=1
    fi
}

# 稀疏形态余量饥饿阀的 Godot 级消融：阀关闭后 sparse-gait 的进度/阀释放门必须变红。
run_sparse_ablation() {
    local mechanism="$1" seed="$2"
    local name="sparse-ablate-$mechanism"
    local file="$OUT/$name.txt"
    "$GODOT" --headless --rendering-method gl_compatibility \
        --rendering-driver opengl3 --path . --log-file "$LOG" \
        --fixed-fps 40 "$SCENE" -- \
        --daddy-determinism=2000 --daddy-tps=400 --daddy-preset=daddy \
        "--daddy-seed=$seed" --daddy-route=sparse-gait \
        "--daddy-ablate=$mechanism" > "$file" 2>&1
    local code=$?
    if [ "$code" -eq 1 ] \
            && grep -q '^\[DADDY-SPARSE-GAIT\]' "$file" \
            && grep -q '^\[DADDY-RESULT\] FAIL' "$file"; then
        echo "[sparse-ablate-$mechanism] PASS (expected exit=1, seed=$seed)"
    else
        echo "[sparse-ablate-$mechanism] FAIL (exit=$code, seed=$seed)"
        grep -E '^\[DADDY-(RESULT|SPARSE-GAIT|METRIC|DET|SANDBOX)\]' "$file" \
            | tail -20 | sed 's/^/    /'
        fail=1
    fi
}

det_stream() {
    grep '^\[DADDY-DET\]' "$OUT/$1.txt"
}

final_hash() {
    det_stream "$1" | tail -1 | sed -n 's/.*hash=\([0-9A-Fa-f]*\).*/\1/p'
}

morph_signature() {
    grep '^\[DADDY-METRIC\]' "$OUT/$1.txt" | tail -1 \
        | sed -n 's/.*morph=\([^ ]*\).*/\1/p'
}

# 同预设/seed 双跑、40/400Hz 子步、1mm 初态微扰。
run flat-a  daddy 1 flat 800 400 "$HASH_FLAT"
run flat-b  daddy 1 flat 800 400 "$HASH_FLAT"
run flat-40 daddy 1 flat 800 40  "$HASH_FLAT"
run perturb daddy 1 flat 800 400 "$HASH_PERTURB" --daddy-perturb=0.001
run idle-start daddy 1 idle-start 500 400 "$HASH_IDLE_START"
run tap        daddy 1 tap        800 400 "$HASH_TAP"

# 同个体先建立稳定高站姿，再持续水平移动；三个 seed 覆盖不同身体/触手图。
run height-retention-seed1  daddy 1  height-retention 1800 400 \
    "$HASH_HEIGHT_RETENTION_SEED1"
run height-retention-seed33 daddy 33 height-retention 1800 400 \
    "$HASH_HEIGHT_RETENTION_SEED33"
run height-retention-seed93 daddy 93 height-retention 1800 400 \
    "$HASH_HEIGHT_RETENTION_SEED93"
run_height_ablation surface-span-replant 1
run_height_ablation support-response-3d 1

# 稀疏形态（daddy seed 5：5 触手、部分永久够不到地）依赖余量饥饿阀维持连续步态；
# 计数门节流与在途落点追踪的行为红灯由无引擎 smoke 消融钉住（平地密集形态下
# 二者与余量门互为冗余，Godot 物理面不可靠区分）。
run sparse-gait daddy 5 sparse-gait 2000 400 "$HASH_SPARSE_GAIT"
run_sparse_ablation starvation-valve 5

# 三个正式预设与四个不同 seed 形态；包含 4-body 小型和 12-body 大型实例。
run brother-seed1 brother 1 flat 800 400 "$HASH_BROTHER"
run daddy-seed2  daddy   2 flat 800 400 "$HASH_DADDY_SEED2"
run terror-seed1 terror  1 flat 800 400 "$HASH_TERROR_SEED1"
run terror-seed7 terror  7 flat 800 400 "$HASH_TERROR_SEED7"
run terror-seed7-b  terror 7 flat 800 400 "$HASH_TERROR_SEED7"
run terror-seed7-40 terror 7 flat 800 40  "$HASH_TERROR_SEED7"

# 真实 Jolt collider：平地/坡、墙、天花板、内角墙→顶、外角顶→外墙→下行。
run course   daddy 1 course   1600 400 "$HASH_COURSE"
run wall     daddy 1 wall     1600 400 "$HASH_WALL"
run wall-idle daddy 1 wall-idle 1200 400 "$HASH_WALL_IDLE"
run ceiling  daddy 1 ceiling  1600 400 "$HASH_CEILING"
run corner   daddy 1 corner   2200 400 "$HASH_CORNER"
run outer    daddy 1 outer    2200 400 "$HASH_OUTER"

# 最大形态也必须通过真实 Jolt 多面地形；wall 另做双跑与 40/400Hz。
run terror-wall     terror 1 wall    1600 400 "$HASH_TERROR_WALL"
run terror-wall-b   terror 1 wall    1600 400 "$HASH_TERROR_WALL"
run terror-wall-40  terror 1 wall    1600 40  "$HASH_TERROR_WALL"
run terror-ceiling  terror 1 ceiling 1600 400 "$HASH_TERROR_CEILING"
run terror-corner   terror 1 corner  2200 400 "$HASH_TERROR_CORNER"
run terror-outer    terror 1 outer   2200 400 "$HASH_TERROR_OUTER"
run terror-stuck    terror 1 stuck   1800 400 "$HASH_TERROR_STUCK"

# 小型形态也不得只在平地恰好可行。
run brother-wall    brother 1 wall   1600 400 "$HASH_BROTHER_WALL"
run brother-corner  brother 1 corner 2200 400 "$HASH_BROTHER_CORNER"

# 单触手接管、卡住脱困、外部够取/拉扯、击飞恢复与三生命周期 API。
run stun      daddy 1 stun      800 400 "$HASH_STUN"
run stuck        daddy 1  stuck 1800 400 "$HASH_STUCK"
run stuck-seed3  daddy 3  stuck 1800 400 "$HASH_STUCK_SEED3"
run stuck-seed4  daddy 4  stuck 1800 400 "$HASH_STUCK_SEED4"
run stuck-seed7  daddy 7  stuck 1800 400 "$HASH_STUCK_SEED7"
run stuck-seed93 daddy 93 stuck 1800 400 "$HASH_STUCK_SEED93"
run target    daddy 1 target    700 400 "$HASH_TARGET"
run launch    daddy 1 launch    800 400 "$HASH_LAUNCH"
run lifecycle daddy 1 lifecycle 600 400 "$HASH_LIFECYCLE"

if diff <(det_stream flat-a) <(det_stream flat-b) > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL: 同参数/seed 双跑的逐点哈希不一致"
    fail=1
fi

if diff <(det_stream flat-a) <(det_stream flat-40) > /dev/null; then
    echo "[40-vs-400] PASS"
else
    echo "[40-vs-400] FAIL: 固定 40Hz 逻辑轨迹受宿主执行频率影响"
    fail=1
fi

if diff <(det_stream terror-seed7) <(det_stream terror-seed7-b) > /dev/null; then
    echo "[terror-double-run] PASS"
else
    echo "[terror-double-run] FAIL: 12-body Terror 同参数/seed 双跑不一致"
    fail=1
fi

if diff <(det_stream terror-seed7) <(det_stream terror-seed7-40) > /dev/null; then
    echo "[terror-40-vs-400] PASS"
else
    echo "[terror-40-vs-400] FAIL: 12-body Terror 轨迹受宿主执行频率影响"
    fail=1
fi

if diff <(det_stream terror-wall) <(det_stream terror-wall-b) > /dev/null; then
    echo "[terror-wall-double-run] PASS"
else
    echo "[terror-wall-double-run] FAIL: Terror wall 同参数/seed 双跑不一致"
    fail=1
fi

if diff <(det_stream terror-wall) <(det_stream terror-wall-40) > /dev/null; then
    echo "[terror-wall-40-vs-400] PASS"
else
    echo "[terror-wall-40-vs-400] FAIL: Terror wall 轨迹受宿主执行频率影响"
    fail=1
fi

base_hash="$(final_hash flat-a)"
perturb_hash="$(final_hash perturb)"
if [ -n "$base_hash" ] && [ -n "$perturb_hash" ] && [ "$base_hash" != "$perturb_hash" ]; then
    echo "[perturb] PASS ($base_hash -> $perturb_hash)"
else
    echo "[perturb] FAIL: 1mm 微扰未改变哈希，或无法解析终值"
    fail=1
fi

morphs="$(for name in brother-seed1 daddy-seed2 terror-seed1 terror-seed7; do
    morph_signature "$name"
done)"
morph_count="$(printf '%s\n' "$morphs" | sed '/^$/d' | sort -u | wc -l | tr -d ' ')"
if [ "$morph_count" -eq 4 ] && printf '%s\n' "$morphs" | grep -q '^4/' \
        && printf '%s\n' "$morphs" | grep -q '^12/'; then
    echo "[morphology-coverage] PASS ($(printf '%s' "$morphs" | tr '\n' ' '))"
else
    echo "[morphology-coverage] FAIL: 未覆盖四种不同形态及 4/12-body 两端"
    fail=1
fi

# 无引擎专项 smoke 钉住形态/物理/接口的更小单元行为与固定哈希。
if dotnet run --no-build --no-restore --project core/daddy_long_legs_smoke \
        > "$OUT/daddy-smoke.txt" 2>&1; then
    echo "[daddy-smoke] PASS"
else
    echo "[daddy-smoke] FAIL"
    grep -E '^\[DADDY-CORE-' "$OUT/daddy-smoke.txt" | tail -40 | sed 's/^/    /'
    fail=1
fi

# 验证门自身有效：逐项关闭对应机制后，专项进程必须明确以 EXPECTED-FAIL 退出 1。
for mechanism in support support-lift allocation independent-duty directional-drive \
        step search-expansion stuck-recovery \
        stuck-jitter stun-limp external-pull segment-adhesion residual-terrain \
        terrain-backtrack \
        grip-discrimination step-peel slack-guide start-replant \
        idle-landing-stability idle-support-neutrality \
        step-support-reserve surface-span-replant release-throttle \
        moving-retarget starvation-valve guide-arch \
        support-response-3d; do
    ablation_file="$OUT/daddy-ablate-$mechanism.txt"
    dotnet run --no-build --no-restore --project core/daddy_long_legs_smoke -- \
        "--ablate=$mechanism" > "$ablation_file" 2>&1
    code=$?
    if [ "$code" -eq 1 ] && grep -q 'EXPECTED-FAIL' "$ablation_file"; then
        echo "[ablate-$mechanism] PASS (expected exit=1)"
    else
        echo "[ablate-$mechanism] FAIL (exit=$code)"
        tail -12 "$ablation_file" | sed 's/^/    /'
        fail=1
    fi
done

if [ "$fail" -eq 0 ]; then
    if [ "$UPDATE_HASHES" -eq 1 ]; then
        echo "== DADDY HASH COLLECTION COMPLETE (40 Godot configurations + 30 ablations; hashes not pinned) =="
    else
        echo "== DADDY LONG LEGS MATRIX GREEN (40 Godot configurations + 30 ablations) =="
    fi
else
    if [ "$UPDATE_HASHES" -eq 1 ]; then
        echo "== DADDY HASH COLLECTION RED (one or more behavioral assertions failed) =="
    else
        echo "== DADDY LONG LEGS MATRIX RED =="
    fi
fi
exit "$fail"
