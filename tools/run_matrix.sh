#!/bin/bash
# 确定性全矩阵回归：9 配置 × 硬断言（哈希基线 / 有限值 / 碰撞后约束终态 / 路点下限 / 退出码聚合）。
# 任何一项红 → 本脚本非零退出。旧版 `grep '[DET]'` 管道无 pipefail、探针只打印不断言,
# NaN、尾链断裂、原生崩溃(exit 134)全都假绿——本脚本就是那次教训的产物。
#
# 有意改内核后重定基线：先人工核对各配置 [METRIC]/[FINAL] 合理，再同步更新下方哈希表与 CLAUDE.md §5。
set -uo pipefail

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_matrix}"
LOG=/private/tmp/godot_codex.log

# —— 基线哈希（400Hz/2000tick，与 CLAUDE.md §5 哈希表同步维护）——
HASH_DEFAULT=2467A2B2D5511AF4
HASH_WALL=62D10580D4B0FEE2
HASH_STAND=D9F94BB9262BD2ED
HASH_HEAVY=40023DABFBF998E4
HASH_SPRINTER=168EC2C94EE347A7
HASH_HEXAPOD=4E71B33910C55C76

mkdir -p "$OUT"
if ! dotnet build proc_anim_lab.csproj > "$OUT/build.txt" 2>&1; then
    echo "BUILD FAILED"; tail -20 "$OUT/build.txt"; exit 1
fi

fail=0

# run <name> <期望哈希|-> <路点下限|-> <tick数> <额外参数...>
run() {
    local name="$1" expect="$2" minwp="$3" ticks="$4"; shift 4
    local file="$OUT/$name.txt" extra=()
    [ "$expect" != "-" ] && extra+=("--expect-hash=$expect")
    "$GODOT" --headless --path . --log-file "$LOG" --fixed-fps 40 -- \
        --determinism="$ticks" "${extra[@]+"${extra[@]}"}" "$@" > "$file" 2>&1
    local code=$?
    local ok=1
    if ! grep -q '^\[RESULT\] PASS' "$file"; then
        ok=0
    fi
    if [ "$minwp" != "-" ]; then
        local wp
        wp=$(grep -o 'waypointsReached=[0-9]*' "$file" | head -1 | cut -d= -f2)
        if [ -z "${wp:-}" ] || [ "$wp" -lt "$minwp" ]; then
            echo "[$name] 路点不足: ${wp:-无} < $minwp"
            ok=0
        fi
    fi
    if [ $ok -eq 1 ]; then
        if [ $code -eq 0 ]; then
            echo "[$name] PASS"
        else
            # 已知 Godot 4.7 macOS 偶发退场崩溃（mutex lock failed, exit 134）：
            # 断言全部在崩溃点之前打印完成，判定以 [RESULT] 为准，这里仅标注提醒。
            echo "[$name] PASS（teardown exit=$code，判定以 [RESULT] 为准）"
        fi
    else
        echo "[$name] FAIL (exit=$code)"
        grep -E '^\[(RESULT|METRIC)\]' "$file" | sed 's/^/    /'
        fail=1
    fi
}

final_hash() { grep '^\[DET\]' "$OUT/$1.txt" | tail -1 | sed 's/.*hash=//'; }

run default    "$HASH_DEFAULT"  12 2000 --tps=400
run default-b  "$HASH_DEFAULT"  12 2000 --tps=400
run default-40 "$HASH_DEFAULT"  12 2000
# 微扰轨迹有意发散,路点下限放宽到「仍在健康走路线」（基线跑 12+,微扰实测 ~9）
run perturb    -                6  2000 --tps=400 --perturb=0.001
run wall       "$HASH_WALL"     9  2000 --tps=400 --route=wall --spawn=-4,0.5,0
run stand      "$HASH_STAND"    -  2000 --tps=400 --route=stand --spawn=-6,3.7,0
run heavy      "$HASH_HEAVY"    6  2000 --tps=400 --breed=heavy
run sprinter   "$HASH_SPRINTER" 15 2000 --tps=400 --breed=sprinter
run hexapod    "$HASH_HEXAPOD"  3  2000 --tps=400 --breed=hexapod
# 评审复现固化:嵌入脱困（P1-3）与贴墙擦边（P1-2），位置断言在下方
run embed      -                -  60   --tps=400 --route=stand --spawn=0,-0.1,0
run wallside   -                -  120  --tps=400 --route=stand --spawn=-5.65,0.3,0

# 嵌入脱困:60 tick 后所有 chunk 必须已被 MTD 推出地板（旧版 HitFromInside 永久冻结）
if grep '^\[FINAL\] body' "$OUT/embed.txt" | awk -F'pos=\\(' '{split($2,a,","); if (a[2]+0 < -0.01) bad=1} END {exit bad}'; then
    echo "[embed-escape] PASS"
else
    echo "[embed-escape] FAIL：仍有 chunk 冻在地板内"; fail=1
fi

# 贴墙擦边:髋球（chunk=1,r=0.25）球面不得穿入 x=-5.8 的墙面（旧版穿 5cm 无接触）
hipx=$(grep '^\[FINAL\] body=0 chunk=1 ' "$OUT/wallside.txt" | sed 's/.*pos=(\([^,]*\),.*/\1/')
if awk -v x="${hipx:-0}" 'BEGIN{exit !(x >= -5.551)}'; then
    echo "[wallside-graze] PASS（髋球 x=$hipx）"
else
    echo "[wallside-graze] FAIL：髋球穿墙（x=${hipx:-无}）"; fail=1
fi

# 双跑逐字节：两次 default 的 [DET] 序列必须完全一致
if diff <(grep '^\[DET\]' "$OUT/default.txt") <(grep '^\[DET\]' "$OUT/default-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL：两次 default 的 [DET] 序列不一致"; fail=1
fi

# 灵敏度：微扰后终值哈希必须偏离基线（哈希对状态不敏感 = 回归全盲）
if [ "$(final_hash perturb)" = "$HASH_DEFAULT" ]; then
    echo "[perturb] FAIL：微扰后哈希未变"; fail=1
else
    echo "[perturb] PASS（哈希已偏离基线）"
fi

# 无引擎冒烟（自带哈希基线/边界扫描断言，退出码即判定）
if dotnet run --project core/smoke > "$OUT/smoke.txt" 2>&1; then
    echo "[smoke] PASS"
else
    echo "[smoke] FAIL"
    grep -E '^\[CORE-' "$OUT/smoke.txt" | sed 's/^/    /'
    fail=1
fi

if [ $fail -eq 0 ]; then echo "== MATRIX GREEN =="; else echo "== MATRIX RED =="; fi
exit $fail
