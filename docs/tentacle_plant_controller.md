# 拟态草（TentaclePlant）3D 控制器

`TentaclePlantController` 是与 `LizardLocomotionController` 并列的锚定式伏击生物后端。
它不继承蜥蜴控制器，也不复用 `BreedParams`、`BodyFactory` 或单足端
plant-and-trail `Limb`；只共享 chunk 数学、`ITerrainQuery`、固定 tick 和确定性哈希底座。

实现依据为本机 Rain World 当前 DLL 中 `TentaclePlant`、独立 `Tentacle` 类及其
`TentacleProps` / `TentacleChunk`，并以 `PoleMimic`、`GarbageWorm` 作对照。反编译源码
只留在仓库外用于学习与互操作，本文只记录结构与行为结论，不包含原作源码。

## 1. 原作事实、同类共性与本项目扩展

以下三类结论不可混写：

| 类别 | 结论 |
|---|---|
| **TentaclePlant 原作事实** | 身体为 2 个 chunk、0 条 body connection；根 chunk 不碰地形并在每次更新中钉回洞口。独立 `Tentacle` 理想长度 `300px`（`7.5m`）、8 段，半径从根部 `8px`（`0.2m`）递减到手端 `1px`（`0.025m`）。 |
| **TentaclePlant 原作事实** | 洞口方向来自 shortcut 入口；初始闲置目标位于根前方 `200–300px`。无猎物时目标缓慢游走，贴近地形、进入 narrow space 或长期追不上时会向较开阔位置修正或回到手端附近。 |
| **TentaclePlant 原作事实** | 仅对视线内、距离根不超过触手理想长度的猎物累计 `attack`：每 tick `+1/90`，失去有效目标则每 tick `-1/180`；超过一半后触手明显后缩蓄势，充满后约 10 tick 连续向预测并锁定的方向注速。`canGrab` 在突刺中置满，突刺结束后以每 tick `0.025` 衰减，形成约 40 tick 余留抓取窗。 |
| **TentaclePlant 原作事实** | 命中后 `extended` 每 tick 减 `0.0125`，约 80 tick 完全缩回；随后把猎物拖到距根 `20px`（`0.5m`）内，或再强拉约 30 tick，才请求吞入洞口。扑空后没有额外硬编码 cooldown，而是进入收回并重新积累下一轮攻击。 |
| **同类共性** | TentaclePlant、PoleMimic、GarbageWorm 都是独立 `Creature` 实现，粗拓扑均为“锚定本体 + 2 chunk + 0 connection + 独立 `Tentacle` 段链”。当前 DLL 的 `Tentacle` **不继承** `BodyPart` 或 `Limb`。 |
| **同类差异** | PoleMimic 把各段钉向地图杆路径并经营伪装/苏醒；GarbageWorm 的长度为 `400px × bodySize`，主要观察、抓矛和缩洞。二者用于判断段链共性，不是 TentaclePlant 的参数变体。 |
| **本项目 3D 扩展** | 完整安装框架、三维球冠游荡区、全方向静态地形避让、确定性游走 seed、目标纯值快照与效果输出，均是原作 2D 代码没有回答的移植设计。 |

原作模板把 TentaclePlant 列为 Amphibious，但本项目不实现水中运动；“水草感”来自固定
tick 阻尼、柔性段链和慢速游荡，不包含浮力、水流或游泳分支。
原作也没有独立的“手”类：抓取代理是触手 tip 与攻击时放大到 `9px` 的主 tip 球，
图形层只在末端约 30% 做鼓包。本项目的 `Hand` 因而是明确命名的末端代理输出，不是声称
原作存在同名类型。

## 2. 核心类型与装配

- `TentacleChain`：物种自有的多节触手原语。持有根到手的有序段、速度、半径衰减、
  导引折线和地形回退状态；它不是 `Limb` 的子类。
- `TentaclePlantParams`：纯出生配置。工厂创建实例时冻结快照，运行时不回读调用侧对象。
- `TentaclePlantFactory`：提供 `Original()`、`Short()`、`Hunter()`、`Lurker()`、
  `AllPresets()`、`ByName()` 与 `CreateController()`；未知 ID 快速失败，不静默回落。
- `TentaclePlantMount`：安装点和完整朝向契约：
  `Point`、`OutwardNormal`、`TangentHint`、`ColliderId`。核心正交化后输出稳定的
  `Outward / Tangent / Bitangent`；`TangentHint` 与法线退化时使用固定回退，不读世界
  `Up` 猜方向。
- `TentaclePlantController`：拥有根、手、段链、游荡/攻击时序、目标输入和每 tick 效果输出。
- `TentaclePlantTargetSnapshot`：宿主写入的可空纯值目标快照；实体引用、场景节点和物理对象
  不进入核心。
- `TentaclePlantTargetEffect`：宿主每 tick 读取并应用到真实目标的纯值效果。

当前四个稳定预设：

| 稳定 ID | 长度 / 段数 | 柔性与节奏 |
|---|---:|---|
| `tentacle-plant/original` | `7.5m` / 8 | 原作基准软硬度；90 tick 充能、10 tick 突刺、40 tick 余留抓取窗、80 tick 回收 |
| `tentacle-plant/short` | `5m` / 6 | 更软、更紧凑；110 tick 充能、10 tick 突刺、40 tick 余留抓取窗、95 tick 回收 |
| `tentacle-plant/hunter` | `9m` / 10 | 更硬、更主动；70 tick 充能、12 tick 突刺、40 tick 余留抓取窗、65 tick 回收 |
| `tentacle-plant/lurker` | `3.2m` / 5 | 3.2m 净高房间的吊顶伏击者；100 tick 充能（伪装就位后 ×10 加速 → 10 tick 出手）、8 tick 突刺、40 tick 余留抓取窗、70 tick 回收；伪装参数调满（§4.1） |

参数语义分为六组：

- **几何/出生**：`Length`、`SegmentCount`、`RootRadius`、`TipRadius`、
  `HandVisualRadius`、`StrikeGrabRadius`、`RootMass`、`HandMass`、
  `TipMass`、`RootSurfaceOffset`、`SpawnExtension`。半径与质量沿链递减；`SpawnExtension`
  是首个物理 tick 经地形背书后尝试铺开的出生比例，不是构造函数无条件穿过前方碰撞体的距离。
  根/手视觉半径与突刺抓取半径分开，不能拿表现尺寸暗改捕获范围。
- **柔性段链**：`SegmentDamping`、`SegmentVelocityCap`、`TipGoalAttraction`、
  `InnerGoalAttraction`、`GuideAttraction`、`BacktrackSpeed`、`OutwardRootForce`、
  `ShapeSeparationForce`、`SelfAvoidanceStrength`、`SelfAvoidancePadding`、
  `ConstraintIterations`、`RetractedLengthFraction`。长度与段数改变时仍须保持有限、
  不穿墙且无深断链的可行域。
- **游荡**：`WanderCenterDistance` / `WanderRadius` 定义以
  `Root + Outward × WanderCenterDistance` 为球心的球，并裁掉 mount 安装平面后方部分，
  得到轴对称球冠工作区；`WanderStep` / `WanderJitter` 控制固定 tick 漂移；`WanderProbeDistance` /
  `WanderClearanceDistance` 控制静态地形净空；`WanderResetMeanTicks` /
  `WanderGoalCatchupDistance` 控制目标重置和追不上时回收。
- **导引/回退**：`GuideRefreshTicks`、`GuideTargetMoveThreshold`、
  `GuideClearanceRadius`、`GuideSurfaceOffset`、`GuideDetourDistance`、
  `GuideForwardProgressEpsilon`、`RoutingQueryBudget`、`BlockedSuffixTicks`、
  `RebuildTicks`。候选顺序固定，最多两个折点；预算耗尽不得退化成穿墙直达。
- **攻击/携带**：`ChargeTicks`、`LungeTicks`、`GrabWindowTicks`、`RetractTicks`、
  `ConsumeForceTicks`、`WindupStart`、`WindupPull`、`LungeImpulse`、
  `PredictionTicksPerMeter`、`LowRelativeGrabSpeed`、`ConsumeDistance`、
  `CarryHandMass`、`CarryVelocityGain`、`CarryRootPull`。它们分别控制充能、突刺/余窗、
  回收吞入和宿主目标修正；首版没有额外 `MissCooldown` 参数。
- **伪装/伏击（opt-in）**：`DisguiseExtensionFraction`、`DisguiseEngagePerTick`、
  `DisguiseReleasePerTick`、`DisguiseChargeThreshold`、`DisguiseChargeMultiplier`。
  门在宿主输入 `DisguiseIntent`（默认 false）而不在参数：关闭时这些参数不参与任何
  运行期计算，既有品种基线零漂移（§4.1；CLAUDE.md §6.6 opt-in 硬要求的又一先例）。
  `DisguiseChargeThreshold` 经 `Validate` 强制为正——`DisguiseAmount == 0` 永不过阈。

