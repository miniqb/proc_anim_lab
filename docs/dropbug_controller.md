# DropBug（掉落虫）并列后端 —— 反编译取证、3D 移植与验证边界

> 状态：2026-08-04 落地。`core/species/dropbug/` 与其余九个物种后端并列，
> 只复用 `Body` / `BodyChunk` / `ChunkConnection` / `ITerrainQuery` 底座，
> 不继承、不引用任何其它物种的参数表或肢体实现（`[CORE-MODULARITY]` 扫描背书）。
> 共享层零改动（`core/ProcAnim.Core.csproj` 仅新增 `dropbug_smoke` 的 glob 排除行，
> 属工程文件的既有模式）。

## 1. 反编译取证（真相源：当前 DLL）

直接反编译 `~/workspace/others/Managed_extracted/Assembly-CSharp.dll`：

- **`DropBug.cs`**（本体，1189 行）：三 chunk 身体、Footing 计数与前后不对称重力、
  MoveTowards 推进、越障抬升、倒退行走、inCeilingMode 悬挂收放、JumpFromCeiling /
  Jump / Attack 攻击链、stuckShake、CarryObject。
- **`DropBugAI.cs`**（AI，914 行）：`CeilingSitModule`（悬挂点选择/离脱），
  `ValidCeilingSpot` / `CeilingSpotScore`（合法悬挂点判定与打分），SitUpdate 的
  掉落触发（预判提前量、dropDelay、spearDanger）。
- **`DropBugGraphics.cs`**（图形，735 行）：**腿为 `Limb[2,2]` 纯图形件**，全部挂在
  chunk0 上、只做 FindGrip 视觉抓地，**不向身体回传力**——"腿不参与物理"由此实证。
  runCycle 驱动腿相位；触须/颚/尾须均为图形件。

### 1.1 逐位换算的原作数值（1px = 0.025m，力/速度 = 米/tick）

| 项 | 原作 | 本项目 |
|---|---|---|
| chunk 半径 头/中/尾 | 6 / 8 / 6 px | 0.15 / 0.20 / 0.15 m |
| chunk 质量 | 0.32 / 0.32 / 0.16 | 同值 |
| 连接 头-中 / 中-尾 | Normal 12 / 14 px | Rigid 0.30 / 0.35 m |
| 头-尾防对折 | Push 8 px（只防过近） | PushOnly 0.20 m |
| 连接权重 | weight −1（按质量反比） | WeightA = massB/(massA+massB) |
| airFriction / surfaceFriction | 0.999 / 0.4 | 同值 |
| 自撑力 头/尾 | +0.5 / −1 px·axis | 0.0125 / 0.025（见 §8 静止归零） |
| Footing 门槛/衰减/宽限帽 | >10 / −3·tick / clamp 35 | 同值 |
| 前段站稳阻尼 + 重力抵消 | ×0.8 + 全额 | 同值 |
| 尾段重力抵消 | ×Lerp(0.5, 1, stuck)，无阻尼 | 同值 |
| 推进 头/中/尾 | 4.5 / −0.45 / −0.2 px | 0.1125 / 0.01125 / 0.005 |
| 倒退 尾/中/头 | 7.5 / +0.2 / −0.45 px | 0.1875 / 0.005 / 0.01125 |
| 失稳推进衰减 | ×0.3 | 同值 |
| 负重力衰减 | LerpMap(m, 0, 4, 1, 0.2, 0.7) | 同公式 |
| 越障：头滞后/横向/中段/上跳 | 5 / 1.3 / 0.5 / 3.2 px | 0.125 / 0.0325 / 0.0125 / 0.08 |
| 悬挂静息长度 头-中/中-尾/防折 | →5 / 2 / 0 px | →0.125 / 0.05 / 0 m |
| 悬挂进入速率 / 贴附半径 | +0.025·tick / 40 px | 同值 / 1.0 m |
| 悬挂位置伺服 头/中/尾 | 0.05 / 0.4 / 0.5 ×f | 同值 |
| 爬升辅助 | 50px 内 LOS 时 mid.pos += 1px | 1.25 m / 0.025 m |
| 俯冲冲量 头/中 | 21 / 16 px（先 ×0.5 削速） | 0.525 / 0.4 |
| 跳跃方向功率 | LerpMap(dir.y, −1, 1, 0.7, 1.2, 1.1) | 同公式 |
| 空中转向 头/中尾 | 1.2 / −0.4 px | 0.03 / 0.01 |
| 高位水平修正窗 | 高于 250px 且距 <350px，×3px | 6.25 m / 8.75 m / 0.075 |
| 落地攻击冷却 | 20 tick | 同值 |
| 蓄力速率 / 头 / 中 | 1/15；charging²px / −4·charging px | 同值；0.025c² / 0.1c |
| 扑击可及 | LerpMap(dot(扑向,身体轴), −0.1, 0.8, 0, 300px, 0.4) | 0..7.5 m 同公式 |
| 瞄点抬高 / 起跳上扬 | ≤20px；Slerp 0.05..0.2 | ≤0.5 m；同公式 |
| 合法悬挂点 | 空 tile + 上方 2 实心 + 下方空 + 落差 ≥6 tile | 见 §5 3D 判据 |
| 腿 | 2×2 图形 Limb、legLength 45px、dangle 30 tick | 4 条表现腿 / 1.125 m / 同值 |

