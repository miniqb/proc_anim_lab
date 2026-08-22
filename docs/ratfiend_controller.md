# RatFiend（鼠煞）并列后端 —— 驼背鼠头人形怪：姿态混合、断肢与爬行

> 第 11 个并列物种后端（2026-08-22）。参考形象：苍白灰皮、修长瘦削的鼠头人形怪——
> 长吻大嘴满口尖牙、大立耳、重度驼背、双手下垂利爪、小尾巴（用户提供的恐怖谷参考图）。
> 机制基底 = Humanoid（清醒近地失重伺服木偶，零 knockdown 状态机），物种新造三块：
> **常态驼背**（倾斜站立力偶）、**走↔跑连续姿态混合**（Gait 标量）、**断肢与爬行**
> （固定断肘/膝 + 推进 ∝ 抓地肢体数）。本项目**首个可动颌**渲染件。

## 1. 核心机制

### 1.1 装配（`RatFiendFactory`）

3 chunk `[胸, 髋, 头]` 照搬人形：胸↔髋 Rigid 杆（WeightA 质量反比）、胸↔头 PullOnly 脖
（允许压缩——「耷拉」的构件基础）；装配完显式钉定 RotationChunk（CLAUDE.md §6.3）。
不加第 4 chunk——驼背是平衡态**倾角**自由度，不是拓扑自由度。

- 腿 = 2 × Lizard `Limb`（唯一跨物种白名单边 `RatFiend>Lizard`，与 Humanoid 同款），
  anchor=髋，**LookaheadTicks=10 硬性**（双足前瞻点循环，Humanoid §3.5 结论）、Pair 互绑、
  对角相位种子。修长感靠 LegLength 0.75 / ChestHipsDist 0.55 / 半径小；
  LegSpeed 0.3 / StepLength 0.6 留在可行域（CLAUDE.md §6.5）。
- 臂 = 2 × 物种私有 `RatArm`（Humanoid `Arm` 的同构克隆 + `Severed` 标志 +
  `EffectiveLength` 断后减半）。**不白名单引 Humanoid.Arm**：断肢语义要改臂长钳制本体，
  为新物种动别家后端比动共享层更糟。
- 参数 `RatFiendParams` + `Snapshot()` 出生冻结（DropBugParams 模式）；
  `ById` 未知 ID 抛异常快速失败。

### 1.2 常态驼背 = 倾斜的站立力偶目标轴（显式，非阻尼差）

```
postureUp = normalize(worldUp·cosθ + Facing·sinθ)      // θ = HunchAngleDegrees（gaunt 25°）
Uprightness 与 ApplyStandCouple 全部改对 postureUp 度量/施力
```

失重伺服体制下站立力偶永远赢——把目标轴倾斜就是把**整个平衡态**倾斜：静止、走、跑
全程驼背（胸稳定停在髋前上方 sinθ×躯干长处）。头伺服/髋伺服/重力开关/撑地判定
仍用世界上方向。消融开关 `EnableHunchTilt=false` → 静立前倾塌回直立（smoke 红灯）。

**修复轮 R1（静立漂移）**：初版胸/髋阻尼取 0.9/0.86「近平」，实测静立 300 tick 净前漂
0.65m。根因：WeightedPush 每 tick 注入动量守恒，但**衰减系数不同的两端稳态速度不再抵消**
（v_i = a_i/(1−d_i)，倾斜力偶的水平分量被阻尼差整流成净动量 ≈0.002 m/tick）。
修复：胸/髋阻尼**逐字相等**（0.9/0.9）——稳态速度对回到动量零，漂移消失
（后半窗漂移 0.649 → 0.009m）。Humanoid 靠阻尼差做巡航弓背，本物种不需要它。

**修复轮 R2（行走驼背归零）**：阻尼拉平后行走驼背从 0.564 塌到 0.000——巡航时
BaseSpeed ≫ MaxMoveSpeed×(1−damping)，胸髋**双双被钉在同一推进天花板上**，任何力偶/
注入型前倾通道都被余量钳制吸收（Humanoid LeanPush 消融实证的同一现象）。
修复：**天花板本身不对称**（`HunchSpeedBias`：胸 ×(1+bias)、髋 ×(1−bias)）——钳制
就是通道，吸收不掉；刚性杆把持续速度差转成行进前倾。bias 定 0.02：静立 25°（dot 0.42）
→ 行走 32.5°（dot 0.54）的渐进（0.08 时 53° 成俯冲，被推翻）。

