#!/bin/bash
# 确定性全矩阵回归：28 配置 × 硬断言（哈希基线 / 有限值 / 深断裂连跑 / 释放 churn / 路点下限 /
# 位置检查 / 退出码聚合）。任何一项红 → 本脚本非零退出。旧版 `grep '[DET]'` 管道无 pipefail、
# 探针只打印不断言, NaN、尾链断裂、原生崩溃(exit 134)全都假绿——本脚本就是那次教训的产物。
# 蜥蜴 20 配置 + 人形（--species=humanoid）8 配置：巡逻/双跑/40Hz 时基/击飞爬起/昏迷苏醒/
# 动作脚本/两变体。
#
# 有意改内核后重定基线：先人工核对各配置 [METRIC]/[FINAL] 合理，再同步更新下方哈希表与
# core/smoke/Program.cs 的 ExpectedHash（基线哈希只存这两处，别处一律引用不复制），
# 并按新参考值回顾一遍路点下限（下限 ≈ 参考值的 75~80%，历史上曾停在修复前旧值——终审 C16）。
set -uo pipefail
export LC_ALL=C   # awk/sed 的数值解析钉死 C locale（C# 侧输出已是 InvariantCulture，双保险）

GODOT="${GODOT:-/Applications/Godot_mono.app/Contents/MacOS/Godot}"
cd "$(dirname "$0")/.."
OUT="${1:-/private/tmp/proc_anim_matrix}"
LOG=/private/tmp/godot_codex.log

# —— 基线哈希（400Hz/2000tick；真相源两处之一，另一处是 smoke 的 ExpectedHash）——
HASH_DEFAULT=EE1CD7D5CB648C9B
HASH_WALL=7A8169F9D9389F86
HASH_STAND=D9F94BB9262BD2ED
HASH_HEAVY=8315CD340EE9C580
HASH_SPRINTER=60BF6780B4011C22
HASH_HEXAPOD=16815326C023AC80
HASH_CARROT=F03E9DA79674A7C4
HASH_WALL_HEAVY=7A5D1D36900F6440
HASH_WALL_HEXAPOD=E92282BB5EBB4CF2
HASH_TURN_HEXAPOD=0204E3BB5F8DEA7F
HASH_WALL_TURN_HEXAPOD=4CBAA86B86D0D0AE
HASH_CARROT_TURN_HEAVY=3771A59BBEA8BFC2
HASH_CARROT_TURN_HEXAPOD=5242DACF5544403E
HASH_WALL_TAIL=4E84FC3E4EBA1BE3
HASH_WALL_CORNER=CCCFDC1A3D452797
# —— 人形（HumanoidLocomotionController；smoke 侧对应 HumanoidExpectedHash）——
HASH_HUMANOID=5CF8E13698D5B063
HASH_HUMANOID_YANK=EF36380038C24A6C
HASH_HUMANOID_STUN=6628D23755468053
HASH_HUMANOID_ACT=798C7D211B0DC39B
HASH_HUMANOID_BRUTE=22EAD0333D21F7AA
HASH_HUMANOID_WAIF=DB623B44578EF033

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
        grep -E '^\[(RESULT|METRIC|SCENARIO)\]' "$file" | sed 's/^/    /'
        fail=1
    fi
}

final_hash() { grep '^\[DET\]' "$OUT/$1.txt" | tail -1 | sed 's/.*hash=//'; }

# 路点下限取当前参考值约 75~80%；小基数向紧取整（heavy 当前参考 7 → 下限 7），
# 微扰轨迹有意发散，取更宽的「仍在健康走路线」下限。
run default    "$HASH_DEFAULT"  9  2000 --tps=400
run default-b  "$HASH_DEFAULT"  9  2000 --tps=400
run default-40 "$HASH_DEFAULT"  9  2000
run perturb    -                6  2000 --tps=400 --perturb=0.001
run wall       "$HASH_WALL"     11 2000 --tps=400 --route=wall --spawn=-4,0.5,0
# 评审 P1 复现固化:三节脊柱贴墙——之前只有二节 default 跑过 wall 路线，heavy/hexapod
# 从未在实际墙面几何下验证过姿态，讲白了矩阵曾经"绿"是因为压根没跑过这个组合。
run wall-heavy   "$HASH_WALL_HEAVY"   7  2000 --tps=400 --route=wall --spawn=-4,0.5,0 --breed=heavy
run wall-hexapod "$HASH_WALL_HEXAPOD" 10 2000 --tps=400 --route=wall --spawn=-4,0.5,0 --breed=hexapod
# 复合 wall 路线拆出的事件相对回归：平地/墙面 180° 掉头、尾链释放后恢复、首次目标墙换面。
# 不依赖历史绝对 tick；[SCENARIO] 的覆盖与耗时均由 SandboxWorld 真断言。
run turn-hexapod "$HASH_TURN_HEXAPOD" 12 800 --tps=400 --route=turn --spawn=0,0.5,5 --breed=hexapod
run wall-turn-hexapod "$HASH_WALL_TURN_HEXAPOD" - 400 --tps=400 --route=wall-turn --spawn=-4,0.5,0 --breed=hexapod
# 行进中把 External 胡萝卜侧转约 90°（实际方向点积门控）：头前恢复是真断言；中段角度领先的峰值/时长
# 先保留为可读指标，供后续判断是否需要偏离 RW 式软体相位差。
run carrot-turn-heavy "$HASH_CARROT_TURN_HEAVY" - 800 --tps=400 --route=carrot-turn --spawn=0,0.5,5 --breed=heavy
run carrot-turn-hexapod "$HASH_CARROT_TURN_HEXAPOD" - 800 --tps=400 --route=carrot-turn --spawn=0,0.5,5 --breed=hexapod
run wall-tail    "$HASH_WALL_TAIL"    2 650 --tps=400 --route=wall-tail --spawn=-4,0.5,0 --breed=hexapod
run wall-corner  "$HASH_WALL_CORNER"  - 180 --tps=400 --route=wall-corner --spawn=-4,0.5,0 --breed=hexapod
run stand      "$HASH_STAND"    -  2000 --tps=400 --route=stand --spawn=-6,3.7,0
run carrot     "$HASH_CARROT"   20 2000 --tps=400 --route=carrot
run heavy      "$HASH_HEAVY"    7  2000 --tps=400 --breed=heavy
run sprinter   "$HASH_SPRINTER" 12 2000 --tps=400 --breed=sprinter
run hexapod    "$HASH_HEXAPOD"  8  2000 --tps=400 --breed=hexapod
# 评审复现固化:嵌入脱困（P1-3）与贴墙擦边（P1-2），位置断言在下方
run embed      -                -  60   --tps=400 --route=stand --spawn=0,-0.1,0
run wallside   -                -  120  --tps=400 --route=stand --spawn=-5.65,0.3,0

