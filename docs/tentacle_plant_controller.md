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
- `TentaclePlantFactory`：提供 `Original()`、`Short()`、`Hunter()`、`AllPresets()`、
  `ByName()` 与 `CreateController()`；未知 ID 快速失败，不静默回落。
- `TentaclePlantMount`：安装点和完整朝向契约：
  `Point`、`OutwardNormal`、`TangentHint`、`ColliderId`。核心正交化后输出稳定的
  `Outward / Tangent / Bitangent`；`TangentHint` 与法线退化时使用固定回退，不读世界
  `Up` 猜方向。
- `TentaclePlantController`：拥有根、手、段链、游荡/攻击时序、目标输入和每 tick 效果输出。
- `TentaclePlantTargetSnapshot`：宿主写入的可空纯值目标快照；实体引用、场景节点和物理对象
  不进入核心。
- `TentaclePlantTargetEffect`：宿主每 tick 读取并应用到真实目标的纯值效果。

当前三个稳定预设：

| 稳定 ID | 长度 / 段数 | 柔性与节奏 |
|---|---:|---|
| `tentacle-plant/original` | `7.5m` / 8 | 原作基准软硬度；90 tick 充能、10 tick 突刺、40 tick 余留抓取窗、80 tick 回收 |
| `tentacle-plant/short` | `5m` / 6 | 更软、更紧凑；110 tick 充能、10 tick 突刺、40 tick 余留抓取窗、95 tick 回收 |
| `tentacle-plant/hunter` | `9m` / 10 | 更硬、更主动；70 tick 充能、12 tick 突刺、40 tick 余留抓取窗、65 tick 回收 |

参数语义分为五组：

- **几何/出生**：`Length`、`SegmentCount`、`RootRadius`、`TipRadius`、
  `HandVisualRadius`、`StrikeGrabRadius`、`RootMass`、`HandMass`、
  `TipMass`、`RootSurfaceOffset`、`SpawnExtension`。半径与质量沿链递减；根/手视觉半径与突刺抓取半径
  分开，不能拿表现尺寸暗改捕获范围。
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
并主动收住外段；不会继续沿旧的直达目标把细梢穿进整株无法通过的窄缝。

`GuidePoints`、`BacktrackFrom` 和查询计数都是核心只读输出。沙盒可以把它们画出来，
但不得用调试图形反向决定运动。合法埋地只限根部；可见段和手仍受静态地形净空约束。

### 3.3 猎物边界

核心不扫描动态物体。宿主负责选择猎物，并把
`StableId / Position / VelocityPerTick / Radius / Mass / HostVisible / HostGrabbable`
写入 nullable `Target`。`HostGrabbable` 只约束捕获，宿主仍可让不可抓目标参与追踪与充能；
控制器据其余字段做范围、视线、预测、阶段和几何命中判定。

每次 `Tick` 都覆盖只读 `TargetEffect`；`TargetId` 指明本次效果所属的稳定目标，其余字段语义为：

- `CaptureStarted`：本 tick 首次几何命中，请宿主建立抓取关系；
- `Held`：核心仍在保持该稳定目标；
- `PositionCorrection` / `VelocityDelta`：宿主应用到目标权威物理的建议修正；
- `Released`：抓取失效、显式释放或重新安装，本 tick 应解除宿主关系；
- `ConsumeRequested`：目标已拉到根部，或完全缩回后的强拉宽限已到，请宿主处理吞入。

核心从不直接移动目标节点、扣血或销毁对象。宿主若不接受 `CaptureStarted`，必须调用
`ReleaseHeldTarget()`，或在下一 tick 清空 `Target` / 换成另一稳定 ID；仅把同一目标的
`HostGrabbable` 改为 `false` 不会解除已建立的核心抓持。

## 4. 行为阶段与固定序

原作主要靠连续 `attack` / `extended` 标量和是否抓住目标推进，并没有以下同名枚举。
本项目把可观察时序显式化为 `TentaclePlantPhase`，方便 3D 宿主、渲染和回归共享同一状态；
这属于接口整理，不声称原作存在六态状态机。

`TentaclePlantPhase` 的公开阶段为：

- `Wandering`：`Target == null` 且 `CanGrab == 0`，在 mount 前方球冠内缓慢游荡并避让地形；
- `Tracking`：宿主仍提供目标、`CanGrab == 0`，且充能尚未进入 Windup；若目标遮挡或越界，充能会衰减，
  因此该观察阶段本身不承诺当前仍可见或仍在攻击包络内；
- `Windup`：充能过半后的明显后缩，仍保留目标预测；
- `Striking`：方向在开始时锁定，按预设的固定 tick 突刺，并开启抓取窗；
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

宿主只调用一次 `Tick(TickContext)`，不得拆开或重排这些阶段。逻辑固定 40 tick/s；
`Vel` 仍是米/tick 位移，渲染只读插值状态。

## 5. 宿主 API 与生命周期

宿主按以下数据流接线：

1. 由地形安装点构造 `TentaclePlantMount(Point, OutwardNormal, TangentHint, ColliderId)`；
2. 从 `TentaclePlantFactory.Original/Short/Hunter/ByName` 取得出生参数，再由
   `CreateController` 创建实例并给固定 seed；
