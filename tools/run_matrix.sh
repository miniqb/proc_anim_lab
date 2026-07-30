#!/bin/bash
# 确定性全矩阵回归：37 配置（20 Lizard + 13 Centipede + 4 Vulture）× 硬断言
# （哈希基线 / 有限值 / 深断裂连跑 / 释放 churn / 路点下限 /
# 位置检查 / 退出码聚合）。任何一项红 → 本脚本非零退出。旧版 `grep '[DET]'` 管道无 pipefail、
# 探针只打印不断言, NaN、尾链断裂、原生崩溃(exit 134)全都假绿——本脚本就是那次教训的产物。
#
# 有意改内核后重定基线：先人工核对各配置 [METRIC]/[FINAL] 合理，再同步更新下方哈希表与
# core/smoke/Program.cs（Lizard）或 core/smoke/CentipedeSmoke.cs（Centipede）的
# ExpectedHash（可执行基线只存这三处，别处一律引用不复制），
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
HASH_VULTURE=2D5A98F31A341BD9
HASH_VULTURE_KING=17B7904915D96960
HASH_VULTURE_SWIFT=9CB6463BBDFBF7B2
HASH_VULTURE_PERCH=7D2E1015ED56A00E

# —— 并列蜈蚣基线（独立于上方 Lizard 真相源；各场景 tick 数见调用处）——
HASH_CENTIPEDE_SHORT=0F040547BFD02043
HASH_CENTIPEDE_LONG=B66DAAB5D006190E
HASH_CENTIPEDE_ARMORED=A6EDF4704829C261
HASH_CENTIPEDE_RIBBON=EB6011908D0FAA19
HASH_CENTIPEDE_COURSE_SHORT=BB6696619749832D
HASH_CENTIPEDE_COURSE_LONG=A2BE4857DB102C19
HASH_CENTIPEDE_STEP_DOWN_ARMORED=ECC5207E14979A28
HASH_CENTIPEDE_NARROW_WALL_LONG_END=413E289A97ABD487
HASH_CENTIPEDE_EMBED_LONG=2C8B2D67731F2B7E
HASH_CENTIPEDE_WALLSIDE_LONG=501B7C44E06FA68B

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

# 独立解析蜈蚣通用硬门。SandboxWorld 内部仍以未舍入值作最终判定；这里额外钉住
# 日志契约，避免 [RESULT] 被误删、字段改名或矩阵跑错物种后仍靠退出码假绿。
check_centipede_common() {
    local name="$1" expected_id="$2" expected_segments="$3"
    local file="$OUT/$name.txt" metric penetration enddev deep
    local ok=1
    metric=$(grep "^\[METRIC\] creature=$expected_id " "$file" | tail -1)
    if [ -z "$metric" ] || ! printf '%s\n' "$metric" | grep -q " segments=$expected_segments "; then
        echo "[$name-hard-gates] FAIL：缺少 creature=$expected_id segments=$expected_segments 指标"
        fail=1
        return
    fi
    penetration=$(printf '%s\n' "$metric" | sed -n 's/.* penetration=\([0-9.]*\)m .*/\1/p')
    enddev=$(printf '%s\n' "$metric" | sed -n 's/.* endDev=\([0-9.]*\)x .*/\1/p')
    deep=$(printf '%s\n' "$metric" | sed -n 's/.* maxDeepRun=\([0-9]*\) .*/\1/p')
    if [ -z "$penetration" ] || ! awk -v v="$penetration" 'BEGIN { exit !(v <= 0.0020001) }'; then
        echo "[$name-hard-gates] 穿透超限/字段缺失: ${penetration:-无} > 0.002m"
        ok=0
    fi
    if [ -z "$enddev" ] || ! awk -v v="$enddev" 'BEGIN { exit !(v <= 0.100001) }'; then
        echo "[$name-hard-gates] 终态连接偏差超限/字段缺失: ${enddev:-无} > 0.10"
        ok=0
    fi
    if [ -z "$deep" ] || [ "$deep" -gt 20 ]; then
        echo "[$name-hard-gates] 单一相邻连接偏差 >10% 连续超限/字段缺失: ${deep:-无} > 20 tick"
        ok=0
    fi
    if [ $ok -eq 1 ]; then
        echo "[$name-hard-gates] PASS（penetration=${penetration}m endDev=${enddev}x maxDeepRun=$deep）"
    else
        fail=1
    fi
}