### 1.3 走↔跑连续姿态混合（无 gait 枚举，涌现惯例）

进哈希的平滑标量 `Gait = LerpAndTick(Gait, 清醒有意图且可站立 ? RunSpeed : 0, 0.06, 0.01)`，
姿态权重 `r = InverseLerp(0.35, 0.95, Gait)`（End 取 0.95 不是 0.8——RunSpeed 0.8/1.0
两档的稳态姿态要可区分，smoke 单调断言）。r 驱动三路连续混合：

- **头耷拉↔抬头**：头伺服 aim = `lerp(norm(Facing−up·0.9), norm(Facing+up·0.08), r)`
  （前下 42° 耷拉 → 平视微仰）；爬行覆盖 `norm(Facing+up·0.2)`；攻击（GrabTarget 非空）
  覆盖为直视目标。轴外顶改沿 aim 顶（低头时顶向前下，防与伺服目标打架）。
- **垂手摆臂↔前伸欲抓**：手臂链走/跑分支按 r 混合目标点与追猎速度（§1.4）。
- **嘴微张↔大张**：`MouthOpen = clamp(0.15 + 0.55·r + MouthDrive)`（纯派生量不进哈希；
  `LastMouthOpen` 由 Gait 历史派生供渲染插值）。

Gait 必须内核平滑并进哈希：手臂目标点是物理量（RatArm 碰地形、断肢交接读它的 Pos/Vel），
姿态平滑交给渲染层会让物理端目标点瞬跳、双跑不定。`CrawlFactor` 同理（渲染 dorsal 混合/
肘 pole 翻转的连续驱动）。

### 1.4 手臂通道（TickArms 优先级链，Humanoid 扁平 if-else 形态）

每手固定序取第一条命中：① 昏迷→垂落；② 断臂→垂落（EffectiveLength 钳制短摆，永不
参与撑地）；③ **攻击抓取**（GrabTarget）→ 沿「胸→目标」满伸（可及半径钳制），观测
`HandsOnTarget[i]` = 手到目标真实距离 < GrabContactRadius；④ **爬行撑地**（§1.6）；
⑤ 走/跑混合：

```
swing = clamp(dot(对侧腿脚端 − 髋, Facing) / 腿长, −1, 1)     // 右臂随左腿
walkT = 胸侧下方垂位 + Facing·(0.08 + swing·0.25)            // 摆动读对侧腿相位：
runT  = 胸 + Facing·(臂长·0.8) + 侧向 0.22 + 上抬 0.05        //   零新增状态、天然反相、
HuntPos = lerp(walkT, runT, r)；HuntSpeed ×lerp(0.25, 1, r)   //   随真实步频自适应
```

⑥ 闲置→慢速垂到身侧（Humanoid 闲置分支原样——驼背垂手常态剪影）。
不搬 knuckle-walk 机器（KnucklePos/俯仰泵/扇扫链）——本怪直立时手从不撑地。

### 1.5 断肢（`Sever`，固定断肘/膝——标志位路线，不换实例）

Daddy 的换实例路线是因为触手段链的段数是构造自由度；本怪四肢各是**单粒子**，断肢 =
标志位 + 有效长度减半，粒子保留（哈希折叠形状不随断肢变、渲染画到粒子即肘/膝、
Shift/插值不断链）：

- **臂断**：`Severed=true + ForceRelease`——此后链只给垂落，粒子即残肢肘端
  （渲染/命中直接消费）。走路不受影响（臂本不承力，smoke 断言里程偏差 0.0%）。
- **腿断**：`ForceRelease + JointDist×0.5 + 停用步态机`（此后控制器手动积分残肢膝端
  粒子：阻尼 0.85 + 垂位弹簧 + 重力 + 距髋钳制 + SpherePenetration 出地形）——半长 Limb
  若继续走步态机，FindGrip 落点计抓地会污染推进计数。**存活腿 `Pair = null`**：
  Lookahead 释放 guard 的 `Pair.GripCounter > 0` 会被断腿的冻结计数卡死，存活腿每步
  拖到 1.75× 失效阀才松（`Limb.cs` 的 `Pair is null ||` 短路是现成逃生口，设计期核实）。