## 3. 三维化取舍

### 3.1 安装与游荡体积

`OutwardNormal` 定义洞外方向，但在 3D 中不能单独决定 roll，因此必须再给
`TangentHint`。核心只在这个 mount 局部系里工作：地板、侧墙和天花板安装是同一算法的
旋转实例，不存在按世界方向分支。

二维“根部前方区域”扩为一个局部球冠：先取球心
`Root + Outward × WanderCenterDistance`、半径 `WanderRadius` 的球，再裁掉 mount 安装
平面后方部分。它绕 `Outward` 轴对称，不额外假设上下方向；相同 seed、tick 和输入生成
相同目标序列。这样可在三维中向任意切向漂移，同时保持“从洞里长出来”的轮廓。

### 3.2 地形避让与导引

地形感知只使用 `ITerrainQuery`。核心把 mount 的切向提示投影到每次命中障碍法线的切平面，
再按固定顺序评估 8 个全向绕障候选，不依赖 Godot 节点、导航网格或无序集合；找到可行绕路
时生成 `GuidePoints` 导引折线。段链若发现
后续导引已被地形隔断，`BacktrackFrom` 标记需要回退的首个触手段索引，先缩回可见侧再改道，
不得让手或段链直接穿过遮挡板。

若直线已被球形净空否决、8 个候选又都不可行，核心会把导引终点退到首个障碍前的安全前缀，
并主动收住外段；不会继续沿旧的直达目标把细梢穿进整株无法通过的窄缝。`BacktrackFrom`
生效期间暂停全链的向外根力与隔节互推，只保留导引和相邻节回拉；否则这两种表现力会沿安全
前缀传到贴障后缀，把 15-tick 重铺窗口反复压回同一碰撞面。
目标在进入路由与形态力前还会沿根到目标方向钳到 `Length` 可达球；因此宿主给出 15m 或 25m
远目标时只改变方向，不会让固定查询预算形成魔法距离。若自定义参数或复杂障碍仍耗尽
`RoutingQueryBudget`，本 tick 改用零长度安全前缀，绝不保留上一条可能已失效的 carrot。

构造与 `Remount` 时尚没有可合法查询的 Godot physics space，所以所有可见段和 `Hand` 先折叠在
`mount.Point + Outward × RootRadius`；首个 `Tick` 在 `Body.Tick` 前用中心线射线和固定间距球净空
尝试铺到 `Length × SpawnExtension`，一旦命中便把后缀留在最后安全进度。这个“先折叠、后安全播种”
是 3D 连续碰撞扩展，也与原作 reset 时把触手段收拢到同一位置的做法一致；它避免薄板内的段被
MTD 推到远侧后形成不可恢复的隔墙拓扑。

原作正常状态的 body hand / 触手 tip 是 50/50 位置与速度加性耦合，回卷时由 tip 取得权威，
且 body hand 受 `idealLength × 2 × extended` 根部硬拴；本项目保留这三点。回收后段的误差会在
碰撞前用一次 tip→root 反向 PullOnly 投影传回整链，必要时再相对根统一缩短，因此全链约束与
根部硬拴能同时成立而不增加地形查询。由于 3D `Hand` 是无连接代理，耦合仍发生在主绳约束之后，
公开 tick 结束前会再次投影末链与根部硬拴、做地形安全回退。原作每 tick 先让
`TentacleChunk` 常规限速并积分，再由 `TentaclePlant` 给 tip 与 body proxy 注入供下一 tick
消费的扑击速度；下一 tick body proxy 直接消费，链粒子仍会经过常规 chunk cap。本项目保留
这一区分：链物理仍用 `SegmentVelocityCap`，但 hand/tip 的 tick 末耦合不得提前抹掉待消费冲量，
因此扑击运动使用 `SegmentVelocityCap + 2 × LungeImpulse` 的有限耦合预算，并只允许末节达到
1.25 倍柔顺长度。
这道二次投影是 3D 稳定扩展，用于保证突刺/重目标回收不会再把单节拉成数倍或让代理越过
静态障碍，不声称原作在同一位置有第二次求解。

`GuidePoints`、`BacktrackFrom` 和查询计数都是核心只读输出。沙盒可以把它们画出来，
但不得用调试图形反向决定运动。合法埋地只限根部；可见段和手仍受静态地形净空约束。

### 3.3 猎物边界

核心不扫描动态物体。宿主负责选择猎物，并把
`StableId / Position / VelocityPerTick / Radius / Mass / HostVisible / HostGrabbable`
写入 nullable `Target`。`HostGrabbable` 只约束捕获，宿主仍可让不可抓目标参与追踪与充能；
控制器据其余字段做范围、视线、预测、阶段和几何命中判定。位置、速度、半径和质量必须有限，
半径与质量不得为负；无效快照在进入固定 tick 前快速失败，不能把 NaN 注入链与确定性哈希。

3D 攻击包络是“以物理根为中心的长度球”与“安装面洞外半空间”的交集。判定使用
`Radius` 表达的猎物球体是否与该包络相交，而不是只检查目标中心：目标中心可比
`Length` 最多多出自身半径，或比安装面最多退后自身半径；只有整个猎物球都越出长度球、
或整个球都在安装面后方时才拒绝充能。原作只对 main body chunk 中心做二维距离检查；
这里是为连续 3D 目标体积补出的边界语义。
视线射线的目标端容差只取 `Radius`，路由用的 `GuideClearanceRadius` 不参与视觉判断；通过手/梢
扫掠和速度门后，真正建立抓取关系前还会再做一次手到目标球的静态地形视线校验。因此
`HostVisible=false` 的开放地形低速被动触碰仍可抓取，但 0.15m 薄板后的目标不能借扩大抓取球穿墙。

每次 `Tick` 都覆盖只读 `TargetEffect`；`TargetId` 指明本次效果所属的稳定目标，其余字段语义为：

- `CaptureStarted`：本 tick 首次几何命中，请宿主建立抓取关系；
- `Held`：核心仍在保持该稳定目标；
- `PositionCorrection` / `VelocityDelta`：宿主应用到目标权威物理的建议修正；
- `Released`：抓取失效、显式释放或重新安装，本 tick 应解除宿主关系；
- `ConsumeRequested`：目标已拉到根部，或完全缩回后的强拉宽限已到，请宿主处理吞入。

核心从不直接移动目标节点、扣血或销毁对象。宿主若不接受 `CaptureStarted`，必须调用
`ReleaseHeldTarget()`，或在下一 tick 清空 `Target` / 换成另一稳定 ID；仅把同一目标的
`HostGrabbable` 改为 `false` 不会解除已建立的核心抓持。

`Held` 期间位置关系是刚性的：`PositionCorrection` 覆盖本 tick 最终 `Hand.Pos` 与目标快照的
完整误差，宿主应用后目标与手端重合；`Mass` / `CarryHandMass` 的权重保留在 `VelocityDelta`
和 Hand/Tip 反作用中，用来表达轻重目标不同的速度响应。不能把位置修正也按质量缩小，否则扎根
绳约束会拒绝 Hand 本应承担的位移份额，重目标会在仍标记 `Held` 时逐 tick 离手数米。吞入距离门
同样按应用这份刚性位置修正后的手端位置判断；只有额外强拉达到 `ConsumeForceTicks` 才允许走
原作已有的强制请求分支。

## 4. 行为阶段与固定序

原作主要靠连续 `attack` / `extended` 标量和是否抓住目标推进，并没有以下同名枚举。
本项目把可观察时序显式化为 `TentaclePlantPhase`，方便 3D 宿主、渲染和回归共享同一状态；
这属于接口整理，不声称原作存在六态状态机。

`TentaclePlantPhase` 的公开阶段为：

- `Wandering`：`Target == null` 且 `CanGrab == 0`，在 mount 前方球冠内缓慢游荡并避让地形；
- `Tracking`：宿主仍提供目标、`CanGrab == 0`，且充能尚未进入 Windup；若目标遮挡或越界，充能会衰减，
  因此该观察阶段本身不承诺当前仍可见或仍在攻击包络内；
- `Windup`：充能过半后的明显后缩，仍保留目标预测；
- `Striking`：原作在扑击开始时锁定预测方向；本项目额外冻结该预测点作为 3D guide/servo
  目标。沿链渐弱传递同一后置冲量，以及修正手端相对锁定攻击直线的横向误差，也都是 3D
  求解扩展；它们不会在扑击中重新追踪猎物。公开阶段表示冲量注入窗口；最后一拍在下一 tick
  实际积分时，即使公开阶段已转为 `Recovering` 或 `Holding`，仍沿用锁定目标与扑击耦合预算；