# —— 人形物种（[RESULT] 判定在 HumanoidSandboxDriver.DumpFinalState）——
# hwalk = 坡→平地→跨台阶三点巡逻；yank = 行进中击飞后限时回正续走；stun = 昏迷瘫倒+苏醒爬起；
# act = 指向→持物→蓄力停驶→出手 动作脚本；brute/waif = 品种变体各自巡逻。
run humanoid       "$HASH_HUMANOID"       20 2000 --tps=400 --species=humanoid --route=hwalk
run humanoid-b     "$HASH_HUMANOID"       20 2000 --tps=400 --species=humanoid --route=hwalk
run humanoid-40    "$HASH_HUMANOID"       20 2000 --species=humanoid --route=hwalk
run humanoid-yank  "$HASH_HUMANOID_YANK"  19 2000 --tps=400 --species=humanoid --route=hwalk --yank=600
run humanoid-stun  "$HASH_HUMANOID_STUN"  -  1200 --tps=400 --species=humanoid --stun=500,120
run humanoid-act   "$HASH_HUMANOID_ACT"   -  900  --tps=400 --species=humanoid --route=hact
run humanoid-brute "$HASH_HUMANOID_BRUTE" 17 2000 --tps=400 --species=humanoid --route=hwalk --breed=brute
run humanoid-waif  "$HASH_HUMANOID_WAIF"  25 2000 --tps=400 --species=humanoid --route=hwalk --breed=waif

# 嵌入脱困:60 tick 后所有 chunk 必须已被 MTD 推出地板（旧版 HitFromInside 永久冻结）
if grep '^\[FINAL\] body' "$OUT/embed.txt" | awk -F'pos=\\(' '{split($2,a,","); if (a[2]+0 < -0.01) bad=1} END {exit bad}'; then
    echo "[embed-escape] PASS"
else
    echo "[embed-escape] FAIL：仍有 chunk 冻在地板内"; fail=1
fi

# 贴墙擦边:髋球（chunk=1,r=0.25）球面不得穿入 x=-5.8 的墙面（旧版穿 5cm 无接触）。
# 抓取失败/非数值必须红——曾经 ${hipx:-0} 让空值以 0 参赛恒 PASS（终审 C4）。
hipx=$(grep '^\[FINAL\] body=0 chunk=1 ' "$OUT/wallside.txt" | sed -n 's/.*pos=(\(-\{0,1\}[0-9][0-9.]*\),.*/\1/p')
if [ -z "$hipx" ]; then
    echo "[wallside-graze] FAIL：未能从 [FINAL] 提取髋球 x（格式漂移/chunk 索引变化）"; fail=1
elif awk -v x="$hipx" 'BEGIN{exit !(x >= -5.551)}'; then
    echo "[wallside-graze] PASS（髋球 x=$hipx）"
else
    echo "[wallside-graze] FAIL：髋球穿墙（x=$hipx）"; fail=1
fi

# 双跑逐字节：两次 default 的 [DET] 序列必须完全一致
if diff <(grep '^\[DET\]' "$OUT/default.txt") <(grep '^\[DET\]' "$OUT/default-b.txt") > /dev/null; then
    echo "[double-run] PASS"
else
    echo "[double-run] FAIL：两次 default 的 [DET] 序列不一致"; fail=1
fi

# 人形双跑逐字节（哈希折叠含手臂：chunks → legs → arms）
if diff <(grep '^\[DET\]' "$OUT/humanoid.txt") <(grep '^\[DET\]' "$OUT/humanoid-b.txt") > /dev/null; then
    echo "[humanoid-double-run] PASS"
else
    echo "[humanoid-double-run] FAIL：两次 humanoid 的 [DET] 序列不一致"; fail=1
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