3. 每 tick 在调用 `Tick` 前写 nullable `controller.Target`；
4. `Tick` 后读取 `controller.TargetEffect`，由 gameplay 权威对象应用效果；
5. 渲染读取 `Body`、`Root`、`Hand`、`Segments` 及其插值位置。

生命周期入口：

- `Shift(delta)`：世界 rebase。平移根、手、全部段、目标记忆、wander goal、guide points 和
  插值历史，保持阶段、充能、seed 与抓取连续性。
- `Remount(mount)`：地形不随体移动的重新安装。替换完整 mount frame，清导引、旧目标记忆、
  抓取和攻击阶段，并从新洞口重新播种可见链。
- `ReleaseHeldTarget()`：立即解除内核抓持，并在**下一次 `Tick`**通过
  `TargetEffect.Released` 通知宿主；若 `Target` 仍存在则进入 `Tracking`，否则进入
  `Wandering`。`Remount()` 对已有抓持的释放通知也遵循下一 tick 语义。

宿主/调试可读：

- 几何：`Body`、`Root`、`Hand`、`Segments` 和各自插值位置；
- 行为：`Phase`、`AttackCharge`、`CanGrab`、`Extension`、`AttackSerial`、
  `HeldTargetId`；
- 路径：`WanderGoal`、0–2 个真实折点 `GuidePoints`（根与目标为隐含端点），以及
  `BacktrackFrom`（`-1` 表示无回退，否则为首个需回卷的触手段索引）；
- 预算：当 tick `TickQueryCount` 与生命周期峰值 `PeakQueryCount`。

这些输出除 `Target` 外均不得由宿主回写。拟态草也不接受 `MoveDir` / `RunSpeed`：
为了统一表面而伪造移动输入只会把固定生物错误塞进移动生物契约。

## 6. 沙盒与回归

直接观察：

```bash
godot --path . scenes/tentacle_plant_sandbox.tscn
```

沙盒支持以下确定性 CLI：

```text
--plant-preset=tentacle-plant/original|tentacle-plant/short|tentacle-plant/hunter
--plant-mount=floor|wall|ceiling
--plant-route=idle|hit|miss|occluded
--plant-seed=<ulong>
--plant-determinism=<ticks>
--plant-tps=<positive integer>       # 专项矩阵使用 40 / 400
--plant-perturb=<meters>
--plant-expect-hash=<hex>
```

`--plant-preset` 的规范写法是完整稳定 ID，交互 CLI 也接受
`original / short / hunter` 简名；`--plant-expect-hash` 仅在同时给出正数
`--plant-determinism` 时有效。

专项验证：

```bash
dotnet run --project core/tentacle_plant_smoke
./tools/run_tentacle_plant_matrix.sh
```

两条入口都以退出码和断言判定。无引擎 smoke 覆盖：

- 三预设装配、参数快照与未知 ID 快速失败；
- 三向安装、游荡域/净空、局部两折点与回卷恢复；
- 45/90/10/40/80+30 tick 时序、被动低速捕获、质量牵引及目标失效门；
- `Shift`、`Remount`、`ReleaseHeldTarget` 的逐字段生命周期；
- 同 seed 双跑 bit-exact、不同 seed 轨迹差异、8/16 段查询增长、穿透和所有数值 finite。

Godot 矩阵覆盖 floor / wall / ceiling、idle / hit / miss / occluded、三预设、同 seed
双跑、idle 与 hit 的 40/400Hz 同 tick 结果、1mm 初态微扰**灵敏度**、真实 collider
全半径穿透与 `TargetEffect` 事件顺序。既有 Lizard / Centipede / Spider / Cicada /
Vulture / Humanoid 的不变性由各自 smoke 与 matrix 在本轮集成验收中另行运行，不包含在
拟态草专项脚本内部。

具体哈希与配置数量只以 smoke/matrix 当前输出和脚本内钉死常量为准；在基线真正生成并通过前，
文档不预写数值。

## 7. 回迁边界与已知问题

- 回迁到 `random-room-runtime` 时，gameplay 根/安装点仍是权威；内核只模拟根外触手，
  不使用移动生物的 tether 配方，也不移动 `CharacterBody3D`。
- `ITerrainQuery` 仍只查询可站立的静态地形。猎物、道具、伤害、逃脱、死亡和吞入由宿主根据
  `TentaclePlantTargetEffect` 处理。
- 当前地形绕障是固定预算的局部导引，不是 3D 导航或全局最短路；复杂凹洞、活动门和会移动的
  遮挡物不在首版保证内。
- 首版不模拟段链自缠、不同拟态草互相缠绕或触手与动态刚体的逐段碰撞；目标交互集中在手端。
- 不实现水流、浮力、游泳或原作 Amphibious 分支；也不包含 PoleMimic 的伪装攀爬面、
  GarbageWorm 的抓矛行为和正式美术。
- 确定性承诺为同机同构建、同 seed、同 tick 输入 bit-exact；不承诺跨平台浮点逐位一致。