- `Recovering`：扑击结束而 `CanGrab > 0` 的余窗阶段；可见目标的 `AttackCharge`
  会在此期间并行重新积累。它不改变 `Extension`，只有真正 `Holding` 才按
  `RetractTicks` 回收；
- `Holding`：已捕获稳定目标，持续保持并向根部回收。

`original` 的基准序列为：90 tick 连续有效目标充能；第 45 tick 后进入明显 Windup；
充满后突刺 10 tick；`CanGrab` 在突刺全程保持开启，并在突刺结束后再用 40 tick 衰减；
命中后约 80 tick
完全缩回，再以“距根不超过 `0.5m` 或额外强拉 30 tick”请求吞入。目标失效、遮挡或移出
范围会按当前阶段退回 Tracking/Wandering 或 Recovering，不允许无限连续抽打。

每个核心 tick 的顺序固定为：

1. `Body.Tick`；
   （随后伪装/探头/拉伸标量按序演化：`UpdateDisguise` → `UpdateProbe` →
   拉伸状态机——全部先于本 tick 的 extension/goal/lengthScale 消费者，§4.1–4.3）
2. `TentacleChain` 路径与物理解算；
3. 感知目标并推进攻击标量；
4. 注入下一 tick 使用的形态力；
5. 耦合 `Root` / `Hand` / tip；
6. 判定抓取、保持、释放或吞入，并覆盖回收效果 `TargetEffect`。

捕获建立的 tick 保持完整 `Extension`；之后每个已抓持 tick，段链先用本 tick 将公开的下一档
`Extension` 解算，再在第 6 步提交同一标量，故 80 tick 回收不会出现“几何仍按旧长度、输出却已缩短”
的错位。保持阶段计算宿主 `VelocityDelta` 时读取的是施加 hand 回收修正**之前**的速度；否则目标质量
大于 `CarryHandMass / CarryVelocityGain` 时误差项会翻号成正反馈。反作用写回 Hand/Tip 后还会重施
`SegmentVelocityCap`。宿主仍须按 §5 在 tick 后把 `PositionCorrection` / `VelocityDelta` 应用到同一
权威目标并于下一 tick 回喂，核心不代管动态实体。

宿主只调用一次 `Tick(TickContext)`，不得拆开或重排这些阶段。逻辑固定 40 tick/s；
`Vel` 仍是米/tick 位移，渲染只读插值状态。

### 4.1 伪装/伏击标量（opt-in，本项目扩展）

原作没有对应机制（`PoleMimic` 的伪装攀爬面是另一物种、明确不移植）。为"吊在天花板
伪装成灯、猎物经过突然咬"的玩法，本项目加了一个**连续伪装标量**而不是新 Phase 枚举值
——`(int)Phase` 进两份哈希折叠，追加枚举值即便放在末尾也意味着新状态语义要进折叠流；
伪装本质是对六态的连续调制（蜷缩程度），与阶段正交，标量表达最干净。

- **输入** `DisguiseIntent`（bool，默认 false，持久属性）：第二个宿主可写输入。
- **输出** `DisguiseAmount ∈ [0,1]`：按 `DisguiseEngagePerTick`（慢，默认 80 tick 回满）
  / `DisguiseReleasePerTick`（快，默认 4 tick 归零）朝 intent 缓动。
- **力学效果**（全部由 `_disguiseAmount > 0` 分支门控，默认路径零新增浮点运算）：
  1. `physicsExtension` 被 cap 到 `Lerp(1, DisguiseExtensionFraction, amount)`——
     链经既有 PullOnly 绳约束与根部硬拴被匀速收拢到挂点附近；
  2. goal 连续混合向 `Root + Outward × (Length × DisguiseExtensionFraction)`
     （硬切换会抖动导引刷新与查询计数）；
  3. wander 更新跳过（`WanderGoal` 冻结、RNG 不消耗）；
  4. 形态力静默：Windup 后缩力乘 `(1 − amount)`（蜷缩时后缩只会把伪装球向外推），
     `OutwardRootForce` / `ShapeSeparationForce` 经 `InjectShapeForces` 新尾参
     `calmAmount` 同比衰减（goal/guide 吸引不衰减——它们正是拉回根部的力）；
  5. 充能加速：`amount ≥ DisguiseChargeThreshold` 时每 tick 充能增量从 2 变
     `2 × DisguiseChargeMultiplier`（纯整数、保留半 tick 计量与"充满同 tick 开扑"）。
- **覆盖序**（优先级从高到低）：`Holding` → 忽略 intent、强制快衰；突刺运动窗口
  （`consumingStrikeMotion || _strikeTicksRemaining > 0`）→ 强制快衰 **且 extension
  cap / goal 收拢整体旁路**——蜷缩链才能被扑击冲量完整甩出（充满 tick 冲量注入时
  位置尚未移动，cap 残留无害；下一 tick 起窗口判定生效）；否则朝 intent 缓动。
- **恢复伪装是涌现的**：扑空/吞入后 intent 仍为 true，amount 从 0 以 engage 速率
  回升，cap 连续收缩把伸出的链逐 tick 拉回——无需额外状态。
- `Remount()` 连同 `Target` 一起把 intent/amount 复位。

"攻击后范围内没有存活猎物才恢复伪装"之类的策略归宿主（沙盒 ambush 路线 / 竞技场
相位机 / 主仓 AI），内核只提供机制。

### 4.2 探头张紧标量（opt-in，本项目扩展）

原作没有对应机制。为"察觉到猎物但锁不住位置时，放弃伪装、张着嘴探出头搜索"的玩法
（生物学设定：喉部灯泡=发光+检测反射变化的一体器官，双颌是它的准直器），加第二个
连续标量——同 §4.1 的论证，不加 Phase 枚举值。

- **输入** `ProbeIntent`（bool，默认 false，持久属性）：第三个宿主可写输入。
  **探测悬停本身不需要新输入**：宿主喂 `HostVisible=false` 的合成 Target 快照，
  goal 即转向该点（goal 选择不看 HostVisible）、不充能、零视线射线、wander RNG
  冻结、`Phase=Tracking`——缓慢蠕动由宿主动画探测点实现，内核不加速度参数。
  `ProbeIntent` 只补齐这套组合缺的两块：渲染张嘴依据 + 预张紧充能。
- **输出** `ProbeAmount ∈ [0,1]`：按 `ProbeEngagePerTick`（默认 20 tick 回满）/
  `ProbeReleasePerTick`（默认 8 tick 归零）朝 intent 缓动。
- **力学效果**（默认路径零新增浮点运算）：
  1. 充能预张紧：`amount ≥ ProbeChargeThreshold` 时充能增量取
     `Max(既有值, 2 × ProbeChargeMultiplier)`（与伪装加速同位、纯整数；两者同过阈
     取更张紧者）——锁定后宿主喂真目标，lurker 配 10 即 `ceil(100/10)=10` tick 出手；
  2. wander 冻结条件扩为 `_disguiseAmount <= 0 && _probeAmount <= 0`（探头态与
     伪装同款不消耗 RNG）。
- **覆盖序**：`Holding` / 突刺运动窗口 → 忽略 intent、强制快衰（照抄伪装）；
  **`DisguiseIntent` 优先**——双 intent 同真时探头快衰、伪装上升（宿主相位切换在
  同 tick 邻域改写两个 bool 是合法的，交接期两标量共存、平滑交棒，不抛异常）。
- `Remount()` 连同 Target 一起复位 intent/amount。搜索策略（扇区、路点、聆听、
  预算）全部归宿主。

### 4.3 攻击弹性拉伸（opt-in，本项目扩展）

原作没有对应机制；生物学参照是变色龙舌头的弹性反冲弹射。让突刺窗口的有效链长
放大 `StrikeStretchFactor` 倍（默认 **1.0 = 恒等元**），攻距与"探测头动半径 +
锁定锥长"解耦——探测用短臂（暴露少），攻击用长臂（保证兑现），极限距离咬空。

- **参数** `StrikeStretchFactor ∈ [1,2]`（上界理由见 Params 注释：goal 钳制不超
  拴绳语义 `Length*2`、查询预算、攻距语义）+ `StrikeStretchRecoverPerTick`
  （出窗定速回卷，默认 20 tick；Validate 有跨字段约束
  `Length*2*(factor−1)*recover ≤ SegmentVelocityCap` 防瞬移回抽）。
- **输出** `StretchAmount ∈ [0,1]`：突刺运动窗口内为 1、出窗定速斜坡归零；
  当前有效倍率 = `Lerp(1, factor, StretchAmount)`。
