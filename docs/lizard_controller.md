# Lizard 3D 控制器（基线后端 + M1~M5 产物档案）

`LizardLocomotionController` 是本项目的**第一个**运动后端，也是共享物理层（`Body` /
`BodyChunk` / `ChunkConnection` / `SphereTerrain` / `Limb`）的来源。其余九个物种后端与它
**并列**，不继承、不扩充。

实现依据为本机 Rain World `Lizard` / `LizardGraphics` / `Limb` / `LizardLimb` /
`TailSegment` 的反编译逐行核实，只移植行为结构与单位关系，不包含原游戏源码。

> 本文档是 **M1~M5 里程碑产物的详细档案** + **蜥蜴各轮修复的根因记录**。
> 装配 / 输入 / 输出契约见 [`porting_contract.md`](porting_contract.md)；
> 路线图与跨物种约束见 [`../CLAUDE.md`](../CLAUDE.md)。

---

## 1. M1~M5 产物

> 路径注意：M1~M4 期间内核在 `scripts/physics/` 与 `scripts/terrain/`，**M5 起全部移入
> `core/`**（`core/physics/`、`core/terrain/`、`core/species/lizard/`、`core/godot/`）。
> 下文保留当时的叙述，读到旧路径时按此换算。

### M1 —— 物理地基

`scripts/physics/`（纯 C# 内核：BodyChunk / ChunkConnection / Body / ITerrainQuery，零场景树
依赖，即 M5 的回迁边界）、`scripts/terrain/RaycastTerrainQuery.cs`（内核与 Godot 物理的**唯一
接缝**）、`scripts/sandbox/`（驱动/渲染/拖拽/确定性探针）、`scenes/sandbox.tscn`（白盒：地板 +
缓坡 + 台阶 + 墙）。

### M2 —— 会走路

- `Limb.cs`（腿粒子：单点追目标 IK + plant-and-trail 状态机 + 竖直投影射线 `FindGrip` +
  单侧腿长钳制，≙ RW `Limb`/`LizardLimb`）
- `LizardLocomotionController.cs`（行走驱动：推进力 ∝ 抓地腿数，**无 locomotion 状态机**，
  `MoveDir`/`RunSpeed` 唯一输入，≙ Lizard 移动块）
- `SphereTerrain.cs`（Body/Limb 共用的球-地形解算）

沙盒 WASD 行走 + 拖拽看腿自适应，脚球按状态换色（绿抓稳 / 橙迈步 / 灰蓝摆动）。确定性模式
改为脚本化路点巡走（**行走本身进哈希**）：800 tick 走约 25.7m、平均 2/4 腿抓地。

### M3 —— 地形涌现（走/爬两态，零模式分支）

≙ 研究文档 §11.6b / §12.3。

**`LizardLocomotionController` 侧**：

- **重力开关**：`FootingCounter` / `NoGripCounter` → `ApplyGravity`。抓稳 → 重力 0 + 贴地
  摩擦档 0.8/0.5；坠落 → 重力回归 + 0.999/0.3（数值直取 RW）。
- **支撑法线 `SupportNormal`**：抓地腿抓握面法线的平滑平均（平地 = 上，墙 = 墙法线）。
- 移动意图被支撑面挡住的分量**沿面内上坡重定向**——推墙自动变向上爬，同一公式覆盖斜坡。
- 推进目标射线**钉在支撑面** + `RideHeight`（≙ 瞄路径格中心；**身体不飘离墙的真正来源**）。
- 引擎极速 `MaxMoveSpeed`：墙面无阻滑升的唯一刹车——平地上碰撞/腿阻先饱和，撞不到它。

**`Limb` 侧**：

- 整套步进几何跑在**支撑系**（up = 支撑法线：走 = 朝下打、爬 = 朝墙打，**同一条代码**）。
- `FindGrip` 三类候选统一选「离期望点最近」：支撑向投影 + 锚点直射（面前有墙先够到墙面）+
  攀爬中世界向下投影（翻越棱线够到顶面）。
  > **起点必须沿「支撑法线的水平反方向」单侧排开**——沿步进方向排会叠成悬在墙面外的竖线，
  > 薄墙顶面永远打不到。