## 2. 装配（`DropBugFactory`）

三个稳定 ID，未知 ID 快速失败（抛 `ArgumentException`，不静默回落）：

- **`dropbug/original`** —— 上表逐位换算的基准个体。
- **`dropbug/nimble`** —— 0.85 体格、0.75 质量、蓄力 1/12、MaxMoveSpeed 0.15、短腿。
- **`dropbug/bulky`** —— 1.25 体格、1.5 质量、蓄力 1/20、冲量加强、负重耐受
  （CarryMassFull 6）、MaxMoveSpeed 0.10。

朝向参照显式钉定：头→尾（全身轴）、中→尾、尾→头——数值上与 RW 建链顺序副作用
（conn2 后建、头尾互绑覆盖）一致，但不依赖顺序巧合。出生参数 Snapshot 冻结
（smoke `birthFrozen` 断言）。

## 3. 固定序与支撑

`Tick` 顺序：ConfigureBody（按上 tick HangFactor 写静息长度/碰撞开关，≙ RW Update 尾部
赋值作用于下一物理步）→ `Body.Tick` → 冷却递减 → 自撑力 → 支撑探测 → 俯冲结束判定 →
悬挂 / 腾空 / 地面 Act → 站稳重力块 → travelDir 衰减 → 地面限速 → 姿态帧 → 表现腿。

**支撑探测**（原作为 AI 图 tile 可达性，3D 无格子）：头/中各一根沿世界重力向的
`radius + 0.15m` 射线，或本 tick 已有可站立接触（法线·up ≥ 0.5，与 Humanoid
IsGroundNormal 同一条线；HitFromInside 零法线视为有支撑）。悬挂意图存在且身体接近锚点
（< 2×1.25m）时，朝锚面方向的探针也计支撑——≙ RW 天花板 tile 对 DropBug 可达，
贴顶爬升期间重力被 Footing 块抵消。腾空（Jumping）期间改用接触判据
（任一节可站立接触 → +1，否则清零，≙ jumping 块）；悬挂钉 20。

**前后不对称重力**是本物种的辨识点：站稳时头/中 ×0.8 阻尼 + 全额抵消，尾段只抵消
`Lerp(0.5, 1, stuckSignal)` 且无阻尼（卡住时尾段获得全额支撑，原作语义）；倒退行走时
尾段是领航端、按前段处理。smoke `FOOTING-ASYM` 用台缘悬尾场景量"站稳后下垂增量"
（0.477m vs 消融 −0.03m）。

## 4. 行走、越障、倒退、卡住