- **力学效果**（整个状态机包在 `factor > 1` 单分支里）：`lengthScale` 经尾部默认
  参数（`=1f`，先例 `calmAmount`）传进 `TickPhysics` / `InjectShapeForces` /
  `ConstrainTipAfterCoupling` 的 `effectiveLength`、`maximumTipReach` 与
  `ClampGoalToReach`，及 `CoupleHandAndTip` 根部拴绳；**攻击资格包络**
  （`EvaluateAttackTarget` 的 reach 与 `DistanceSquaredToAttackEnvelope`）用构造期
  预计算的静态 `_strikeReach = Length × factor`（斜坡值会让 `TargetStatus` 在回卷
  期翻线）。extension 的 4 处 `Clamp(0,1)` 原样保留——extension 语义仍是回收档位，
  拉伸走独立乘子。
- **进入瞬时、退出斜坡**：加长对 PullOnly 链是纯放松（零力注入）可瞬时；缩短经
  `ConstrainTipToRoot` 硬投影回抽必须斜坡——咬空后的 ~20 tick 回卷正是设计要求的
  "可读回收僵直"（玩家逃跑窗口）。开扑首 tick `lengthScale` 仍为 1 是有意一拍延迟
  （冲量下一 tick 才被消费，DropBug"上一 tick 因子作用于本 tick 物理"教义）。
- **不给任何预设开启**：拉伸由宿主（竞技场 Export 覆写预设）/ CLI（沙盒
  `--plant-stretch`）按场景 opt-in。

## 5. 宿主 API 与生命周期

宿主按以下数据流接线：

1. 由地形安装点构造 `TentaclePlantMount(Point, OutwardNormal, TangentHint, ColliderId)`；
2. 从 `TentaclePlantFactory.Original/Short/Hunter/Lurker/ByName` 取得出生参数，再由
   `CreateController` 创建实例并给固定 seed；
3. 每 tick 在调用 `Tick` 前写 nullable `controller.Target`；需要伪装/探头张紧时
   按策略改写持久 bool `controller.DisguiseIntent`（§4.1）/ `controller.ProbeIntent`
   （§4.2）；
4. `Tick` 后读取 `controller.TargetEffect`，由 gameplay 权威对象应用效果；
5. 渲染读取 `Body`、`Root`、`Hand`、`Segments` 及其插值位置。

生命周期入口：

- `Shift(delta)`：世界 rebase。平移根、手、全部段、目标记忆、wander goal、guide points 和
  插值历史，保持阶段、充能、seed 与抓取连续性。
- `Remount(mount)`：地形不随体移动的重新安装。替换完整 mount frame，清导引、旧目标记忆、
  抓取和攻击阶段，立即把可见链折叠到新洞口外缘，并在下一物理 tick 按新地形安全播种。
- `ReleaseHeldTarget()`：立即解除内核抓持，并在**下一次 `Tick`**通过
  `TargetEffect.Released` 通知宿主；若 `Target` 仍存在则进入 `Tracking`，否则进入
  `Wandering`。`Remount()` 对已有抓持的释放通知也遵循下一 tick 语义。

宿主/调试可读：

- 几何：`Body`、`Root`、`Hand`、`Segments` 和各自插值位置；
- 行为：`Phase`、`AttackCharge`、`CanGrab`、`Extension`、`AttackSerial`、
  `HeldTargetId`、`DisguiseAmount`（渲染层伪装视觉的驱动标量）、`ProbeAmount`
  （渲染层张嘴/探照灯的驱动标量）、`StretchAmount`（当前拉伸程度），以及最近一次
  tick 的 `TargetStatus`（可充能、越界、安装面后、遮挡、宿主隐藏、抓持或锁向突刺）；
- 路径：`WanderGoal`、0–2 个真实折点 `GuidePoints`（根与目标为隐含端点），以及
  `BacktrackFrom`（`-1` 表示无回退，否则为首个需回卷的触手段索引）；
- 预算：当 tick `TickQueryCount` 与生命周期峰值 `PeakQueryCount`。

这些输出除 `Target`、`DisguiseIntent` 与 `ProbeIntent` 外均不得由宿主回写。拟态草
也不接受 `MoveDir` / `RunSpeed`：为了统一表面而伪造移动输入只会把固定生物错误塞进
移动生物契约。

## 6. 沙盒与回归

直接观察：

```bash
godot --path . scenes/tentacle_plant_sandbox.tscn
```

沙盒键位：`1/2/3/0` 换预设、`F/G/H` 换安装面、`4~8/N/M` 换路线、`V` 正式/白盒双视图、
`C` 手动切 `DisguiseIntent`（ambush 路线脚本会每 tick 覆盖回 true）、`Space` 释放并
重置猎物、按住右键自由飞相机。确定性 CLI：

```text
--plant-preset=tentacle-plant/original|short|hunter|lurker（完整稳定 ID 或简名）
--plant-mount=floor|wall|ceiling
--plant-route=idle|hit|miss|occluded|ambush|probe|stretch
--plant-target-local=<outward,tangent,bitangent>  # 可选，米；覆盖脚本猎物位置
--plant-min-strike-speed=<meters/tick> # 可选；确定性 hit 的最低首扑峰值
--plant-stretch=<factor>             # 仅 stretch 路线；[1,2]，确定性 stretch 必填 >1
--plant-seed=<ulong>
--plant-determinism=<ticks>
--plant-tps=<positive integer>       # 专项矩阵使用 40 / 400
--plant-perturb=<meters>
--plant-expect-hash=<hex>
--plant-screenshot=<path[@tick]>     # 视觉验证旁路：到 tick 截图退出（headless 无帧）
--plant-cam=<px,py,pz,lx,ly,lz>      # 视觉验证旁路：固定机位 + look-at
```

`--plant-expect-hash` 仅在同时给出正数 `--plant-determinism` 时有效；两条视觉旁路
不触碰物理与哈希、不进矩阵。三条 opt-in 路线脚本：

- `ambush`：全程 `DisguiseIntent=true`，tick 200 放静止猎物，演完"入伪装缩到挂点 →
  伪装态 10 tick 加速充能突袭 → 抓取/回收/吞入 → 慢速回伪装"的完整弧线；
- `probe`（lurker ceiling，700 tick）：伪装 150 tick → `ProbeIntent=true` + 喂
  `hostVisible=false` 的合成探测点（头张嘴转向悬停，断言零充能零扑击、tick 400 时
  头距探测点 <1m）→ tick 401 同点转可见真目标 → 预张紧充能，`firstStrike ==
  400 + ceil(ChargeTicks/ProbeChargeMultiplier) = 410` → 抓取/吞入 → intent 持续、
  终态 `ProbeAmount ≥ 0.99`；
- `stretch`（original floor，400 tick，`--plant-stretch=1.5`）：目标钉在距根 ≈1.2L
  （原包络外、1.5L 包络内），断言 tick 1 起即 `Chargeable`（包络放大证据）、
  `firstStrike == ChargeTicks`、扑窗手端冲出 `L+0.30`（lengthScale 生效直接观测）、
  原包络外完成抓取/吞入、拉伸标量满→零弧线完整。

折叠门控：`ambush` 折 `DisguiseAmount`；`probe` 折 `DisguiseAmount + ProbeAmount`；
`stretch` 折 `StretchAmount`——既有 11 条基线的折叠字节流逐位不变，并有"非对应
路线标量恒零"的三条通用守卫。

专项验证：

```bash
dotnet run --project core/tentacle_plant_smoke
./tools/run_tentacle_plant_matrix.sh
```

两条入口都以退出码和断言判定。无引擎 smoke 覆盖：

- 四预设装配、参数快照、未知 ID 与伪装参数越界快速失败；
- 三向安装、游荡域/净空、局部两折点与回卷恢复；开放地形首 tick 必须铺到
  `Length × SpawnExtension`，original/hunter 在三向 0.25m 薄板前的出生/Remount 各跑
  2000 tick，要求全程无远侧段、无中心线穿板且连续回卷不超过 60 tick；
- 45/90/10/40/80+30 tick 时序、三预设攻击期链节/梢端位移硬门、大攻角目标首扑命中与最低
  冲击位移、被动低速捕获、质量牵引及目标失效门；
- `{0.25, 1, 4, 10}` 质量的真实闭合宿主回路，保证全程 finite、Hand/Tip 同位、内部速度不越过
  `SegmentVelocityCap`、链节不持续超长、`2 × Length × Extension` 根部硬拴零超差，且请求吞入时
  目标已进入 `ConsumeDistance`；0.15m 薄板负例与开放地形正例共同钉住抓取视线；