- `HasGrip` 区分真落点与摆动期空中目标。**修掉 M2 潜伏 bug**：脚追上空中胡萝卜也计抓地，
  在 M3 里它会骗过重力开关悬空飞天。

### M3 翻越三件套

快速爬墙 / 正面推墙实测逼出来的，全部 ≙ RW `FollowConnection` 的对应机制：

1. **翻越伺服**：头越过棱线后支撑向目标射线打空，补一根加深的世界向下探测
   （`CrestProbeDepth`）把推进目标钉在顶面 + 比例回中力（`CrestCentering`，≙ RW 攀爬回中
   `vel -= (pos−格中心) × k`）。否则会退回「头前 + 向上」的飞行胡萝卜，在支撑系旋转跟上前
   把身体**弹道式抛过墙顶**。
2. **顶死换步**（≙ `timeSpentTryingThisMove`）：推着走却几乎不动超过 `StallReleaseTicks`，
   强制抓得最久的腿松开重迈步。正面顶墙时身体静止，plant-and-trail 的松脚条件永远不满足；
   RW 靠随机抖动打破僵局，**确定性内核必须显式超时**。
3. **拉直力**（≙ `straightenOut`）：身体轴线背对目标（撞墙翻倒、头折叠到髋后）时头沿
   「髋→目标」强拉、髋反向推。翻倒姿态会让 `stepDir` 被反向身体轴污染、腿全部背着目标迈步，
   没有它就永久瘫在墙脚。

**确定性路线**：上坡→下坡→撞墙→爬墙→翻越 3m 薄墙→落地续走循环（2000 tick：
waypointsReached=12、gravityOff≈80%）；另有 `--route=wall`（配 `--spawn=-4,0.5,0`）做正面
推墙翻越测试。身体按重力开关换色（红 = 坠落、青 = 抓稳/攀爬）。落地冲击的单 tick 约束拉伸
通常 ~3%、偶发硬着陆 ~16%（软体压扁回弹，下 tick 即被松弛修正）。

### M4 —— 多样化与调参

手感全部收拢到一张品种参数表。

- **`BreedParams.cs`**（≙ `LizardBreedParams` 运动子集）：字段名镜像 RW（`BodySizeFac` /
  `LimbSpeed` / `LimbQuickness` / `StepLength` / `LiftFeet` / `FeetDown` /
  `LegPairDisplacement` / `LimbGripDelay` / `SmoothenLegMovement` / `NoGripSpeed` /
  `TailSegments` / `TailStiffness` + `TailTipStiffness`（`tailStiffnessDecline` 的端点式）/
  `TailLengthFactor`），单位全部本项目制；`SpineSegments` / `LegPairs` 为 3D 扩展（RW 蜥蜴
  固定 2 锚 4 腿）。**纯出生配置**——工厂读表装配，内核运行时不回读、零行为分支。
- **`BodyFactory`**（≙ `LizardBreeds`）重写为通用装配器：脊柱 = N chunk 的 Rigid 链
  （头…中段…髋）、腿对沿脊柱均匀分布锚定（相邻对出生错位相反 = 对角步态相位种子）、
  尾巴 = 渐细 PullOnly 链（`WeightA` 沿链递减）。

**四预设**：`default`（M2~M3 调教的基线四腿，取值与旧硬编码逐位一致）、`heavy`（绿蜥系：
3 节脊柱 ×1.2 体格、宽站距、硬长尾、`SmoothenLegMovement=false`）、`sprinter`（黄蜥系：
0.85 体格快腿短尾）、`hexapod`（3 脊柱 3 腿对；三锚六腿拓扑有 RW Caramel/SpitLizard 先例，
3D 参数为本项目调教）。沙盒数字键 1~4 现场换品种、`--breed=` 供无头回归。

#### 闲置休息姿态（≙ RW `Limb.Mode.HuntRelativePosition`）

连续 `IdleAfterTicks` 找不到落点且 `RunSpeed≈0` → `IdlePose`，追逐目标每 tick 切「锚点沿支撑
方向垂下、向本侧微撇」的休息位，脚垂回身侧；有输入立即恢复迈步。`HasGrip` **恒 false 不骗
重力开关**；有移动意图时整套逻辑休眠（默认路线哈希不变）。

回归：`--route=stand --spawn=-6,3.7,0` 空降墙顶站桩，悬空侧双脚应 `idle=True` 收拢。