- 推进主要施加在前段（头 0.1125、中/尾小反向力），身体被"头"拖着走；`MaxMoveSpeed`
  为 3D 追加（连续胡萝卜无 RW 瞄格中心的天然限速，论证同蜥蜴），只在站稳且非
  弹道/蓄力/悬挂时钳制。
- **越障抬升**：条件照抄原作字面语义——前进意图强、中段踩地、头落在中段后面
  （`(head−mid)·hd < −0.125`）→ 头向后上翘、中段前送、头顶无实心补 0.08 上跳。
  实测该涌现签名出现在**反转朝向**时（正向撞台阶头恒在前），smoke 用反转场景验证：
  点火 9 次、折返 10 tick（消融 0 次 / 16 tick）。
- **倒退行走**：悬挂锚已指定、未贴附、未卡住且身体距贴附点 < 3.5m 时尾段领航
  （原作 walkBackwardsDist 为 0.005/tick 重掷的 0..20 tile 随机值 → 确定性常量，
  取原作区间中低段）。
- **卡住抖动**：原作 StuckTracker 历史位置 + Random.value/RNV → 确定性等价：30 tick
  环形缓冲的身体中心净位移均速 < 0.01 m/tick 且有移动意图时累计，信号在 40..120 tick
  区间 0→1；shake 用原作 LerpAndTick 参数（升 0.07/1÷70、降 0.07/0.05，>0.9 触发、
  <0.2 消退的滞回天然防自灭）；抖动方向/幅度用整数模数伪随机（Knuth 乘法散列，
  逐位确定），pos 与 vel 各注入 ≤0.125m·shake；行进力 ×Lerp(1, 1.5, shake)。

## 5. 悬挂

**3D 合法性判据**（`TryAssignHangAnchor(hit, terrain)`，拒因写入 `LastHangRejection`）：

1. 法线向下分量 ≥ `MinCeilingDot`（0.707 = 45°）。**取舍**：只接受 ≤45° 的倒悬面。
   原作只有水平天花板；悬挂→俯冲是重力弹道，面越斜"脱落"越接近贴墙滑落，
   45° 是姿态伺服（沿法线摆位）与弹道语义还能同时成立的边界。斜面悬挂时身体沿
   法线轴摆位、俯冲仍沿世界竖直判定。
2. 实体厚度：表面里侧 `SolidProbeDepth`（默认 0.3m）处必须仍在实体内
   （SpherePenetration 探针，≙ 原作"上方连续 2 实心 tile"=1m；3D collider 厚度由
   关卡作者掌控，0.3 已排除薄板，参数可调）。
3. 身体净空：沿法线 0.6m 无阻挡（≙ 下方空 tile）。
4. 落差：锚点沿**世界竖直**向下 3m 无阻挡（≙ floorAltitude ≥ 6 tile；俯冲是重力
   弹道，此判据保持世界向，不随面倾斜）。

原作的 narrowSpace / 相机 / 出口可达性打分属 AI 选点，归宿主。

**进入**：锚只建立"意图"。身体（mid）进入贴附半径 1.0m 后 `HangFactor` 以 0.025/tick
增长：三节各自向"锚点沿法线的分层目标"位置伺服（头 0.05f / 中 0.4f / 尾 0.5f），
速度 ×(1−f)，站稳计数钉 20；三条连接静息长度按 f 插值收缩（0.30→0.125 / 0.35→0.05 /
0.20→0）——运行时改 `ChunkConnection.RestLength` 公开字段，无共享层配合。
中/尾在 f ≥ 0.05 起停止地形碰撞并埋入面内（原作语义），头保持碰撞、悬在球团下方。
最后 1.25m 有爬升辅助（LOS 时 mid.pos += 0.025/tick）。**可达性边界**：贴附半径 +
辅助共覆盖 ~1.25m，更高的锚需要宿主给邻近可达位置（台柱/平台，或 Launch 抛投）——
与 `MoveTarget` 只接受邻近可达点同一契约；原作靠 DropBug 模板的墙面/天花板可达性
自行爬顶，本项目不移植攀爬。