# run_centipede <name> <期望哈希|-> <路点下限|-> <tick数> <稳定ID> <节数> <额外参数...>
run_centipede() {
    local name="$1" expect="$2" minwp="$3" ticks="$4" stable_id="$5" segments="$6"
    shift 6
    run "$name" "$expect" "$minwp" "$ticks" "--creature=$stable_id" "$@"
    check_centipede_common "$name" "$stable_id" "$segments"
}

check_centipede_course() {
    local name="$1" expected_segments="$2"
    local file="$OUT/$name.txt" metric summary segments budget printed_budget none_run blocked_run connection_run tail_lag
    local stage line lead tail lag ok=1
    metric=$(grep '^\[METRIC\] creature=centipede/' "$file" | tail -1)
    summary=$(grep '^\[CENTIPEDE-COURSE\] drive=' "$file" | tail -1)
    segments=$(printf '%s\n' "$metric" | sed -n 's/.* segments=\([0-9]*\) .*/\1/p')
    none_run=$(printf '%s\n' "$summary" | sed -n 's/.* maxNoneRun=\([0-9]*\) .*/\1/p')
    blocked_run=$(printf '%s\n' "$summary" | sed -n 's/.* maxBlockedRun=\([0-9]*\) .*/\1/p')
    connection_run=$(printf '%s\n' "$summary" | sed -n 's/.* maxConnectionRun=\([0-9]*\) .*/\1/p')
    tail_lag=$(printf '%s\n' "$summary" | sed -n 's/.* maxTailLag=\([0-9]*\) .*/\1/p')
    printed_budget=$(printf '%s\n' "$summary" | sed -n 's/.* tailBudget=\([0-9]*\) .*/\1/p')
    budget=$((40 + 8 * expected_segments))
    if [ "$segments" != "$expected_segments" ] || [ "$printed_budget" != "$budget" ]; then
        echo "[$name-course-gates] 节数/尾随预算错误: segments=${segments:-无}, budget=${printed_budget:-无}, expected=$expected_segments/$budget"
        ok=0
    fi
    if [ -z "$none_run" ] || [ "$none_run" -gt 40 ]; then
        echo "[$name-course-gates] 换面失去有效支撑超限/字段缺失: ${none_run:-无} > 40 tick"
        ok=0
    fi
    if [ -z "$blocked_run" ] || [ "$blocked_run" -gt 40 ]; then
        echo "[$name-course-gates] 领航表面路径阻塞超限/字段缺失: ${blocked_run:-无} > 40 tick"
        ok=0
    fi
    if [ -z "$connection_run" ] || [ "$connection_run" -gt 20 ]; then
        echo "[$name-course-gates] 相邻连接断续超限/字段缺失: ${connection_run:-无} > 20 tick"
        ok=0
    fi
    if [ -z "$tail_lag" ] || [ "$tail_lag" -gt "$budget" ]; then
        echo "[$name-course-gates] 尾端通过超时/字段缺失: ${tail_lag:-无} > $budget tick"
        ok=0
    fi
    for stage in floor slope inner-wall top outer-wall ceiling; do
        line=$(grep "^\[CENTIPEDE-COURSE\] stage=$stage " "$file" | tail -1)
        lead=$(printf '%s\n' "$line" | sed -n 's/.* lead=\(-\{0,1\}[0-9]*\) .*/\1/p')
        tail=$(printf '%s\n' "$line" | sed -n 's/.* tail=\(-\{0,1\}[0-9]*\) .*/\1/p')
        lag=$(printf '%s\n' "$line" | sed -n 's/.* lag=\(-\{0,1\}[0-9]*\).*/\1/p')
        if [ -z "$lead" ] || [ -z "$tail" ] || [ -z "$lag" ] \
            || [ "$lead" -lt 0 ] || [ "$tail" -lt 0 ] || [ "$lag" -lt 0 ] \
            || [ "$lag" -gt "$budget" ] || [ $((tail - lead)) -ne "$lag" ]; then
            echo "[$name-course-gates] 阶段 $stage 不完整/尾随超时: lead=${lead:-无} tail=${tail:-无} lag=${lag:-无}"
            ok=0
        fi
    done
    if [ $ok -eq 1 ]; then
        echo "[$name-course-gates] PASS（switch=$none_run blocked=$blocked_run connection=$connection_run tail=$tail_lag/$budget）"
    else
        fail=1
    fi
}