#### 防折叠支柱（≙ RW `Lizard.bodyChunkConnections[2]`，用户实测逼出来的）

距离约束链**没有弯曲刚度**——头-中、中-髋两条距离都满足时链条照样可 180° 对折，heavy 刚上墙
时头会折到中段下面钻着爬。RW 蜥蜴的对策是第三条「头↔第三节」Push-only 连接：
`RestLength = 节长 × (1 + bodyStiffnes)`，伸直（2 节长）永不触发、折叠低于下限才软推撑开 =
参数化的最大折角钳制。

移植为 `BreedParams.BodyStiffness`（粉蜥 0.2 / 绿蜥 0.5 / 蓝蜥 0，直取 RW 品种表）+ 工厂对
每对隔一节 chunk 加 `SoftOnly` PushOnly 连接（弹性走 RW 原公式 `1 − Lerp(0.9, 0.5, stiffness)`，
**Pos/Vel 同步修正** ≙ RW `BodyChunkConnection.Update` 原语义——只写 Vel 的弱推压不住抓地腿
锚定的折叠）。`SoftOnly` 连接不进硬求解器、不计 `LastRelaxDeviation`、渲染不画线。

指标 `maxFoldIntrusion` / `foldTicks` 进 `[METRIC]`。2 节脊柱无隔节对，default/sprinter
哈希不受影响。

#### 调参教训（heavy 第一版近瘫的根因）

腿慢（`LimbSpeed` 0.10）+ 步幅大（`StepLength` 0.85）+ 外撇远（0.7）**三者叠加**会让脚永远
追不上身体，平均抓地 0.6/4 → 重力开关长期打开。

> **腿参数必须留在可行域（速度 ≥0.12、步幅 ≤0.75）附近**，「笨重感」交给脊柱节数 / 体格缩放 /
> 站距 / 尾巴刚度表达——它们不碰抓地循环。

### M5 —— 内核抽离

回迁 = 拷走 `core/` 一个文件夹。详见 [`porting_contract.md`](porting_contract.md)。

- `core/ProcAnim.Core.csproj`：内核 classlib，**只使用 GodotSharp NuGet 的纯托管数学结构**
  ——不引 Godot.NET.Sdk（挡住场景树源生成器）。注意 GodotSharp 包里 `GD`/`Node` 仍编译期可达，
  真正的强制是 smoke 的 **TypeRef 边界扫描**：允许清单 `Vector3`/`Mathf` 之外的 `Godot.*`
  引用即回归 FAIL。命名空间去 Lab 化：`ProcAnimLab.{Physics,Sandbox,Terrain}` → `ProcAnim.Core`。
- `core/godot/RaycastTerrainQuery.cs` 引擎适配器随文件夹走、归游戏程序集编译。
- `core/smoke/`：无引擎冒烟回归（纯 .NET console，退出码判定）。哈希折叠下沉为内核
  `DeterminismHasher`，沙盒探针与冒烟共用同一实现（两边哈希可互证）。
- **抽离质量门**：9 配置全矩阵与抽离前基线**逐字节 diff 为空**——移动文件 + 改命名空间 +
  程序集拆分 + 哈希器下沉，零行为漂移。

---

## 2. 修复轮记录

### 2.1 RotationChunk 消费端（2026-07）

`BodyChunk.RotationChunk` 的机制本身见 [`../CLAUDE.md`](../CLAUDE.md) §6 与
[`porting_contract.md`](porting_contract.md) §1.1b。蜥蜴侧的**消费**是
`LizardLocomotionController.TickLimbs` 的每锚点步进方向（≙ `LizardLimb`
`a = DirVec(rotationChunk→connection)` 后与目标 Lerp 0.4；髋锚翻转 ≙
`connection.index==2` 的 `a *= -1`，**按锚点判定不写死索引**）。

头/髋锚 = 脊柱长基线轴，**与旧全局 stepDir 按 IEEE 逐位相等**（负号与除法可交换）——
default/sprinter/heavy/wall/stand/carrot 六条矩阵哈希 + smoke 基线改动后逐位未动，
**自带对照组**。唯 `hexapod`（中段锚腿对改跟本段朝向）按设计漂移换新基线。