**退出**恒为瞬时（≙ 原作 inCeilingMode 直接归 0）：`ReleaseHangDive`（俯冲）、
`ClearHangAnchor`（撤销）、身体被挤出贴附半径、锚面复验失败（每 tick 一根射线，
collider 消失 → 直接掉落，3D 附加）。静息长度下一 tick 由公式恢复、碰撞重开、
MTD 单 tick 推出嵌入——smoke/矩阵断言全程无弹飞（maxStep < 0.45m）、无穿透。

**悬挂中**：几乎静止（矩阵实测 100 tick 漂移 0.0000m、峰值速度 0.0225 m/tick）、
腿收拢到锚面固定点。"更不容易被发现"（VisibilityBonus）是 AI 观测面，归宿主。

## 6. 俯冲

`ReleaseHangDive()`：有 `AttackTarget` 时按距离预判提前量瞄准（≙ AI SitUpdate 的
`ClampMagnitude(vel, 6px) × LerpMap(dist, 40, 500, 0, 30, 0.8)`，该公式虽在原作 AI 侧，
但它是弹道学而非决策，移到控制器；宿主只决定"何时脱落"），无目标时垂直向下
（≙ Dislodge）。`Jump`：头/中先 ×0.5 削速再施 0.525/0.4 冲量（×方向功率 0.7..1.2）。

腾空期间（扑击与俯冲共用，≙ jumping 块）：LOS 通过时头 +0.03 朝目标、中/尾 −0.01
反向（头朝目标的力偶）；俯冲且头高于目标 6.25m、距离 <8.75m 时，取"目标+目标速度"
预测点单位方向的**水平分量**（不归一化，保持原作 `.x × 3px` 的幅度语义）×0.075 加到头。
任一节可站立接触即结束：`AttackCooldown = 20`，冷却期拒绝蓄力。

矩阵 dive 路线（8m 塔）：最近脱靶 0.795m、飞行 15 tick、落地冷却 20；smoke（解析
地形）最近 0.452m，消融空中修正后升到 1.044m（门有效性证据）。

## 7. 蓄力扑击

`TryStartPounce()`：站稳 + 有目标 + 非悬挂/腾空/冷却/负重（`CarriedMass > 0` 即拒，
≙ grasps[0] != null）。蓄力 +1/15·tick：头 +0.025c² 朝目标、中 −0.1c 反向——刚性链
在地面上的压缩表现为**整体后坐**（smoke 实测中段后坐 0.379m；头尾间距只微屈 3mm，
原作 2D 深蹲主要是图形层）。蓄满 `Attack`：目标上方 0.5m 无实心时按距离抬高瞄点
≤0.5m；自身头/中上方无实心时按距离把方向向上 Slerp 0.05..0.2；末次可及复核后弹射。

**可及范围** `LerpMap(dot(扑向, 身体轴), −0.1, 0.8, 0, 7.5m, 0.4)`：正对 7.5m、
侧对 ~3.1m、背对 0——"侧对着目标时够到的距离明显更短"直接由公式承载
（`PounceReach()` 公开给宿主/HUD 画可及）。目标逃逸的放弃为**逐 tick 复核**
（原作只在蓄满的 Attack() 复核；提前复核等价于"目标离开即放弃"且响应更快）。
站稳丢失时放弃归零（原作把 charging 冻在非归零态属未定义行为区，不移植）。

## 8. 显式 vs 涌现（任务第 12 条的取舍）

保留的显式状态只有四个跨 tick 意图量，其余全部派生：

| 状态 | 显式/涌现 | 理由 |
|---|---|---|
| `HangFactor` | 显式 float | ≙ inCeilingMode；直接驱动静息长度形变与位置伺服，物理上不可从几何反推 |
| `PounceCharge` | 显式 float | ≙ charging；驱动力 ramp 与释放时刻 |
| `Diving` | 显式 bool | ≙ fromCeilingJump；"因俯冲腾空"与"被宿主击飞"物理不可分辨，但结束条件（触地即停+冷却）与转向（水平修正）不同 |
| `AttackCooldown` | 显式计数 | ≙ afterDropAttackDelay |
| 站/走/坠 | 涌现 | FootingCounter 连续计数（≙ 原作，无枚举） |
| `Jumping` | 半涌现 | 置位显式（弹射瞬间），复位涌现（接触计数回稳） |
| 倒退 | 涌现 | 每 tick 由锚距离/卡住信号重算，非宿主开关 |
| 越障抬升 | 涌现 | 纯当 tick 几何条件，无状态 |
| `Sitting` | 派生 | 悬挂/蓄力/站稳无意图的汇总，只喂 travelDir 衰减与自撑力门 |