- **摔倒涌现**：`ApplyGravity = !(Conscious && Grounded && CanStand)`——断任一腿即重力
  回归、直立伺服静默，身体自然倒下。零过渡状态机、零计时器（实测断腿后 6~7 tick 内
  胸高跌破站高 55%）。
- 返回 `RatFiendSeveredLimbState`（末端粒子 Pos/LastPos/Vel 原样交接——断落瞬间渲染
  插值无缝，Daddy 先例）；已断再调 throw；昏迷可断；`staggerImpulse` opt-in 默认零
  （回归路线不传 → 基线不受影响）；`SeverSerial` 单调事件序号（渲染抽搐按沿触发）。
  单向不可逆（不做接肢；重开 = 宿主重建控制器）。

### 1.6 爬行（Crawling = 清醒且断了任一条腿）

- **重力常开**，躯干贴地靠 chunk 地形碰撞 + SurfaceFriction（Vulture 栖息爬行体制），
  零悬浮伺服；有支撑时微小抬鼻力偶（吻部离地）。
- **手臂撑地 = 控制器层 plant-and-trail 子链驱动哑粒子 RatArm**（Humanoid 手撑地的
  既有形态，不给臂造 Limb）：入场门 = `Grounded`（摔倒还在空中不许抢先扎手，零额外
  射线）；找点 = 胸前 `EffectiveLength·0.55` 处竖直下探（tick 奇偶交替，每 tick ≤1 根）；
  锁定后身体从旁爬过 = trail；合法窗（身后 0.25 / 身前 1.1× / 距离 1.4×有效臂长）超限重找。
- **存活腿**照常 `leg.Tick`——髋贴地时 FindGrip 自动退化成短促蹬地，`Gripping` 计入。
- **推进 ∝ 抓地肢体数（涌现核心）**：`force = CrawlForcePerLimb(0.011) × GripCount ×
  RunSpeed`，胸髋各按 CrawlMaxSpeed(0.045) 余量钳制。抓地计数 = 撑稳的手（有撑点 +
  吸附/够近 + 地形接触）+ 抓稳的存活腿，读上一 tick 收尾状态（固定序确定性滞后）。
  消融开关 `EnableCrawlGripScaling=false`（引擎改常数）→ 断肢里程单调必塌（smoke 红灯：
  3/2/1 肢 = 23.2/20.4/10.4m → 消融后全部 24.1m）。
- **调头两件套（R8 修复轮）**：目标在身后时——① `Facing` 限速回转
  （`CrawlTurnRatePerTick` 0.06 rad/tick ≈ 180°/1.3s，反平行退化绕重力上轴；站立仍
  瞬时置向）；② 胸沿 Facing 推、**髋沿「追胸」的拖车轴推**（胸−髋投影到地面）——胸髋
  同向推绕不出转矩，Facing 转了身体轴还倒着。直线爬行两方向都收敛等于 effMove，
  逐位不变；调头时身体被胸拖着画弧转身、头绕外侧扫过。
- **爬台阶 = 手拉体升（R9 修复轮）**：撑稳的手高出 chunk 底面（`CrawlClimbDeadzone`
  0.04m 死区外）→ 该 chunk 竖直速度朝 `CrawlClimbMaxRise`(0.035) 伺服，注入 ≤
  `CrawlClimbGain`(0.5) × 高差（临顶自然减力）；chunk 底面追平撑点标高即熄火——身体
  正好落上台面，自终止。同时**豁免滑墙**：正对台阶立面时 `SlideAlongWalls` 会把意图
  消成零（头对头 slid 长度归零），推进与拽升双双熄火，所以「正在攀台阶」（撑点高出
  胸底死区外）期间不滑墙。平地撑点与 chunk 底面同高 → 两者恒不触发，**平地爬行
  （含调头）逐位不变**。可爬高度无显式参数：高过「探针举高 + 胸高」的台面探不到
  （起点陷入固体 → 零法线拒收），涌现上限 ≈ `CrawlProbeRise`(0.3) + 胸径 ≈ 0.5m，
  更高的立面自然回到「墙」语义（滑墙绕行）。
