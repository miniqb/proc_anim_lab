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

## 5. 宿主 API 与生命周期

宿主按以下数据流接线：

1. 由地形安装点构造 `TentaclePlantMount(Point, OutwardNormal, TangentHint, ColliderId)`；
2. 从 `TentaclePlantFactory.Original/Short/Hunter/Lurker/ByName` 取得出生参数，再由
   `CreateController` 创建实例并给固定 seed；
3. 每 tick 在调用 `Tick` 前写 nullable `controller.Target`；需要伪装时按策略改写
   持久 bool `controller.DisguiseIntent`（§4.1）；
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
  `HeldTargetId`、`DisguiseAmount`（渲染层伪装视觉的驱动标量），以及最近一次 tick 的
  `TargetStatus`（可充能、越界、安装面后、遮挡、宿主隐藏、抓持或锁向突刺）；
- 路径：`WanderGoal`、0–2 个真实折点 `GuidePoints`（根与目标为隐含端点），以及
  `BacktrackFrom`（`-1` 表示无回退，否则为首个需回卷的触手段索引）；
- 预算：当 tick `TickQueryCount` 与生命周期峰值 `PeakQueryCount`。

这些输出除 `Target` 与 `DisguiseIntent` 外均不得由宿主回写。拟态草也不接受
`MoveDir` / `RunSpeed`：为了统一表面而伪造移动输入只会把固定生物错误塞进移动生物契约。

## 6. 沙盒与回归

直接观察：

```bash
godot --path . scenes/tentacle_plant_sandbox.tscn
```

沙盒键位：`1/2/3/0` 换预设、`F/G/H` 换安装面、`4~8` 换路线、`V` 正式/白盒双视图、
`C` 手动切 `DisguiseIntent`（ambush 路线脚本会每 tick 覆盖回 true）、`Space` 释放并
重置猎物、按住右键自由飞相机。确定性 CLI：

```text
--plant-preset=tentacle-plant/original|short|hunter|lurker（完整稳定 ID 或简名）
--plant-mount=floor|wall|ceiling
--plant-route=idle|hit|miss|occluded|ambush
--plant-target-local=<outward,tangent,bitangent>  # 可选，米；覆盖脚本猎物位置
--plant-min-strike-speed=<meters/tick> # 可选；确定性 hit 的最低首扑峰值
--plant-seed=<ulong>
--plant-determinism=<ticks>
--plant-tps=<positive integer>       # 专项矩阵使用 40 / 400
--plant-perturb=<meters>
--plant-expect-hash=<hex>
--plant-screenshot=<path[@tick]>     # 视觉验证旁路：到 tick 截图退出（headless 无帧）
--plant-cam=<px,py,pz,lx,ly,lz>      # 视觉验证旁路：固定机位 + look-at
```

`--plant-expect-hash` 仅在同时给出正数 `--plant-determinism` 时有效；两条视觉旁路
不触碰物理与哈希、不进矩阵。`ambush` 路线脚本：全程 `DisguiseIntent=true`，tick 200
放静止猎物，演完"入伪装缩到挂点 → 伪装态 10 tick 加速充能突袭 → 抓取/回收/吞入 →
慢速回伪装"的完整弧线；仅该路线把 `DisguiseAmount` 追加进沙盒确定性折叠（route 门控，
既有 11 条基线的折叠字节流逐位不变），并有"非 ambush 路线伪装标量恒零"的通用守卫。

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
- 同 seed 双跑 bit-exact、不同 seed 轨迹差异、8/16 段查询增长、穿透和所有数值 finite。

Godot 矩阵覆盖 floor / wall / ceiling、idle / hit / miss / occluded / ambush、四预设、
同 seed 双跑、idle / hit / ambush 的 40/400Hz 同 tick 结果、1mm 初态微扰**灵敏度**、
真实 collider 全半径穿透、hunter 长度球边缘与约 50 度大攻角扑击/抓取，以及
`TargetEffect` 事件顺序。既有 Lizard / Centipede / Spider / Cicada /
Vulture / Humanoid 的不变性由各自 smoke 与 matrix 在本轮集成验收中另行运行，不包含在
拟态草专项脚本内部。

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
- **嘴帧**：forward = 末端多段混合（0.6/0.4 差分）→ Striking 混 40% 突刺速度方向、
  伪装混向 Outward（缩链退化帧兜底）→ 低通；up 逐帧平行传输延续，**不做世界竖直
  对齐**——张开平面自由跟随触手自身 roll，只保证嘴对着猎物。
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

### 7.2 吊顶伏击竞技场（探索场景，不进矩阵）

`scenes/tentacle_plant_arena.tscn` + `TentaclePlantArenaWorld/Hud`，rat_arena 纪律
（命令行零参数、全 `[Export]` Inspector、默认值唯一真相源、生效值打在 ready 行）：

```bash
godot --path . scenes/tentacle_plant_arena.tscn
```

`BoxRoomArenaBuilder` 盒房间（默认 16×16m、净高 3.2m），lurker 挂房间正中天花板、
出生即伪装；挂点下一盏暖 OmniLight 亮度随 `DisguiseAmount` 走（揭露时"灯熄了"）。
宿主相位只有 `Ambush / Engage` 两个，冷却与回伪装计时都是时间戳（冷却≠相位——
RatFiend R19 教训）：

| 相位 | 条件 | 喂入 |
|---|---|---|
| Ambush | 玩家水平距挂点 > `AmbushTriggerRadius`(2.2m) | `Target=null`，intent=true |
| Ambush | 距 ≤ 触发半径 | 喂可见目标——伪装态加速充能，突袭由内核涌现 |
| Ambush→Engage | `AttackSerial` 增沿（伏击出手） | 压 `AttackCooldownSeconds`(2.5s) 冷却 |
| Engage | 距 ≤ `EngageReleaseRadius`(4.5m，迟滞) | 喂目标，`HostVisible = 冷却已过`——冷却中不充能仍 Tracking：**闭嘴、朝向玩家、扭动身体** |
| Engage→Ambush | 距 > 脱离半径持续 `RearmDelaySeconds`(3s) | intent=true、`Target=null`，慢速回伪装 |

玩家（共享 `ArenaFirstPersonPlayer`，层 2 对内核不可见）恒 `HostGrabbable=false`
（快咬弹开制，绕开 `PositionCorrection` 全量交付与自驱玩家打架的坑）；喂内核的
目标球心取**眼位**（`EyePosition`，= 相机高度，脚底 +1.55m）——吊顶伏击者冲着脸咬，
不是胶囊中心的腰腹；咬中判定宿主自做：Striking 期 `Hand` 距玩家眼位（= 瞄准点）
≤ `BiteRadius`(0.55m)，每 `AttackSerial` 只结算一次 → BITTEN 计数 + 镜头 kick + `AddImpulse` 背向推离（直写 Velocity 会被
"无输入即刻停"一步归零——RatArena R18b 实证）。防御断言：出现任何抓持效果即
warn + `ReleaseHeldTarget()`。实测节拍：出生 ~1.6s 后伏击首咬，玩家滞留则每
5s 一咬（2.5s 冷却 + 2.5s 充能，Windup 半程起嘴会当着玩家的面慢慢张开）。

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
- 首版不模拟段链自缠、不同拟态草互相缠绕或触手与动态刚体的逐段碰撞；目标交互集中在手端。
- 不实现水流、浮力、游泳或原作 Amphibious 分支；也不包含 PoleMimic 的伪装攀爬面、
  GarbageWorm 的抓矛行为和正式美术。
- 确定性承诺为同机同构建、同 seed、同 tick 输入 bit-exact；不承诺跨平台浮点逐位一致。