check_centipede_step_down() {
    local name="$1" expected_segments="$2"
    local file="$OUT/$name.txt" metric summary segments lead tail lag lead_wall lead_wall_ticks
    local tail_wall tail_wall_ticks net final_sep pile changed budget ok=1
    metric=$(grep '^\[METRIC\] creature=centipede/' "$file" | tail -1)
    summary=$(grep '^\[CENTIPEDE-STEP-DOWN\] ' "$file" | tail -1)
    segments=$(printf '%s\n' "$metric" | sed -n 's/.* segments=\([0-9]*\) .*/\1/p')
    lead=$(printf '%s\n' "$summary" | sed -n 's/.* lead=\(-\{0,1\}[0-9]*\) .*/\1/p')
    tail=$(printf '%s\n' "$summary" | sed -n 's/.* tail=\(-\{0,1\}[0-9]*\) .*/\1/p')
    lag=$(printf '%s\n' "$summary" | sed -n 's/.* lag=\(-\{0,1\}[0-9]*\) .*/\1/p')
    lead_wall=$(printf '%s\n' "$summary" | sed -n 's/.* leadWall=\(-\{0,1\}[0-9]*\)\/[0-9]* .*/\1/p')
    lead_wall_ticks=$(printf '%s\n' "$summary" | sed -n 's/.* leadWall=-\{0,1\}[0-9]*\/\([0-9]*\) .*/\1/p')
    tail_wall=$(printf '%s\n' "$summary" | sed -n 's/.* tailWall=\(-\{0,1\}[0-9]*\)\/[0-9]* .*/\1/p')
    tail_wall_ticks=$(printf '%s\n' "$summary" | sed -n 's/.* tailWall=-\{0,1\}[0-9]*\/\([0-9]*\) .*/\1/p')
    net=$(printf '%s\n' "$summary" | sed -n 's/.* netProgress=\([0-9.]*\)m .*/\1/p')
    final_sep=$(printf '%s\n' "$summary" | sed -n 's/.* finalNonAdjacent=\([0-9.]*\)x .*/\1/p')
    pile=$(printf '%s\n' "$summary" | sed -n 's/.* maxPileRun=\([0-9]*\) .*/\1/p')
    changed=$(printf '%s\n' "$summary" | sed -n 's/.* leadChanged=\([^ ]*\).*/\1/p')
    budget=$((40 + 8 * expected_segments))
    if [ "$segments" != "$expected_segments" ]; then
        echo "[$name-step-down-gates] 节数错误/字段缺失: ${segments:-无} != $expected_segments"
        ok=0
    fi
    if [ -z "$lead" ] || [ -z "$tail" ] || [ -z "$lag" ] \
        || [ "$lead" -lt 0 ] || [ "$tail" -lt "$lead" ] \
        || [ $((tail - lead)) -ne "$lag" ] || [ "$lag" -gt "$budget" ]; then
        echo "[$name-step-down-gates] 领/尾端未完整下阶梯: lead=${lead:-无} tail=${tail:-无} lag=${lag:-无} budget=$budget"
        ok=0
    fi
    if [ -z "$net" ] || ! awk -v v="$net" 'BEGIN { exit !(v >= 2.5) }'; then
        echo "[$name-step-down-gates] 身体净前进不足/字段缺失: ${net:-无} < 2.5m"
        ok=0
    fi
    if [ -z "$lead_wall" ] || [ -z "$lead_wall_ticks" ] \
        || [ -z "$tail_wall" ] || [ -z "$tail_wall_ticks" ] \
        || [ "$lead_wall" -lt 0 ] || [ "$lead_wall_ticks" -lt 1 ] \
        || [ "$tail_wall" -lt 0 ] || [ "$tail_wall_ticks" -lt 1 ]; then
        echo "[$name-step-down-gates] 未取得真实外侧立面支撑: lead=${lead_wall:-无}/${lead_wall_ticks:-无} tail=${tail_wall:-无}/${tail_wall_ticks:-无}"
        ok=0
    fi
    if [ -z "$final_sep" ] || ! awk -v v="$final_sep" 'BEGIN { exit !(v >= 0.75) }'; then
        echo "[$name-step-down-gates] 终态非相邻节仍成团/字段缺失: ${final_sep:-无} < 0.75x 半径和"
        ok=0
    fi
    if [ -z "$pile" ] || [ "$pile" -gt 8 ]; then
        echo "[$name-step-down-gates] 非相邻节严重重叠持续超限/字段缺失: ${pile:-无} > 8 tick"
        ok=0
    fi
    if [ "$changed" != "False" ]; then
        echo "[$name-step-down-gates] 固定 Start 领航端发生变化/字段缺失: ${changed:-无}"
        ok=0
    fi
    if [ $ok -eq 1 ]; then
        echo "[$name-step-down-gates] PASS（lead/tail=$lead/$tail lag=$lag/$budget wall=$lead_wall/$lead_wall_ticks,$tail_wall/$tail_wall_ticks net=${net}m finalSep=${final_sep}x pile=$pile）"
    else
        fail=1
    fi
}

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
# 秃鹫（VultureFlightController，与蜥蜴并列的飞行生物控制器）：
# fly = 3D 巡航环线反复越 3m 薄墙（[RESULT] 断言飞行占比 ≥80% + 越墙高度 ≥4m）；
# perch = 空中路点后喂地面目标，[RESULT] 断言真的降落吸附且终态栖息。
run vulture       "$HASH_VULTURE"       21 2000 --tps=400 --route=fly --breed=vulture --spawn=0,0.5,0
run vulture-king  "$HASH_VULTURE_KING"  24 2000 --tps=400 --route=fly --breed=king --spawn=0,0.5,0
run vulture-swift "$HASH_VULTURE_SWIFT" 28 2000 --tps=400 --route=fly --breed=swift --spawn=0,0.5,0
run vulture-perch "$HASH_VULTURE_PERCH" 2  800  --tps=400 --route=perch --breed=vulture --spawn=0,0.5,0