- **四肢全断蠕动**：`GripCount==0 且有移动意图` → 周期性内力偶
  `WeightedPush(胸, 髋, Facing·0.002·sin(2π·tick/60))`——动量守恒内力靠摩擦整流，
  近零位移的挣扎感（smoke：2000 tick 位移 0.15m < 0.5m 上界）。

### 1.7 攻击接缝（最小面——玩法 100% 宿主侧，Daddy 竞技场惯例）

刻意不做 TentaclePlant/Daddy 式的内核抓取通道（触手物理才需要 Reached/Held/
PositionCorrection 全套）；接缝只有：输入 `GrabTarget`（Vector3?，逐 tick 写/清）+
`MouthDrive`（0..1）；观测 `HandsOnTarget[2]` + `MouthOpen/LastMouthOpen` + 手粒子位置。
宿主自己判定双手到位、计时咬合（MouthDrive 脉冲 + 自己数咬合次数）、把玩家钉在双手间
（修正 = 双手中点 − 玩家，**全量交付**——TentaclePlant 重目标离手的坑）、放开与冷却。
头朝向在 GrabTarget 非空时由内核自动转向目标；咬合前突是渲染层化妆。

### 1.8 确定性折叠（`FoldState`，DropBug 模板）

FoldBody → Facing → Gait → CrawlFactor → GroundedCounter → SeverSerial → 4×severed →
逐腿（Pos/Vel/ReachingForTerrain/HasGrip/GripCounter）→ 逐臂（Pos/Vel/Mode/GrabPos/
ReachedSnapPosition/TerrainContact/Severed）。MouthOpen/HandsOnTarget/CrawlGripCount
是派生量不折叠；Last* 是派生历史不折叠。普通路线从不调 Sever → 两族基线正交。

## 2. 品种预设

| ID | 定位 | 关键差异 |
|----|------|---------|
| `ratfiend/gaunt` | 基线：修长驼背苍白鬼怪 | 表内默认值；HunchAngle 25° |
| `ratfiend/dusk` | gaunt 的**暗剪影调色变体** | 体格参数逐位相同只换 ID（调色板归渲染层）；矩阵断言 dusk/walk 哈希与 gaunt **逐位相等** |
| `ratfiend/broad` | 重型 | 大体格短脖、HunchAngle 22°、腿慢步大、爬行力略低 |
| `ratfiend/whelp` | 幼体 | 小体格快节奏、HunchAngle 28° 驼得更深 |

## 3. 正式渲染（`RatFiendFormalRenderer`，渲染件七号——首个可动颌）

- **头 = 颅吻一体变径扫管**（5 站楔形颅→细长吻，鼻尖渐暗）。**刻意不用共享 SphereMesh
  头**：`TubeMeshBuilder` 顶点色烘焙包裹漫反射（ShadeAmount 0.22），与平色球在苍白色下
  会在拼缝处露 ~20% 亮度断层（近黑的 Humanoid 头看不见这个坑——设计期新发现）。
- **可动下颌**：绕耳下铰点旋转的 3 站短管，张角 = `jawRest(3~8°) + (gape(45~55°) −
  rest)·mouthSmoothed`；口腔 = 沿两颌角平分线的暗红喉管，半径 ×clamp(mouth·1.6)
  闭嘴缩进颌内。**抗抖四件套**：头帧每帧只算一次、张角单独低通（开 25/合 18 非对称）、
  牙 = (头帧, 张角) 纯函数零累积、jawRest ≥3° 永不真正咬死。渲染 target 用
  `lerp(LastMouthOpen, MouthOpen, alpha)` 插值 + 附加化妆（闲置微呼吸颌/跑步喘/
  挣扎加张/断肢尖叫）。
- **牙**：脏白 AddBlade 沿上吻下缘/下颌上缘两条唇线（上 5~8/下 4~7 每侧、12% 缺牙、
  前犬齿 ×1.2~1.6，seed 冻结），根色暗牙龈、上下错半齿位防闭合 z-fight。
  **有意偏离 Humanoid 的「头色颅骨锯齿」哲学**——本怪是真嘴，恐怖来自嘴。