> 工厂**显式钉定**脊柱指向（≙ RW Deer 构造后重申指向的先例），不学 RW Lizard 靠「防折叠
> 连接恰好最后建」的顺序巧合——我们的尾链建在最后，巧合会让髋参照尾根，**软尾摆动会污染
> 步向**。

出生摆位的世界 Z 侧向仅是一次性相位种子（出生脊柱竖叠、朝向退化竖直），运行时脚位全由每
锚点 stepDir 接管。

### 2.2 SpineFollower（多节脊柱爬墙 V 形折叠）

**根因**：`ApplyLocomotionForce` 原先让链尾 `Hips` 直接追「目标点身后一节」，偏移量取
`SpineLength`（= 脊柱**全长**）。两节脊柱（Head/Hips 相邻）时这恰好退化成正确语义；
**三节以上（heavy/hexapod）时中间节完全没有驱动力**，两条独立刚性连接在「头到髋直线距离 <
脊柱全长」的欠约束自由度上被动折成 V 形，且抓稳后重力关闭，**错误姿态可稳定维持**。

反编译核实（`Lizard.cs:2277-2280`）：RW 原版只用 `bodyChunkConnections[0].distance`
——**单节**长度——驱动 `bodyChunks[1]`，链尾 `bodyChunks[2]` **从不被直接追踪**，只靠连接
约束被动拖行。

**修复**：新增 `SpineFollower`（≙ `bodyChunks[1]`，工厂钉定为 `chunks[1]`）与
`HeadLinkLength`（单节长度）承接这个追踪力，`Hips` 恢复纯被动拖行。

两节脊柱下 `SpineFollower` 与 `Hips` 是同一 chunk 且 `HeadLinkLength` 数值与原 `SpineLength`
相同——七条矩阵配置与 smoke 哈希**逐位不变（有数学证明，非仅回归验证）**。`heavy`/`hexapod`
换新基线：官方巡逻路线下头-中-髋夹角由折叠态稳定 ~53° 回升到稳态 ~177°。

**修复轮二**（外部评审）完成 `straightenOut` 的 RW 对齐（判轴/施力点切到 `SpineFollower`），
并由真断言暴露 `wall-hexapod` 的 82 tick 持续折叠。

**修复轮三（墙角残留深挖）** 纠正了轮二的三点误判：最长窗口实际从墙边 180° 掉头开始，不是
单一「髋部钉地」；**腿粒子不向身体回传反力，不会直接钉住 Hips**；RW Caramel/SpitLizard
本就有 chunk 0/1/2 三锚六腿拓扑。真实链路是「尾链跨墙卡链点火 + 平面 180° 掉头 + `misLocal`
沿目标轴促使头髋互穿 + 拉直力受低抓地衰减 + 约束修正被墙地碰撞覆盖」。分层修复：

1. `misTarget` 只做目标对齐，本地折叠改沿 Head-Hips 弦向撑开；输入反转追加绕 `SupportNormal`
   的确定性侧向转身，零输入清旧意图。
2. 移植 RW `straightenOutNeeded` 跨 tick 记忆 + 新增独立于头速的 `SpineCornerStuckTicks`，
   所有计数强度显式 clamp。
3. 接触法线用固定容量 `ContactManifold3D` 收集，非正交法线固定迭代投影；卡角 ≥10 tick 时
   **只恢复碰撞相对松弛末新增的** SoftOnly 支柱违反，最终候选再做 MTD 穿透校验——不重跑整套
   约束、不重复摩擦。
4. 同阈值启用 RW 式 `TerrainSqueeze`（只缩 Hips 地形有效半径，1→0.05，下限 0.025m）。
5. 回归改查支柱实际违反（不再用跨品种统一 `<100°` 假门槛），并拆出 `turn-hexapod` /
   `wall-turn-hexapod` / `wall-tail` / `wall-corner` 四个**事件相对**场景。

两节脊柱走显式 legacy 分支，default/sprinter/smoke 基线不受新恢复控制器影响。

### 2.3 拖尾点失稳（wall-pose）

**症状**：上墙后髋不贴墙水平悬空；顶死推墙时髋摆到正侧方 ~80° 锁死成横向滑移步态。