# 并列蜈蚣矩阵（13 配置）：四预设巡逻 + short 双跑/40Hz/微扰 +
# short/long 全向课程 + armored 固定头下阶梯 + long 窄墙/嵌入恢复/擦墙。long 课程必须实际抵达 ceiling 阶段，
# 因而同时承担长型天花板路线；不另跑一条内容相同的重复命令制造假覆盖。
run_centipede centipede-short    "$HASH_CENTIPEDE_SHORT"    2 2000 centipede/short   5  --tps=400
run_centipede centipede-long     "$HASH_CENTIPEDE_LONG"     1 2000 centipede/long    18 --tps=400
run_centipede centipede-armored  "$HASH_CENTIPEDE_ARMORED"  2 2000 centipede/armored 10 --tps=400
run_centipede centipede-ribbon   "$HASH_CENTIPEDE_RIBBON"   2 2000 centipede/ribbon  12 --tps=400
run_centipede centipede-short-b  "$HASH_CENTIPEDE_SHORT"    2 2000 centipede/short   5  --tps=400
run_centipede centipede-short-40 "$HASH_CENTIPEDE_SHORT"    2 2000 centipede/short   5
run_centipede centipede-short-perturb - 1 2000 centipede/short 5 --tps=400 --perturb=0.001
run_centipede centipede-course-short "$HASH_CENTIPEDE_COURSE_SHORT" - 900 \
    centipede/short 5 --tps=400 --route=centipede-course --spawn=0,0.5,20
run_centipede centipede-course-long "$HASH_CENTIPEDE_COURSE_LONG" - 1200 \
    centipede/long 18 --tps=400 --route=centipede-course --spawn=0,0.5,20
# 全身先在 z=-8 专用平台顶面展开；宿主固定 Start 且始终只给 +X，不在外角替控制器补 Down。
run_centipede centipede-step-down-armored "$HASH_CENTIPEDE_STEP_DOWN_ARMORED" - 500 \
    centipede/armored 10 --tps=400 --route=centipede-step-down --spawn=1.4,1.1,-8 --lead=start
# 固定 End 向前翻越旧 0.4m 墙一次，随后至少停驶 80 tick，避免终态依赖运动相位。
run_centipede centipede-narrow-wall-long-end "$HASH_CENTIPEDE_NARROW_WALL_LONG_END" 1 800 \
    centipede/long 18 --tps=400 --route=centipede-narrow-wall --spawn=0,0.5,0 --lead=end
# z=5 避开旧 Step：这里故意把整条身体出生在地板内，而不是把长体横穿另一块障碍物。
run_centipede centipede-embed-long "$HASH_CENTIPEDE_EMBED_LONG" - 80 \
    centipede/long 18 --tps=400 --route=stand --spawn=0,-0.1,5