- **躯干**：六点脊柱，驼峰独立控制点上移到肩位且为全身最宽（1.08×胸径）+ 掐腰 +
  椎骨凸 AddKnob；呼吸（Lizard 门控，爬行时频率 ×2.7 幅度 ×0.6 急促浅喘）；
  **爬行 dorsal 双源 slerp**（站姿 −facing 系 / 爬行 up 系按 CrawlFactor 混 + 低通 k=6）
  ——防脊轴转水平后驼峰方向乱转。
- **四肢**：TwoBoneIk 肘膝，pole 三态混合（垂手肘朝后外/前伸肘外压/**爬行肘朝上**）
  逐臂低通；钩爪 = 手瘤 + 逐指两枚刀片折钩（seed 指长/弯度，撑地时向抓面摊开）；
  腿膝剖面 2.2~2.6 档。**断肢** = 肩/髋关节→内核残肢粒子的短残段（真粒子，插值无缝）
  + 暗红断口瘤 + 碎肉小刀片（seed 角度）。
- **尾**：渲染侧 verlet 短锥链（3~4 节，环纹交替明暗 = 裸鼠尾）。**修复轮 R3**：
  尾根方向初版直接用 dorsal——爬行时 dorsal = 世界上，尾巴直挺挺戳向天（首轮截图
  实测）；改按 CrawlFactor 混向水平「身后」方向 + 加重下垂。
- **动态**：咬合冲击（mouthSmoothed 下行沿 → 0.12s 头前突 + 牙提亮）、断肢抽搐
  （SeverSerial 沿触发 0.6s jitter + 残肢甩 + 嘴尖叫）、爬行挣扎（驼峰侧向蠕摆 +
  瞪眼 + 急喘）、眨眼 PRNG（Nervous 低 → 少而慢更瘆人）、耳后折（跑步/受创）与耳颤。
- **调色**：用户定档苍白灰皮为主（gaunt/broad/whelp；暗鼻/暗爪尖/暗眼窝拉回剪影感）；
  dusk = 近黑身 + 骨白鼠颅（Vulture 先例）。`srgbVertexColors: true`。
- **修复轮 R4（白盒标记漏显）**：白盒渲染器的目标标记球在 Draw 里按业务条件覆写
  Visible，把 SetVisible(false) 顶掉——正式视图上浮着黄/粉标记球（首轮截图实测）。
  修复：白盒记住整体可见态，标记 Visible 与其相与。

## 4. 沙盒与回归

### 4.1 回归沙盒（`scenes/ratfiend_sandbox.tscn` + `scripts/ratfiend_sandbox/`）

平地 + 0.3m 台阶（walk 环线跨越）+ 转向墙。交互键位：WASD 跑（Shift 慢走看驼背摆臂）、
**K 断掉下一条肢体**（腿→臂演示序，看三肢爬行）、R 重生复原、T/Y 设/清抓取目标、
M 嘴开合、B 击飞、V 正式/白盒、1~4 预设。长路线用**有界巡逻**（两点乒乓 MoveTarget）
——平地带宽 ±22m，恒向 MoveDir 会走出地板边坠落（设计期修复：攻击路线放开后的
行走曾走出边缘，travel 从 101m 异常值收敛到 37m）。

### 4.2 独立入口与回归

```bash
dotnet run --project core/ratfiend_smoke        # 无引擎冒烟（16 门 + 4 消融红灯）
./tools/run_ratfiend_matrix.sh [输出目录]        # Godot 矩阵 23 项 + smoke
```

**无引擎 smoke**（`core/ratfiend_smoke/Program.cs`，基线 `ExpectedHash =
0x61B69A122056878D`，R8 调头修复重钉）：ASSEMBLY（拓扑/钉定/Pair 互绑/ById 抛异常/出生冻结/dusk 同构）、
DET（1400 tick 主路线含固定 tick 断腿→爬行→断臂：双跑 bit-exact + 微扰必变 +
routeCovered）、WALK（里程 68m + 驼背几何：静立 200 tick 后 dot(胸−髋, Facing)=0.42
≥0.15 + 后半窗漂移 0.009m<0.15 + **消融红灯 ①**：EnableHunchTilt=false → 静立前倾
−0.001）、POSTURE（RunSpeed 0.2/0.5/0.8/1.0 稳态：头高 [−0.37,−0.28,−0.06,+0.04]、
嘴 [0.15,0.29,0.56,0.70]、前伸 [0.31,0.48,0.83,1.01] 三链严格单调）、ARM-SWING
（反相摆 89%≥60%）、SEVER-API（播种逐位连续/叠断 throw/昏迷可断/stagger 精确）、
SEVER-ARM-WALK（断臂行走偏差 0.0%<5%）、SEVER-LEG-CRAWL（7 tick 摔倒 + 爬行 35.0m +
存活腿步频 25 循环 + Pair 已清）、CRAWL-TURN（爬稳后目标反向：Facing 每 tick 回转
≤0.06 rad + 48 tick 收敛 + 回程 25m + 头髋水平距全程 ≥0.32m（实测 0.94）+ 身体轴
真的转过来 endSpineAlign=1.00 + **消融红灯 ③**：限速调成 π/tick（=旧瞬时置向）→
头髋最小距塌到 0.048m 穿插带）、CRAWL-STEP（断腿爬行撞 0.3m 台阶：翻上后继续爬
（endX=27.5 > 台阶沿+3）+ 胸落台面（Y=0.51）+ 直线不被滑墙滑歪（zDrift=0）+
**消融红灯 ④**：CrawlClimbGain=0（撤手拉体升）→ 卡死在台阶面前 X=2.79）、
SEVER-MONOTONE（3/2/1 肢里程严格递减 + **消融红灯 ②**：
EnableCrawlGripScaling=false → 极差塌为 0）、SEVER-ALL（蠕动 0.15m<0.5 不逃逸）、
ATTACK（2 tick 双手到位/满嘴/放开归零/不可及永不误报）、LIFECYCLE（Shift 逐字段精确含
断肢态/Teleport 作废/Launch 恢复）、QUERY（rays max 21≤40、shapes max 7≤8）、
PENETRATION（残余穿透 0 <2mm）。

**Godot 矩阵 23 项**（`tools/run_ratfiend_matrix.sh`，哈希 2026-08-22 实跑钉定；
R8 调头修复重钉 sever-leg / sever-both-legs / broad- / whelp-sever-leg 四条——巡逻
乒乓折返落在爬行段，正是调头行为改变的路线；R9 新增 crawl-step，其余逐位不变）：
walk-a/b（双跑 diff）、walk-40（40Hz 时基不变）、perturb（1mm 必变）、run（跑步姿态：
maxRunBlend 1.0 + 跑步窗嘴均值 0.70≥0.6）、yank（击飞 621 tick 落地恢复续走 10.7m）、
sever-leg（摔倒 506 + 爬行 33.7m 含一次乒乓调头 + 抓地 3）、crawl-step（断腿爬行翻
0.3m 台阶到台面目标点：摔倒 506 + 抓地 3 + 到点 + 末态胸 Y≥0.35 落在台面——内核
CRAWL-STEP 门的引擎侧对照，验证 Godot 射线 HitFromInside 语义一致）、sever-arm-walk（断臂巡逻
86m 不受影响）、sever-both-legs（双断腿纯臂爬 27.6m 含调头）、sever-all（全断漂移
0.15m 蠕动不逃逸）、
attack（喂目标后 2 tick 双手到位 + 咬合脉冲满嘴 + 放开）、sever-during-attack
（攻击中断执行臂 → 断臂手的「抓住」观测立即消失——竞技场并发规则的内核侧保证）、
dusk/broad/whelp 变体（walk + 断腿爬行，变体只跑 walk 会漏检断肢——DropBug 评审 P2
同款教训）+ 交叉红灯：double-run / 40-vs-400 / perturb / preset-difference /
**dusk-parity**（dusk 哈希必须与 gaunt 逐位相等——体格同构的免费断言）。

### 4.3 枪击竞技场（探索场景，不进矩阵）

`scenes/rat_arena.tscn` + `scripts/rat_sandbox/`：第一人称玩家 + 枪 + HUD。
命中判定 = 宿主侧纯数学（`RayHitMath` 射线 vs 球/胶囊，从 Daddy 竞技场提取共享）对
13 个部位代理（头球/躯干三件/每肢两段胶囊，肘膝点用 `RatFiendJointMath` 与渲染件同源
公式），最近 t 即部位；命中臂/腿 → `Sever` 固定断肘/膝 + 断落物（`RatSeveredPiece`
3 点 Verlet，Daddy SeveredPiece 砍 Traction 的两态版）。相位机 Chase→Strike→Grabbed→
Recover（咬一口就放开 + 冷却，用户定档不做挣脱连打）；断腿后爬行追逐是内核涌现，
宿主不设 Downed 相位。调参全 [Export]（Daddy 纪律）。

## 5. 有意偏离与设计裁定清单

1. 驼背 = 显式倾斜姿态轴 + 天花板差分，**不是** Humanoid 的阻尼差路线（§1.2 两轮修复）。
2. 断肢 = 标志位 + 长度减半，**不是** Daddy 的换实例（单粒子肢体没有段数自由度）。
3. 攻击接缝 = 最小面（GrabTarget/MouthDrive/HandsOnTarget），**不是** TargetSnapshot/
   TargetEffect 全套（玩法 100% 宿主侧）。
4. 牙 = 脏白真牙 + 可开闭嘴，**有意偏离** Humanoid「不是嘴」的颅骨锯齿哲学。
5. 走/跑 = RunSpeed 连续油门 + Gait 平滑标量派生姿态，**无 gait 枚举**（涌现惯例）。
6. MouthOpen 纯派生不进哈希；Gait/CrawlFactor 进哈希（物理量目标点的平滑源）。

## 5.5 对抗性评审轮（2026-08-22：四分区并行评审 + 逐条对抗核实，7 初判 → 3 核实）

| # | 核实为真 | 修复 |
|---|---------|------|
| R5（high，假绿） | smoke LIFECYCLE 的 `Shift(+512)` 把身体挪出 200m 有限盒地板——推进无 Grounded 门，失重巡航照常累积里程，「续走/击飞恢复」两个门靠**空中漂移**通过，没测到落地恢复 | 该检查改用无限半空间地板；两门补 `Grounded && !ApplyGravity` 条件（空中漂移不再冒充续走） |
| R6（low） | `RatFiendJointMath.Dorsal` 的 Slerp 在两源反平行（摔倒翻滚瞬间脊轴倒置 + 三向量近共面）时退化返回零向量/噪声方向——文件里唯一没有退化守卫的混合 | 混合结果加 LengthSquared 守卫，退化回退站姿源（误差本被 0.22m 瞄准冗余覆盖，属鲁棒性缺口） |
| R7（low） | 竞技场断落物 X/Z 钳位面取内径名义平面，但四墙墙心骑在该平面上、向房内突出半墙厚 0.09m——断肢可静止在墙体内、视觉半埋（Daddy SeveredPiece 模板继承的既有偏差） | 钳位面各内缩半墙厚到墙体内表面（地板/天花板走面恰在名义平面，不缩） |

被驳斥的 4 条（核实代理逐条给出反证）：爬行里程用路径长非位移（推进沿意图方向，塌缩场景不成立）、
确定性退出的帧内多步竞态（误读 Godot Quit 语义）、Sever 后 HandsOnTarget 单帧残留
（窗口存在但所有宿主的消费时序都不落入）、CLAUDE.md §6.4 文字过期（本轮已同步更新）。

## 5.6 R8 爬行调头修复轮（2026-08-22，用户实测报告）

**症状**：爬行时目标在身后 → 头直接从裆下穿过去，手在头后、脚在头前，头髋穿插——
身体从不整体转向。此前该路径**没有任何断言覆盖**（smoke/矩阵的爬行全是直线段），
bug 因此存活到交互实测。

**根因是两层，缺一不可（初判被推翻一次）**：
1. `Facing = effMove` 瞬时置向——站立时有腿 + 站立力偶把身体真转过来，问题被掩盖；
   爬行时身体是贴地水平链，Facing 一帧翻 180°，头伺服/推进/手撑点全部立刻反向。
2. **初判只修 ①**（Facing 限速回转）后实测头髋最小距仍 0.050m ≈ 消融值 0.048m——
   第二层根因：推进对胸、髋沿同一方向推，**绕不出转矩**，Facing 转过去了胸髋杆还倒着，
   等效「倒着爬 + 头被拽过裆」。

**修复**（`RatFiendLocomotionController`，全部物种局部）：爬行时 ① Facing 限速回转
（新出生参数 `CrawlTurnRatePerTick` 0.06 rad/tick，反平行退化绕重力上轴；站立不受限）；
② 髋的推进方向改「追胸」拖车轴（胸−髋投影到地面）——杆被胸拖着自然回转。
两者在直线爬行时都收敛等于 effMove，**直线基线逐位不变**（SEVER-LEG-CRAWL/
SEVER-MONOTONE 数值一字不差）；主 DET 路线因摔倒过渡段脊轴未对齐而漂移，有意重钉。

**验证**：新 smoke 门 CRAWL-TURN（回转限速 0.0600 恰等上限 / 48 tick 收敛 / 头髋
最小距 0.94m ≥0.32 / endSpineAlign=1.00 / 消融红灯 π/tick → 0.048m）；矩阵重钉 4 条
爬行折返路线（sever-leg / sever-both-legs / broad- / whelp-sever-leg），其余 18 项
逐位不变；sever-both-legs tick 970→1060 连拍目检：拉弧转身、头绕外侧、无穿插。

## 5.7 R9 爬行翻台阶修复轮（2026-08-22，用户实测报告）

**症状**：断腿爬行连沙盒里 0.3m 的小台阶都上不去——实际放进游戏会被随便什么地形卡死。
（走路态过台阶没问题：腿的 plant-and-trail 落脚 + 髋高伺服天然会踩上去。）

**根因是两层耦合**：
1. **爬行推进纯水平**：胸球半径 0.21 < 台面 0.3，球心低于台阶沿，SpherePenetration 对
   立面的排斥是水平的——推进永远顶墙。手探针（起点 ≈ 胸高+`CrawlProbeRise` 0.3 ≈ 0.51 >
   台面）**早就能把手搭上台面**，但撑点只进抓地计数，没有任何「把躯干拽上去」的力。
2. **滑墙把意图消成零**：台阶立面对爬行躯干是「墙」，正对时 `SlideAlongWalls` 的
   slid 向量长度归零 → effMove=0 → 推进与（若有的）拽升双双熄火，彻底钉死。

**修复**（全部鼠煞局部）：① **手拉体升**——撑稳的手高出 chunk 底面（0.04 死区外）时
把该 chunk 竖直速度朝 `CrawlClimbMaxRise` 伺服（注入 ≤ `CrawlClimbGain`×高差，
底面追平撑点即自终止，身体正好落上台面）；② 攀台阶期间（同一判据）**豁免滑墙**。
撑稳判据与抓地计数共用同一 `HandPlanted`（数进引擎的手才有资格拽人）。平地撑点与
底面同高 → 两者恒不触发：**全部既有基线（smoke DET + 矩阵 22 项）逐位未动，零重钉**。
可爬高度是涌现上限（≈`CrawlProbeRise`+胸径≈0.5m）：更高的台面探针起点陷入固体、
零法线拒收，自动回到「墙」语义滑墙绕行——不需要显式高度参数。

**验证**：新 smoke 门 CRAWL-STEP（翻上 0.3m 台阶后续爬 24m / 胸 Y=0.51 落台面 /
零侧偏 / 消融 CrawlClimbGain=0 → 卡死在 X=2.79 台阶面前）；矩阵新增 crawl-step 路线
（断腿爬行到台面上的目标点，哈希 496782DEC1DB8EDB 首跑钉定）；其余 22 项全绿未动。

## 6. 已知边界与后续工作

- 断肢单向不可逆（无接肢/再生）；重开 = 宿主重建控制器。
- 爬行三肢相位无协调器（手 tick 奇偶 + 腿自身节律自由跑），GripCount 瞬降的推进抖动
  接受为涌现质感——只钉里程不钉平滑度。
- 低台阶爬行已闭环（R9：0.3m 台阶 smoke+矩阵双侧钉死，涌现上限 ≈0.5m）；**斜坡**
  爬行仍未专项验证；连续多级台阶（楼梯）未验证——单级机制自终止后应能续爬下一级，
  待场景实测。
- 渲染：爬行姿态的整体剪影仍偏「肉堆」（驼峰球贴地占主导）；牙在满口大张时略拥挤；
  均属调参级打磨，待交互实测后按观感收。
- dusk 调色板与 gaunt 的暗/白反转只在 ForBreed 定档层，未做运行时切换。