- `15.1 / 15.2 / 25m` 同方向远目标的连续横向响应，防止查询预算边界再次冻结导引；
- hunter 在三向安装下的目标球长度/前半空间交界，以及整个目标球越界时的拒绝门；
- `Shift`、`Remount`、`ReleaseHeldTarget` 的逐字段生命周期；
- 伪装/伏击（`DISGUISE` 检查，天花板安装 + lurker）：参数惰性（intent=false 下极端
  伪装参数与出厂参数 300 tick 逐位同运动学——门在输入不在参数）、入伪装 80 tick 达满
  且单调、蜷缩静置包络（位置包络而非速度阈值——自避让在蜷缩球内有持续微推）、
  `WanderGoal` 冻结、突袭时延精确 10 tick、抓持期 amount 快衰归零（快衰窗口内允许
  残量——捕获可落在突刺后衰减完成前）、突袭窗口 tip 冲出伪装
  包络（cap 旁路证据）、释放后慢速回满、intent 撤销 ≤4 tick 归零 + 游走恢复；整套
  场景双跑 bit-exact 并钉独立基线 `DisguiseExpectedHash`（与主 `ExpectedHash` 并列）；
- 探头张紧（`PROBE` 检查，天花板安装 + lurker）：参数惰性（intent=false + 极端探头
  参数 + 逐 tick 喂可见目标刻意加热充能路径，300 tick 逐位同出厂）、伪装→探头交接
  快衰 ≤5 tick、合成隐藏探测点全程 `Tracking/HostHidden`、零充能零扑击零效果、头端
  伺服趋近探测点（终距 <1m）、双 intent 冲突时探头 ≤9 tick 归零且伪装上升（优先级
  直接证据）、锁定后 `strikeDelay == ceil(200/20) = 10`（预张紧核心断言）、突刺窗
  强衰、释放后回满 + **probe 单独冻结游走**（wander 新分支直接证据）、intent 撤销
  ≤8 tick 归零 + 游走恢复；消融孪生（probe 永不张紧）`strikeDelay == 100`；双跑
  bit-exact 并钉独立基线 `ProbeExpectedHash`；
- 攻击拉伸（`STRETCH` 检查，floor + original ×1.5）：参数惰性（factor=1 + 极端回卷
  速率跑完整 hit 场景刻意加热扑击窗，300 tick 逐位同出厂）、1.2L 目标从 tick 1 即
  `Chargeable`（包络放大证据）、`firstStrike == ChargeTicks`、扑窗 tip 冲出
  `L+0.30`（越过 PullOnly 名义上限 = lengthScale 生效直接观测）、窗后定速回卷无瞬移
  回抽、静置收回 `L+0.15`（回收僵直闭环）、原包络外完成抓取/吞入/释放、消融孪生
  （factor=1）恒 `OutOfRange` 零扑击；双跑 bit-exact 并钉独立基线
  `StretchExpectedHash`；
- 同 seed 双跑 bit-exact、不同 seed 轨迹差异、8/16 段查询增长、穿透和所有数值 finite。

Godot 矩阵覆盖 floor / wall / ceiling、idle / hit / miss / occluded / ambush / probe /
stretch、四预设、同 seed 双跑、idle / hit / ambush / probe / stretch 的 40/400Hz 同
tick 结果、1mm 初态微扰**灵敏度**、真实 collider 全半径穿透、hunter 长度球边缘与约
50 度大攻角扑击/抓取，以及 `TargetEffect` 事件顺序。既有 Lizard / Centipede /
Spider / Cicada / Vulture / Humanoid 的不变性由各自 smoke 与 matrix 在本轮集成验收中
另行运行，不包含在拟态草专项脚本内部。

具体哈希与配置数量只以 smoke/matrix 当前输出和脚本内钉死常量为准；在基线真正生成并通过前，
文档不预写数值。

## 7. 正式渲染件与伏击竞技场

### 7.1 正式渲染件（`scripts/render/TentaclePlantFormalRenderer.cs`）

拟态草的第一个正式渲染件，也是外观从"植物"改成"肉质触手怪"的落点（叶片等植物修饰
已从白盒渲染器移除；白盒保留作 V 键调试视图，mock 猎物球仍由白盒负责）：

- **肉质管体**：`SplineSampler` 4× 密化 + `TubeMeshBuilder.AddTube`，
  `srgbVertexColors: true`（场景 tonemap 2 硬要求）；视觉剖面反锥度——肉根粗 → 颈细 →
  向头膨出（原作视觉剖面与物理半径解耦有据，渲染研究 §1.4）；双频行波蠕动化妆，
  Striking 抑制、伪装归零。配色按预设分档（苍白肉粉 / 灰褐 / 暗红 / 苍白灰粉），禁绿。
- **蛇/蜥式双颌大嘴**（RatFiend 可动颌先例重定档）：上下两根长吻锥管绕 mouthRight 轴
  对开**各承担一半张角**（长吻双颌对开时嘴缝中线天然保持在触手轴向上），
  rest 6~9° 永不咬死、gape 160~172°（真 180° 颌背会贴上颈管）；铰点填缝双球
  （后肉色 / 前暗红口底）+ 沿平分线的宽根暗红喉锥（闭嘴随 mawScale 缩没）；牙齿
  seed 基因（上颌每侧 8~12 / 下颌 7~10、10% 缺牙、前两颗犬齿 ×1.3~1.7、下牙错半齿位），
  **十字双刀片**——单面 `AddBlade` 侧视近乎消失，大张的嘴是主视觉；无眼无耳。
- **嘴开度**：`AttackSerial` 增沿先开 0.18s **飞行保持窗**（全张扑向猎物——手端
  ~0.6m/tick、交战出手 ≤~5m 即飞行 ≤~8 tick，宁可略晚合、嘴到脸前绝不先闭；
  内核真抓到 `HeldTargetId` 时提前触发，接触帧即咬），窗末才进咬合顿挫 snap 窗
  （阶跃沿驱动，不测平滑域——RatFiend R18b 教训）；
  `openTarget = 飞行窗 ? 1 : (snap 窗 || Striking || Holding) ? 0 :
  max(InverseLerp(WindupStart,1,charge), disguiseEase) + 微呼吸/负偏置微颤`；
  非对称低通**开慢（λ=7，蓄力"慢慢张开"）合快（λ=28，咬合"啪"地咬死）**。
- **嘴帧**：forward = 末端多段混合（0.6/0.4 差分）→ 宿主光束覆写 `SetBeamAim(dir,
  weight)`（权重 ease λ=6 淡入淡出，Ambush 权重 0 回落链推导）→ Striking 混 40% 突刺
  速度方向、伪装混向 Outward（缩链退化帧兜底）→ 低通；up 逐帧平行传输延续，**不做世界竖直
  对齐**——张开平面自由跟随触手自身 roll，只保证嘴对着猎物。