**根因**：推进拖尾点 `target + Dir(target→Head) × 节长` 在 LookAhead(0.5) > 节长(0.3) 时
**恒在头前 ~0.2m**。follower 注入是方向性定幅（不随距离缩放），刚性连接把 follower 锁在头
周围一节长球面上 → 吸引子在球面内**前**侧 → **稳定平衡位是「髋在头前」**，我们要的拖尾位是
**反**平衡点，只靠头部运动的拖行（~v/L，满速时约失稳泵 3 倍）压制——头一顶死失稳泵独大。

**修复**：拖尾点挪到头后 `Head + Dir(target→Head) × 半节长`（吸引子挪进球面内背离目标侧，
拖尾位变成稳定极点；系数不取 1.0 防 `Dir` 两点重合退化）。平地 smoke 走距仅 −1.4%，
wall 翻越 11→14、wall-hexapod 11→13 **反升**。stand/embed/wallside 无移动意图不触推进力，
哈希不变。

顶死 + 1cm 侧扰动与无扰动对照固化为 smoke `[CORE-WALL-POSE]` 真断言。

> 复现中确认的两个次级问题（Fallback 胡萝卜可达性校验、站稳态雕像化）**未修**，独立成后续工作。

### 2.4 上墙弓起（RearBrace，链尾静息位回摆）

**症状**：heavy 刚上墙的一段时间脊柱以中段为铰向墙外弓起、髋部悬在墙外 0.5~0.8m、尾巴悬浮
成弧，随爬升缓慢回贴（最坏 1.5s）。

**四层根因**（无引擎逐 tick 取证 + 消融）：

- **形成**：上墙交接期头被拉上墙而链尾无任何驱动（被地板摩擦 + 尾根回拉钉住），三节脊柱唯一
  自由铰只能折弯。
- **变丑**：折角落在 97°~150° 的**恢复死区**（`misLocal` <120° 才介入、防折叠支柱 0.45m
  折算 <97° 才触发），且两者都是沿头髋弦「**方向盲**」撑开——撑出来的是水平旗杆不是贴墙直线；
  加上球体重叠的视觉放大。
- **维持**：尾根机械回拉（**纯化消融**：只把尾根 `WeightA` 置 0 与整条摘尾逐项同值——尾链
  Footing 记账贡献为零）+ 后腿超出 `JointDist` 0.63m 可及圈被 `FindGrip` 逐 tick 全拒（最长
  60 tick 无锚，抓地腿减半又反削唯一贴墙力）+ **抓稳关重力让悬臂力中性**（把后半身摆回墙面
  的钟摆力正是被开关关掉的那个）。
  > RW 2D 侧视**不存在**「垂直于墙伸出」这个自由度——3D 化新增的维度，**无反编译对策可抄**。
- **恢复**：爬行拖拽被动对齐（τ≈25 tick），髋进可及圈后腿一抓即拉平。

**迭代一（已废弃，被用户实测否决）**：沿 `−SupportNormal` 朝最近支撑面吸的贴面伺服。推墙
场景达标（重抓 +59→+30），但**上墙后停驶**时没有推进力抵消，把停在近直姿态（174°）的身体
**主动折进墙角**（135° 锁死、髋吸到 0.30m 贴死）——「朝面吸」在悬臂几何里就是**压链方向**，
缺的是「绕中段回摆」的**切向**分量。

**迭代二（现行）**：`RearBraceGain`（0.15）——链尾朝「SpineFollower + 头→SpineFollower 轴 ×
链尾当前半径」的**直脊柱静息位**回摆，施力前**显式去径向**（纯切向、不压缩链条、零地形查询、
`MaxMoveSpeed` 余量钳制）。悬臂态它给出爬行拖拽本来要等 τ≈25 tick 才给的下摆；停驶态前段轴
指哪就摆到哪、**不会无中生有拉向墙**。

结构门 `Hips != SpineFollower` + `!ApplyGravity`——spine=2 链尾就是拖尾点驱动的 follower
天然免疫（default/sprinter 与 smoke `ExpectedHash` 逐位不变），坠落态交还重力。

> 另试过翻越窗口（Crest）豁免，**实测净负**（回摆正是把链尾送过棱线的助力：豁免后
> wall-heavy 8→6、heavy 巡逻 8→6）——不设窗口分支。