## 9. 有意偏离原作的清单（原作 → 本项目 → 理由）

1. **自撑力静止归零**：原作恒定注入（净 −0.5px/tick 轴向动量）→ 静止（Sitting）时
   不注入。原作靠 AI 持续微调掩盖静止缓滑；确定性回归要求静止逐位为零
   （smoke `stationaryFrozen` 严格断言）。任务语义本身是"在运动中保持舒展"。
2. **HangMidRise 9px→8px（0.225→0.20m）**：原作几何下头顶与面恰好相切，3D 球形
   碰撞与吸附伺服会逐 tick 推挤出 ~12mm 定态残余（tile 碰撞无此敏感度）；留出
   头部半径余量后悬挂定态零穿透。
3. **HangCollisionToggle 0.5→0.05**：原作 0.5 时，f∈[0.35, 0.5) 窗口内中段吸附目标
   已越面而碰撞未关，球形碰撞逐 tick 博弈（实测 ~10mm 残余）；提前到贴附即允许
   嵌入，收放全程 2mm 门达标。
4. **卡住检测**：Random/StuckTracker → 30 tick 窗口净位移 + 整数模数伪随机抖动
   （净位移对抖动自身的随机游走钝感，不自灭；humanoid sin 相位先例）。
5. **walkBackwardsDist**：0..20 tile 随机重掷 → 常量 3.5m。
6. **runCycle**：头位移 ≥2px 的 tick 固定 +0.125 → 按位移比例（0.05m→0.125，
   上限 2×，死区 2mm）。任务要求"步频由实际位移驱动、静止不迈"，原作的二值门
   在低速下会完全停摆。
7. **MaxMoveSpeed（3D 追加）** 与 **MinCeilingDot / SolidProbeDepth / 落差射线**
  （3D 判据）：见 §4/§5。
8. **俯冲预判公式位置**：原作在 AI 侧 → 控制器（弹道学归运动层，决策归宿主）。
9. **Dislodge 的 jumpAtPos=(0,−1)**：原作把方向向量当世界坐标继续转向（视为原作
   缺陷），不移植——无目标俯冲为纯弹道。
10. **逐 tick 可及复核**（见 §7）；**站稳丢失时蓄力放弃**（见 §7）。
11. **不移植**：水中运动（项目边界）、bounce 0.1（Body 无反弹语义）、抓取/咬合/
    投掷物反应/AI 全部行为树、噪声/可见度系统、SpitOutOfShortCut。

## 10. 宿主接口

输入：`MoveDir` / `RunSpeed` / `MoveTarget`（+`AtMoveTarget` 迟滞输出，绑定具体目标）、
`CarriedMass`（>0 = 携带，削减行进力、禁止蓄力）、`AttackTarget`
（`{Point, VelocityPerTick}`，固定点传一次、随动点逐 tick 覆写）。

动作 API：`TryAssignHangAnchor(TerrainHit, ITerrainQuery)`（验证 + 建立意图）、
`ClearHangAnchor()`、`ReleaseHangDive()`、`TryStartPounce()`、`CancelPounce()`、
`PounceReach(dir)`；生命周期 `Shift`（全量平移含锚/目标/腿/卡住窗口）、
`Teleport`（清全部暂态）、`Launch`（保留 MoveTarget/AttackTarget 与锚意图，
打断悬挂/蓄力，≙ Deer 先例）。