- **颈部姿态**（`scripts/render/TentaclePlantNeckPose.cs`，纯静态数学，无引擎依赖）：
  光束覆写让嘴 forward 与物理链末段完全解耦——链是只抗拉的绳，探头悬停有余量时链身垂到
  头下方再翘上来、过冲后猎物在脑后，嘴便张向自己的脖子（用户实测；竞技场对齐复现
  lurker：Probe/Engage 期"枕点←物理前枕点"与嘴 forward 的夹角 r=1.5m 绕圈 p50 116°、
  >90° 71%，光锁后站定 p50 114°、>90° 68%）。**指哪看哪优先级高于头部转角**：嘴 forward
  分毫不动，照蛇/巨蜥"寰枕关节有限转角 + 颈椎吸收其余"只重画**最后三节**的走向：
  1. 颈入方向 N = 嘴 forward F 朝链末方向 C 转 θ，θ ≥ min(Ω, `NeckAtlasDegrees`)
     （Ω = angle(F, C)），再往后转只在"三节颈够不着"时发生——闭式可达性解
     （|O − N(θ)·l3 − A| ≤ (l1+l2)·StretchMax ⇔ ρ cos(θ−φ) ≥ γ；可行集在圆周上是
     [φ−α, φ+α] mod 2π，φ=Atan2 的割线两侧两个副本都查，取满足的最小 θ；不可行取定义域
     内圆周上离 φ 最近的端点——评审实测只查主区间时 φ<0 的帧会被误判不可行、θ 塌到下限
     再硬拉伸，且过割线时一帧跳变）；θ ≤ Ω——**绝不比物理链更差**，唯一例外是第 4 条的
     挂点半空间钳（净空优先于头颈角，多出 ≤4°）；权重 w 再把 θ 从 Ω lerp 过去，w=0 逐位
     回落物理链（沙盒从不喂光束、伏击态、蜷缩退化帧均走此路；枕点由渲染件按旧式表达式
     `mouthPos − fwd×0.35headR` 算好喂入，端点逐位相同）。
  2. 弯曲平面向量 G（F 法平面内朝链那侧）逐帧续接：上帧 G 投影到当前法平面，几何解与
     它反向就取反（弯曲侧真的换边）、近反平行（Ω 150°→176°）再向上帧 G 混合钉住——两者
     同侧，lerp 不过零（评审实测直接 lerp 两反向单位向量会在 anti≈0.5 处过零翻面，颈一帧
     镜像 ~1m）。换边时解本身是镜像跳变，由**输出侧偏差限速**（肘/前枕点相对物理点的
     化妆偏差 ≤4 m/s ≈ 0.1 m/tick，再叠 λ=40 低通压单帧抖动）滑过去——试过限速转角
     （720°/s）：过渡帧穿过颈够不着的方向，硬拉伸把末节压扁、进枕方向随机，反而更糟。
  3. 末节钉死沿 N 进枕：前枕点 P2 = O − N·l3（`SplineSampler` 端点单边切线 = 该节，
     管子精确沿 N 进头）；前两节由 `TwoBoneIk` 从锚点 A = s[n−4] 解到 P2，极向量 =
     物理中点相对 锚→前枕 轴的偏侧（米，松弛时主导、保物理形状）+ 进枕侧偏置 0.35
     （肘在 −N 侧，骨 2 才顺着 N 进枕）+ 上帧极向量 0.5 并 λ=8 低通（防同轴翻面：
     4000 tick 零翻面）；进枕转折 > 100° 时把 N 朝来向回转一次（先过半空间钳再比较，
     含钳的头颈角不得比当前更差）。
  4. 骨长逐帧取自插值后的物理链（突刺 ×1.7、回收缩链自动跟随）；够不着才按
     `NeckStretchMax` 等比拉伸，超过 ×1.25 硬比就**沿 N 缩短末节**（进枕方向不变、
     最短 0.25 l3；只有更长的末节才够着时允许伸到 1.5 l3；仍不够任由两骨再伸——极少）；
     链身有余量时锚→前枕弦很短，定长两骨会折成发夹（肘转折 >120° 占冷却退距悬停帧
     34–44%）——骨长不超过弦长 ×0.87（肘转折 ≤110°，化妆管长损失一点换圆顺）。拉伸与
     压缩不同时发生。挂点半空间钳：N 钳到前枕点离安装面 ≥0.4 headR，肘一侧投影同边距。
  控制点总数与旧版同（n+2：肘/前枕点/枕点替换 s[n−3..n−1]），剖面/蠕动的索引参数不变；
  接管权重 = 光束权重 ease × (1 − 1.25×伪装缓动) × 蜷缩门（平均链节 ≤ 2 倍枕点后退量
  时 0、≥ 4 倍时 1——蜷缩链上没有"颈"可言，出伪装首帧不许从它起算）。宿主配置面
  `ConfigureNeck(atlasDeg, stretchMax)`（竞技场 `NeckAtlasDegrees` 45° / `NeckStretchMax`
  1.0；180° = 不限角只做进枕对齐）；读数 `NeckHeadAngleDegrees / NeckOmegaDegrees /
  NeckStretch` 进竞技场 F3 dbg 行。
  **复现结果**（无引擎 harness 编译本类 + `TwoBoneIk` + 感知器，逐帧仿真渲染件化妆状态；
  lurker、tscn 参数、4000 tick）：头颈角 p50 114–116° → **45°**（= 寰枕上限，颈把其余
  吃掉）；>90° 占比 r=1.5 绕圈 71% → 18%、站定 68% → 17%、r=3 23% → 11%、r=5 16% → 0%；
  NaN 0、权重 0 逐位恒等 0 违例（含枕点）、极向量翻面 0、嘴 forward 误差 0；"比物理差"
  帧 ≤35/4000、多出 ≤11°（半空间钳、输出限速滑行中、近反平行钉面三类）；进枕方向逐 tick
  转角 p99 ≤13°（回收期整链被甩 0.3–0.9 m/tick 时最大 26°）；肘逐 tick 位移 p99 悬停
  ≤0.08 m、回收期 0.16–0.37 m（物理 s[n−3] 自己 0.21–0.41 m）；肘转折 ≤111°；最小弯曲半径
  探头悬停 p50 0.6–1.0 headR、退距悬停 0.35–0.4 headR（物理 1.05；管半径 ≈0.3 headR，内侧
  轻微挤压）。**残留**（几何上任何三节渲染颈都救不回）：(a) 回收期过冲——头飞过猎物、
  链绷直、嘴回头咬（Recovering 头颈角 p50 78–92°、>120° 25–32%），宿主
  `RecoilBeamFollowsHead` 让回收期光束跟链甩回可降到 p50 13°/整体 >90° 4–8%，但那是感知
  可见的改动（见 §7.2），默认关；`NeckStretchMax` 1.15 也能换到 Recovering p50 53°、
  颈部可见"呼吸"拉伸（p90 1.15）；(b) 冷却退距悬停在灯正下方（r≤1.5）：3.2m 链吊 1m 深，
  颈须绕一圈从侧后进头，弯曲半径见上；(c) short 预设 5m 链的同一悬停（Engage/Tracking
  p50 55–82°）——链长问题，不是颈的问题。**内核侧方案（评审否决，记档）**：给 s[n−2] 加
  "看向"伺服的 opt-in 力——只抗拉的绳在最痛的帧（悬停 2.3m > (n−2)·link=1.92m 的绳
  可达界；过冲/回收是绷直链）根本摆不到位，且会动 `Hand.Pos`（感知顶点、咬合判距，
  复现改咬中次数 5→2/14）、经 `Hand.Pos` 形成宿主光束↔内核颈的反馈环、回迁契约多一个
  输入；渲染侧派生姿态有蜘蛛 IK 膝/RatFiend 关节数学先例，故落在渲染层。
- **伪装视觉**（纯化妆位移，`sink = smoothstep(DisguiseAmount) × 收拢门`，Striking /
  飞行窗 / snap 窗强制归零一帧交还物理位。**收拢门**：按手端到挂点的物理距离连续
  门控——进入蜷缩静置包络（2L×Fraction+0.15，与 smoke quietTip 断言同源）为 1，
  包络外 `max(0.5, 0.25L)` 处归零。出生/重置后的首次入伪装 DisguiseAmount 两秒到满
  而链还垂在半空，不门控时下沉偏移会把管体中段上提、头 lerp 进天花板，滞后的链段
  仍在低处，管尾被拉成一根从大张的嘴中央穿出的肉锥——用户实测穿帮、且只在首次
  缩回复现（吞食后链已近收拢，早期排查因此漏掉）。门控后首次缩回=纯物理卷链 →
  贴顶才熔入 + 灯泡亮相）：头组件埋进安装面 1.1 headR（允许穿模，全张双颌与牙尖
  全部藏进板内）、管体整体下沉且 `sink > 0.9` 时停画；**灯泡**（唯一刚性件：暖白
  SphereMesh + 微 emission）由伪装外推项 `bulbPush = 1.20·SmoothStep(0.5,1,sink)`
  单独推出走面下——吊在天花板上几乎只露一个发光灯泡，就是一盏吸顶灯。闭嘴时灯泡
  缩回喉内被闭合颌管完全包住。两处穿帮的修复（均用户竞技场实测）：**外推只在
  sink 后半程启动**——早启会把灯泡推进过渡期"半张/近闭"的双颌里，从颌管壁与唇缝
  透出一圈牙齿剪影衬底的锯齿白带，前半程灯泡留在喉心（0.18+0.50×开度，攻击蓄力
  已验证的安全位置）、sink=1 终态推量与旧值一致；**喉锥跟随灯泡同步外推同一
  bulbPush** 且半径随 sink 轻微收缩（×(1−0.35·sink)）——锥留在原位时嘴底与退走的
  灯泡之间会露出一截"连着灯泡的锥"（暖灯照下呈肉色），同推 + 收缩让锥在任意
  sink 下都被灯泡吞没。sink=0 的攻击路径两项均为恒等变换。
- **探照灯**（SpotLight3D，锁定锥的可视化=设定本身）：挂 `_root` 而非 `_bulb`
  （灯泡 basis 含非均匀缩放，子节点会继承畸变），`DrawMouth` 末尾按
  `Basis(right, up, −fwd)` 摆位（`right = fwd×up ⇒ right×up = −fwd`，右手正交且
  −Z ≡ 嘴 forward——Godot SpotLight 沿本地 −Z 照射），灯芯出灯泡球面防埋颌管；
  开阴影（墙挡光 = 感知视线遮挡的视觉诚实）、灯泡关 CastShadow（自发光球不遮
  自己的灯）。能量曲线只读内核标量：`hunt = max(ProbeAmount 低通, 1−伪装)`，
  伪装 = 0.9 弱光池（吸顶灯脚下那圈）、狩猎 = 2.6 搜索光束、蓄力/突刺窗再叠
  1.6 闪耀；伪装态 fwd 已对齐 Outward → 灯自动朝下变吸顶灯，零额外分支。公开
  化妆配置面 `ConfigureSearchlight(halfAngleDeg, range)` 让宿主把锥角对齐感知
  锁定锥（竞技场调用；沙盒用默认值）。嘴开度合成加入 `ProbeAmount` 低通——
  探头张紧全程大张（颌是探照灯的准直器）。