效果：推墙最坏种子后腿重抓 +59→+28 tick、脊柱角回 160° 仅 +9 tick、此后不塌回（≥167°）；
停驶终态 180°（修复前对照 174°、迭代一 135°）；carrot-turn 恢复 12~15→6~7 tick、中段领先
8.4°→0°。代价：wall-heavy 翻越 9→8（相位量级）。

smoke `[CORE-HEAVY-MOUNT]` 双场景真断言（推墙：重抓 ≤45 / 角回 160° ≤40 / 不塌回 <140°；
停驶：终态角 ≥165° / 髋离墙 ≥0.35m —— **钉死「朝面吸」类回归**；增益归零精确复现 +59 红灯）。

**遗留**：形成期瞬时折角保留（合法瞬态）；后腿超距 `FindGrip` 过渡目标（还可砍 ~16 tick
脚程，须保持 `HasGrip=false` 不造假抓地）未做。

### 2.5 上墙前段转向（FrontMount）

RearBrace 解决了链尾弓起，却暴露**新的假绿**——三节脊柱内角接近 180°，但头—第二节仍沿墙法向
水平伸出，RearBrace 只会把髋排到这根**错误轴**后面，整条成为水平旗杆。

`FrontMountGain=0.35` **只服务「多节脊柱、无中段腿」的 Heavy 类拓扑**：Head 腿拿到本 tick
射线背书的陡墙落点即可用原始 `GripNormal` 预构造局部爬升方向，腿同步换步时仅以当前
`Head.TerrainContact + ContactNormal` 补位；SpineFollower 绕 Head **纯切向**回摆，Head 同步
沿面走——**不沿法线吸墙、不驱动 Hips、不造抓地**。

预摆内部角到 120° 即停，真实接触后允许形成用户期望的 ≥90° L/斜线；前段进入沿墙 30°、
`SupportNormal·localNormal ≥ 0.8`、后段踩同面或 Crest 均熄火，**成熟壁面换步不会重燃**。
强度按 `RunSpeed` 缩放，速度注入只补到目标值的 0.75，不逐 tick 累积冲量。

44 组出生距离 × 接近角扫描：44/44 在真实墙接触后 15 tick 内稳定进入沿墙 30°，后腿重抓最坏
45 tick，最小内部角 97.7°、零深折叠；正撞最坏相位 mount 法向占比从 0.986 降到 0.809，交接
最低角 129°。Hexapod 因有中段腿而**结构排除**，wall/tail/turn 轨迹与旧基线逐位一致。

---

## 3. 正式渲染

蜥蜴的 `IFormalRenderer` 经 `FormalRendererFactory` 分派：身尾连续扫管（**显示半径与物理
解耦**）、背刺、足端识别色。细节见
[`rainworld_render_research.md`](rainworld_render_research.md) §5。

> 注意：蜥蜴渲染件在**线性解读**顶点色的条件下调色定型（与 Centipede/Vulture 同批）。翻转到
> `srgbVertexColors: true` 需要重调色，属已知遗留。

## 4. 回归

蜥蜴没有独立矩阵脚本——它是**主矩阵**的 20 项：

- `default` ×2（双跑 diff）、`default` 40Hz（时基不变性）、`perturb`（灵敏度：哈希必须变）
- `wall`（正面推墙翻越 ≥9）、`wall-heavy` / `wall-hexapod`（多节脊柱贴墙，≥7/≥9 路点）
- `turn-hexapod`（平地 180° 掉头）、`wall-turn-hexapod`（竖墙沿面掉头）、`wall-tail`
  （尾链释放后身体恢复）、`wall-corner`（目标墙首次接触换面）
- `carrot-turn-heavy` / `carrot-turn-hexapod`（行进中 External 胡萝卜侧转约 90°，量化中段领先）
- `stand`（站桩 + 闲置姿态）、`carrot`（MoveTarget 路径点直喂通路）
- `heavy` / `sprinter` / `hexapod`（品种默认巡逻）
- `embed`（出生嵌入 60 tick 必须脱困）、`wallside`（贴墙擦边不得穿墙）

2000 tick 参考值（FrontMount 轮后）：`default` 11 路点、`wall` 14 翻越、`heavy` 7、
`sprinter` 15、`hexapod` 11、`carrot` 25；`wall-heavy` 10、`wall-hexapod` 13。

```bash
dotnet run --no-restore --project core/smoke
./tools/run_matrix.sh
```