观测输出：`Footing`/`FootingCounter`、`HangState`/`HangFactor`/`Hanging`/
`HangAnchor`/`LastHangRejection`、`PounceCharge`/`ChargingPounce`、`Jumping`/`Diving`/
`AttackCooldown`、`MovingBackwards`、`Sitting`、`StuckSignal`/`StuckShake`、
`RunCycle`/`TravelDir`/`Legs`（Pos/Planted/StepSerial）、`Forward/Up/Right`、
事件序号 `PounceLeapSerial`/`PounceAbandonSerial`/`DiveSerial`/`HopSerial`、
`LastDiveLandingTick/Point`。选点、索敌、该不该扑、伤害结算一律归宿主。

## 11. 独立入口与回归

```bash
dotnet run --project core/dropbug_smoke     # 无引擎专项（秒级）
./tools/run_dropbug_matrix.sh [输出目录]     # Godot 18 项矩阵（分钟级）
# 交互沙盒：scenes/dropbug_sandbox.tscn（WASD 行走、Shift+右键指定悬挂点、
# T 设扑击目标、Space 俯冲/蓄力、G 撤锚、C 负重、B 击飞、1/2/3 预设）
```

**无引擎 smoke**（20 门全真断言，2026-08-04 固定哈希 `C96B800B5F039447`，
1mm 微扰 `56A3ADA871066245`）：DET 双跑 bit-exact + 钉死哈希 + 微扰灵敏 + 路线覆盖
（卡住抖动/弹射/放弃/越障全部进哈希）；装配拓扑/预设/未知 ID 快速失败/出生冻结；
不对称重力（含消融翻红）；宽限期 9 tick（消融 0）；行走/头领航/失稳注力比 0.514；
18° 斜坡；越障（消融点火 0）；倒退（消融 0 tick）；悬挂判定七分支（有效/地板/斜面/
零法线/薄板/低落差/无净空）；悬挂收放（团缩 span 0.195m、静止漂移 0、消融 span
0.553m + restShrunk=false）；退出与悬挂中 Teleport 不弹飞；俯冲（最近 0.452m、
冷却 20、消融 1.044m）；蓄力（后坐 0.379m、侧对可及 3.11m 即弃、逃逸放弃、
消融照跳、负重拒绝）；卡住抖动（jitter 0.139m、消融 0.002m）；负重梯度
15.73/9.42/3.33m；表现腿（静止零步进、步频随速缩放、击飞 dangle）；生命周期
（Shift 逐位、Launch 精确注入）；查询预算（行走 avg 7.3 / max 8 rays、3 shapes）；
全程残余穿透 3.1e-6 m（碰撞关闭的悬挂节与抖动 tick 除外，理由见 §9-3 与内联注释）。

**Godot 矩阵 18 项**（哈希基线钉在脚本顶部，2026-08-04 实跑）：walk 双跑/40vs400Hz/
1mm 微扰（`8C182AF34288285A`，微扰 `E3061F62EE76DA0C`）+ slope / hop / stuck /
backward / hang / hang-exit / dive / pounce / pounce-abandon / carry / launch /
lifecycle + nimble / bulky 变体巡走（`8ACBF43362435CAB` / `2D47FFA890CF6CC1`，
preset-difference 门），每项含路线级硬断言（[DROPBUG-RESULT] 判定）。

## 12. 已知边界与后续工作

- 渲染层美化（外形、腿造型、俯冲姿态表现）按任务约定统一延后；当前沙盒为调试可视化。
- 不移植墙面/天花板攀爬：高悬挂锚需要宿主给邻近可达位（见 §5 可达性边界）。
- 斜面（≤45°）悬挂的摆位沿法线轴、俯冲判据保持世界竖直——极限角度下"高于目标"
  的语义仍成立，但未针对斜面悬挂做专项路线（仅判定分支有覆盖）。
- 越障抬升的涌现签名主要在反转时出现（§4）；正向撞矮障靠碰撞滑越 + 卡住抖动兜底。
- 蓄力压缩在 3D 刚性链下表现为整体后坐而非视觉深蹲（§7），压缩的图形层表达
  归渲染美化轮。