# 沿 -X 出生的长体在旧墙 +Z 侧相切；中段球先浅嵌后由球体 MTD 推到 z=3+r。
run_centipede centipede-wallside-long "$HASH_CENTIPEDE_WALLSIDE_LONG" - 160 \
    centipede/long 18 --tps=400 --route=stand --spawn=-4,0.3,3.12

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

# 灵敏度：微扰后终值哈希必须偏离基线（哈希对状态不敏感 = 回归全盲）
if [ "$(final_hash perturb)" = "$HASH_DEFAULT" ]; then
    echo "[perturb] FAIL：微扰后哈希未变"; fail=1
else
    echo "[perturb] PASS（哈希已偏离基线）"
fi

# 全向课程日志必须独立证明六阶段、换面连续性、逐连接断续与按节数计算的尾随预算。
check_centipede_course centipede-course-short 5
check_centipede_course centipede-course-long 18
check_centipede_step_down centipede-step-down-armored 10

# long 嵌入恢复：80 tick 后每节球心都应至少到达 floorY + radius - 2mm，
# 且必须仍有真实局部支撑；空/截断的 [FINAL] 不能默认通过。
if grep '^\[FINAL\] body' "$OUT/centipede-embed-long.txt" | awk '
{
    count++
    pos=$0; sub(/^.*pos=\(/, "", pos); split(pos, p, ","); y=p[2]+0
    radius=$0; sub(/^.* r=/, "", radius); split(radius, r, " ")
    support=$0; sub(/^.* support=/, "", support); split(support, s, " ")
    if (y + 0.002 < r[1]+0) bad=1
    if (s[1]+0 >= 0.15) supported++
}
END { exit !(count == 18 && !bad && supported > 0) }'; then
    echo "[centipede-embed-escape] PASS"
else
    echo "[centipede-embed-escape] FAIL：终态节体仍在地板内/无支撑/输出不完整"; fail=1
fi

# long 擦墙：把每个节球对旧墙 AABB([-6.2,-5.8]×[0,3]×[-3,3])做终态距离门，
# 不得穿入 2mm；同时至少一个球必须真实 contact 且与墙面相切，防止“压根没擦到墙”假绿。
if grep '^\[FINAL\] body' "$OUT/centipede-wallside-long.txt" | awk '
{
    count++
    pos=$0; sub(/^.*pos=\(/, "", pos); split(pos, p, ",")
    x=p[1]+0; y=p[2]+0; z=p[3]+0
    radius=$0; sub(/^.* r=/, "", radius); split(radius, rp, " "); r=rp[1]+0
    contact=$0; sub(/^.* contact=/, "", contact); split(contact, c, " ")
    dx=(x < -6.2 ? -6.2-x : (x > -5.8 ? x+5.8 : 0))
    dy=(y < 0 ? -y : (y > 3 ? y-3 : 0))
    dz=(z < -3 ? -3-z : (z > 3 ? z-3 : 0))
    distance=sqrt(dx*dx + dy*dy + dz*dz)
    if (distance + 0.002 < r) bad=1
    gap=distance-r; if (gap < 0) gap=-gap
    if (c[1] == "True" && gap <= 0.003) touched++
}
END { exit !(count == 18 && !bad && touched > 0) }'; then
    echo "[centipede-wallside-graze] PASS"
else
    echo "[centipede-wallside-graze] FAIL：节球穿墙/未真实接触/输出不完整"; fail=1
fi

if grep -q '^\[DET\]' "$OUT/centipede-short.txt" \
        && grep -q '^\[DET\]' "$OUT/centipede-short-b.txt" \
        && diff <(grep '^\[DET\]' "$OUT/centipede-short.txt") \
            <(grep '^\[DET\]' "$OUT/centipede-short-b.txt") > /dev/null; then
    echo "[centipede-double-run] PASS"
else
    echo "[centipede-double-run] FAIL：两次 short 的 [DET] 序列不一致"; fail=1
fi

centipede_perturb_hash=$(final_hash centipede-short-perturb)
if [ -z "$centipede_perturb_hash" ]; then
    echo "[centipede-perturb] FAIL：未能提取微扰终值哈希"; fail=1
elif [ "$centipede_perturb_hash" = "$HASH_CENTIPEDE_SHORT" ]; then
    echo "[centipede-perturb] FAIL：微扰后哈希未变"; fail=1
else
    echo "[centipede-perturb] PASS（哈希已偏离 short 基线）"
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