### 7.2 吊顶伏击竞技场（探索场景，不进矩阵）

`scenes/tentacle_plant_arena.tscn` + `TentaclePlantArenaWorld/Hud`，rat_arena 纪律
（命令行零参数、全 `[Export]` Inspector、默认值唯一真相源、生效值打在 ready 行）：

```bash
godot --path . scenes/tentacle_plant_arena.tscn
```

`BoxRoomArenaBuilder` 盒房间（默认 16×16m、净高 3.2m），lurker 挂房间正中天花板、
出生即伪装；挂点下一盏暖 OmniLight 亮度随 `DisguiseAmount` 走（揭露时"灯熄了"），
与渲染件探照灯互补（那盏演"灯亮着"、这盏演"锁定锥"）。玩家随身补光经
`ConfigureFillLight(1.2, 10)` 压暗——光锥明暗对比是感知机制的可视化，不能被冲淡。

**感知系统整体替换了旧的距离触发**（`AmbushTriggerRadius/EngageReleaseRadius/
RearmDelaySeconds` 已删除）：`TentaclePlantPerception`（纯 C#，同文件
`TentaclePlantProbePlanner`，均在 `scripts/tentacle_plant_sandbox/
TentaclePlantPerception.cs`）只对玩家眼位的逐 tick **移动量**累计——静止=隐身是
硬设计；锥内判定基于宿主权威的**光束朝向**（tick 域、按 `BeamTurnDegreesPerSecond`
限速回转）：探头时指向规划器当前**凝视点**（本路点对应的假想猎物位置——停头刻意停
在它前方 0.6×锥长处，"照哪"不能从探测点推导）、锁定新鲜期指向最后感知点、伏击回落
链末段推导（末三段方向混合按伪装度对齐挂点法线，与渲染同公式但**无低通**——感知
行为不许依赖渲染帧率）。探头悬停的链身有余量、末段在重力下上翘，链推导的 forward
不代表照向——这根光束方向同时喂给感知锥轴、调试锥与渲染件
`SetBeamAim(dir, weight)`（嘴与探照灯按权重混合、ease 淡入淡出，Ambush 权重 0
完全回落链推导，可视化=判定）；"急转"= 目标方向突变 + 回转斜坡自然涌现。
两个光束来源修正（颈部姿态那一轮，见 §7.1）：**探头凝视最小前瞻** `GazeMinAheadMeters`
（1.2m，须 ≤ 锥长）——**只对假想凝视点**（路点采样/回头杀，`planner.GazeIsEstimate`
为假）生效：转场里假想点可能落在探测点旁甚至脑后，光束打回自己脖子，前瞻把光束目标至少
推到探测点前方（沿根→凝视点）1.2m；聆听重瞄的真实猎物估计**不推**——评审复现：推了会把
光束平移到探测点自己的射线上、离猎物 ~76°，锁定要等探测点转过去（~1–2s）才涨，"立即
重瞄"就不成立了；不推则颈角收益少一截（r=1.5 绕圈 >90° 8% → 18%），由颈部姿态兜。
**回收期光束跟链** `RecoilBeamFollowsHead`（默认**关**）——内核 Recovering 期光束改沿链末
方向甩回，过冲后"回头咬"的帧从源头消失（复现 Recovering 头颈角 p50 78° → 13°），代价是
回收窗（= 出手后的抓取窗 GrabWindowTicks/40 s，lurker 1s）内锁定锥离开猎物、其间猎物
移动不续锁定新鲜期（`LockHoldSeconds` 须盖过回收窗，ValidateExports 有 note）——感知
可见的行为改动，由用户按手感开。
渲染颈部姿态本身**不改**光束与感知（`NeckAtlasDegrees` / `NeckStretchMax` 只进
`ConfigureNeck`），F3 dbg 行多一段 `neck=θ°(chain Ω° x拉伸)`。
视线遮挡用墙层（掩码 1）
射线，每 tick ≤1 条且只在"区内且有移动"时发射。累计速率 ∝ 移动量 × 敏化
（Aplysia 式：刺激线性抬升、静止指数恢复）；衰减只在静止时发生、出区稍快不清零；
察觉累计过阈**触发即消耗**（天然阻尼边界横跳）。探头搜索：起手直奔最佳估计
（粗方位 + 幅度粗测距，方位 ±18°/距离 ±30% 噪声按"移动回合"冻结）、停头 =
估计距离 − 0.6×锥长（锥尖够猎物、嘴不过冲）、路点 + 常态性聆听停顿（0.6–1.5s
随机）、无信息时包络按假想猎物速度扩张、**触发性聆听**（探头期区内**任何**被感知
到的移动 tick 都立即重瞄——头跟着猎物转，猎物一停就到位 2–3.5s 长凝视 + 包络坍缩；
凝视时长只在进入聆听时掷一次、凝视真正结束才退出聆听）、**twitch 折扣**（静止 0.5s
后开启新"移动回合"，回合开头 0.3s 的移动**察觉累计**近零——灯照到=免费警告说的是
光束还没照到你的那一下；**锁定累计不打折**：锁定锥内灯已在你身上，累计与伪装态同速，
否则点按式"动 0.3s/停 0.5s"能在探照灯下永远不被锁定；回合按猎物真实动静计、计数
只在移动 tick 消耗，粗估计噪声对也按回合冻结——回合在视线外开启则等首个被感知的
移动 tick 再重抽）、低概率回头杀、预算随时间耗且伸得越长耗得越快。

宿主相位 `Ambush / Probe / Engage` 三个，锁定/冷却/探头就绪都是时间戳或感知量
（冷却≠相位——RatFiend R19 教训）：

| 相位 | 条件 | 喂入 |
|---|---|---|
| Ambush | 无锁定 | `Target=null`，disguise=true/probe=false |
| Ambush | `LockFresh`（锁定新鲜期 2.5s） | 喂真目标（最后感知点、速度 0；越出长度球则钳到包络边缘——`StrikeBeyondReach`）——伪装态 ×`DisguiseChargeMultiplier`（lurker 10 → ~10 tick）充能突袭 |
| Ambush→Probe | 察觉累计过阈且过探头冷却 | `planner.Begin(最佳估计)`，disguise=false/probe=true |
| Probe | 无锁定 | 喂 `hostVisible=false` 合成探测点（planner 动画、头张嘴跟随）；区内每个被感知的移动 tick → `OnListen` 重瞄（头跟着猎物转，停下即长凝视）；预算尽 → 回 Ambush（`EndProbe` 顺手 `ResetAlertness`：敏化回 1、两累计清零——重新蜷成吸顶灯的是个干净的伏击者，不带上一轮的火气慢慢消） |
| Probe | `LockFresh` | 冷却已过喂瞄点（visible）——预张紧 ×10 充能 ~10 tick 出手；冷却中喂猎物前方 `CooldownStandoff`(1.2m) 的退距点（HostHidden，头退开悬停不压脸）；规划器冻结 |
| 任意→Engage | `AttackSerial` 增沿 | 压 `AttackCooldownSeconds`(2.5s) 冷却 |
| Engage | `LockFresh` | 同上：冷却已过喂瞄点，冷却中喂退距点 |
| Engage→Probe | 锁定过期 | 它只知道"置信度涨不上去"——`planner.Begin(最佳估计)` 转搜索 |

玩家（共享 `ArenaFirstPersonPlayer`，层 2 对内核不可见）恒 `HostGrabbable=false`
（快咬弹开制，绕开 `PositionCorrection` 全量交付与自驱玩家打架的坑）；**突刺瞄
"最后感知点"**（喂真目标位置 = `perception.AimPoint`、速度 0 → 内核预测退化为
定点瞄准）——急停侧移可让它咬空；攻距由 Export `StrikeStretchFactor`（范围 [1,2] =
内核 Validate 上界）覆写预设，充能门 = Length×factor + 玩家半径 0.35m（内核长度球）。
**锁定即出手**（`[Export] StrikeBeyondReach`，默认开）：最后感知点越出长度球时
`FeedAimTarget` 把瞄点钳到根→感知点射线上的包络边缘（内缩 0.02m），内核照常充能、
朝猎物方向全力扑出——射线上的钳点仍在挂点前半空间且径向超出 < r，内核三道几何门
必过，只剩视线门；HUD 第二行 `aim=clamped+x.xxm` 显示钳回量。头端实际伸到哪由拉伸
物理决定：链节 1.25× 拉伸余量让它越过长度球，无引擎实测（lurker 3.2m 链、探头停头
2.3m 起扑、开阔地）头端距根极限 1.5→5.6m / 1.6→5.9m / 2.0→6.8m，即 ≈ ×Length +
0.4~0.8m；再加 BiteRadius 0.55m 就是有效咬合斜距，咬空带 = 锁得到（H+L）但咬合距
够不到的那一圈（ValidateExports 的"无咬空带"note 只比长度球，偏保守）。
咬中判定宿主自做：Striking 期 `Hand` 距玩家眼位 ≤ `BiteRadius`(0.55m)，
每 `AttackSerial` 只结算一次 → BITTEN 计数 + 镜头 kick + `AddImpulse` 背向推离
（直写 Velocity 会被"无输入即刻停"一步归零——RatArena R18b 实证）。**咬中失聪**
`BiteDeafSeconds`(1.0s)：期内眼位位移一律不计入感知（差分基准照常推进），触觉锁定
也不认——推离是它自己造成的位移，沿光束轴向外正好落在锁定锥里，不失聪它就追着自己
的推离连咬：无引擎复现（tscn 实际参数、远距突刺、玩家出手前已完全静止）连咬 6 次
直到推出咬合距，关推离则只咬 1 次——"站着不动还被补刀"全是攻击自激；失聪后 1 次。
ValidateExports 按玩家 `ExternalVelocityDecay` 算推离降到移动死区以下的时长，失聪窗
盖不过就打 note。推离方向**背离挂点**（水平）而非背离头：咬中时头常已越过猎物，
背离头会把人推回挂点侧，探头回来停头时正好又碰到。
**触觉锁定**（`perception.TouchLock(eye, lead)`，不经光感知累计）：探头/交战期
`Hand` 距眼位 ≤ BiteRadius 且内核不在 Striking/Holding/**Recovering**（回收尾巴里头
自己在猛动，扫过猎物不算碰到——否则回收时擦一下就把新鲜期续过冷却）、不在失聪期，
就把猎物位置钉到眼位、锁定新鲜期续期、锁定累计归零；下一 tick 相位分支照常喂真目标
→ 预张紧 ×10 充能 ~10 tick 后**正常突刺**（冷却仍是唯一攻击门）。瞄点 = 眼位 +
**引导量** `TouchStrikeLead`(1.0m，沿根→眼，前方 0.3m 内有地形改朝下、再挡住取 0)：
头已贴在猎物上，直接瞄眼位会让内核 `target − Hand` 退化成厘米级伺服残差、突刺方向
随机（评审无引擎复现：冲地板、冲天花板、倒飞穿挂点），引导量给它一根穿过猎物向外的
稳定方向（复现：lead=0 时扑向天花板，lead=1 时 cos(根→眼)=1.00）。感知器把猎物位置
（`PreyPoint`）与引导量分开存：`AimPoint = PreyPoint + lead` 给突刺，`PreyPoint` 给
探头规划与冷却退距，光感知写入时引导量清零。**冷却退距** `CooldownStandoff`(1.2m)：
冷却中不喂瞄点而喂猎物前方 1.2m 的退距点（内核对 HostHidden 目标仍把 goal 设到该点，
直喂就是嘴压在脸上等冷却——咬完贴着又触觉锁定，静止猎物每个冷却周期挨一下）。
复现（新规则）：远距突刺后静止 1 口；走进嘴里站着不动 1 口（推离出接触，锁定过期转
探头）；被咬后退 1m 站定 1 口；**关推离**则站在嘴里每 ~3s 一口（探头回来停头处距眼位
0.54m，正好再碰到——站在它最后知道你在的地方本来就该挨咬）。此前是"碰到即咬 + 立即
回伪装"的反射性咬合，改掉的原因：咬完马上蜷回去像放过了猎物——张开的颌内仍不是
安全屋，只是咬要经过一次看得见的突刺。防御断言：出现任何
抓持效果即 warn + `ReleaseHeldTarget()`。感知/探头全部数值挂
`[ExportGroup("Perception")/("Probe")]`（默认值见 ready 行与 ValidateExports），
HUD 第二行是目标/攻距/`aim=`（direct / clamped+x / standoff），第三行实时显示
aware/lock/sens/fresh/deaf/budget/pa/stretch。

**调试覆盖层**（`TentaclePlantDebugDraw`，`[Export] DebugOverlay` 初值 + 运行时 `F3`
切换）两组信息：

- **感知两锥**（覆盖层开着就恒画——感知不分相位）：以头端为顶点、以感知自己用的
  **tick 域光束方向**（非渲染低通值）为轴，锁定锥（青）与察觉锥（紫、更淡）各画
  母线 + 边界圆环。两锥的判定都是**球扇形**（距离 ≤ 半径 且 cos ≥ cos半角），所以
  圆环画在球冠上（深 = R·cos半角、半径 = R·sin半角）而非平底盖：16° 的锁定锥两者
  几乎重合，75° 的察觉锥差得很远（平底会严重虚报覆盖范围）。默认参数下锁定锥的环
  = 地面那圈光池（半径 0.88m），察觉锥的环 ≈ 眼高附近半径 6.28m 的大圈。母线与
  圆环都是**精确边界**（球半径 / 球冠交线），近似的只有没画的锥面。
- **当前探测点**（绿菱形）+ 探测点↔头端连线（半透明绿条带，长度 = 头端伺服滞后）
  + 头端小橙菱形，只在"宿主本 tick 确实把探测点喂给了内核"时画（Ambush、以及 Probe
  期内转喂真目标时自动消失，不留鬼影）。

HUD 加一行数值版 `dbg cone lock=…°x…m aware=…°x…m neck=θ°(chain Ω° x拉伸) probe=(x,y,z) lag=…m`。绘制手法
沿用 `RayDebugDraw`（朝相机的条带/菱形、Unshaded + NoDepthTest，被触手或墙挡住也
看得见）；头端与探测点按渲染 alpha 插值（与正式渲染件同一 alpha，40Hz 逻辑不抖
画面）。锥轴朝下（伏击态）会撞上 `axis × Up` 退化，回退到 `axis × Right` 建基。
纯观测：只读宿主已有量，不进物理与哈希。

### 7.3 本轮修复记录（lurker 调参）

**症状**：lurker 首版（6 段、`LungeImpulse 0.40`）在 smoke `STRIKE-GEOMETRY` 红灯，
`maxLink 2.10×`。**初判（被推翻）**：以为是冲量过大，压冲量 + 提约束迭代即可——
0.40→0.26、迭代 2→5 只把 2.10× 压到 1.30×，仍越 1.25 门限。**根因**：峰值出现在
Striking→Recovering 的**根链节**（`Anchor→Segments[0]`）——扑击落幕时整链带余速挂在
目标侧，被钉死的根扛住全部松弛，PullOnly 绳约束的逐 tick 收敛残差与链节绝对长度成
反比；短链（link 0.53m）的绝对预算（1.25 × link）本来就比 short 小 36%，速度类参数
不同比缩配根本收不回来。**修复**：`SegmentCount 6→5`（link 0.64m，预算 +20%、同迭代
收敛更快）+ 速度类参数按链节比例缩配（VelCap 0.42、TipGoal 0.009、Impulse 0.26、
阻尼 0.92、迭代 5）。教训与 CLAUDE.md §6.5（调参可行域）同源：**短链的"迅猛感"交给
充能加速表达，不要靠加大冲量**。

## 8. 回迁边界与已知问题

- 回迁到 `random-room-runtime` 时，gameplay 根/安装点仍是权威；内核只模拟根外触手，
  不使用移动生物的 tether 配方，也不移动 `CharacterBody3D`。
- `ITerrainQuery` 查询与段链碰撞、绕障和遮挡相关的静态地形，包含墙与顶面；“可支撑/可安装”
  由物种和宿主按命中法线判定。猎物、道具、伤害、逃脱、死亡和吞入由宿主根据
  `TentaclePlantTargetEffect` 处理。
- 当前地形绕障是固定预算的局部导引，不是 3D 导航或全局最短路；复杂凹洞、活动门和会移动的
  遮挡物不在首版保证内。
- 探测悬停点由宿主以 `HostVisible=false` 合成快照给出（§4.2），内核不搜索场景、
  不感知"察觉区/锁定锥"这类概念——光感知与搜索策略整体归宿主
  （`TentaclePlantPerception` 随竞技场回迁）。
- 首版不模拟段链自缠、不同拟态草互相缠绕或触手与动态刚体的逐段碰撞；目标交互集中在手端。
- 不实现水流、浮力、游泳或原作 Amphibious 分支；也不包含 PoleMimic 的伪装攀爬面、
  GarbageWorm 的抓矛行为和正式美术。
- 确定性承诺为同机同构建、同 seed、同 tick 输入 bit-exact；不承诺跨平台浮点逐位一致。
