# M5 回迁契约：ProcAnim.Core → random-room-runtime

> 本文是 M5 的核心产物：定义运动内核与宿主之间的**全部边界**——在本仓库里宿主是白盒沙盒，
> 回迁后宿主是 [`random-room-runtime`](../../random_room/random-room-runtime/) 的怪物系统。
> 内核侧事实以本仓库 `core/` 为准；主项目侧事实以其 `docs/procedural_monster_visual_spec.md`
> （下称「规格」）为准，引用处标注章节号。主项目对接面的调研快照见 §8。

---

## 0. 回迁三步（TL;DR）

1. **拷 `core/` 一个文件夹**进主项目（两条路线见 §8.2；命名空间归化 = 一次 sed）。
2. **按主项目射线规范实现 `ITerrainQuery`**（§6 有对照模板；`core/godot/RaycastTerrainQuery.cs`
   是参考实现，约 120 行——射线 + GetRestInfo 球体穿透 + 掩码/排除 API + 查询对象复用）。
3. **宿主 tick 里装 0.025s 累加器**驱动选定物种控制器的 `Tick`，渲染读
   `LerpPos(插值分数)`（§3）；回归先跑对应的 `core/smoke` / `core/spider_smoke`
   / `core/cicada_smoke` / `core/tentacle_plant_smoke` / `core/deer_smoke`
   / `core/daddy_long_legs_smoke` / `core/dropbug_smoke`
   （秒级、无引擎），再照主项目 MotionSmoke 惯例加一条 headless 探针（§7）。

## 1. 模块清单与依赖面

### 1.1 目录分层与命名空间（`core/`）

内核按**依赖方向**分层，命名空间跟随目录。上层可依赖下层，反向不行；十个物种后端互为平级、
只共享底座（唯一例外见下方白名单）。

```
core/
├── physics/            ProcAnim.Core.Physics        chunk 物理与碰撞解算
├── terrain/            ProcAnim.Core.Terrain        唯一接缝（ITerrainQuery + 解析实现）
├── host/               ProcAnim.Core.Host           宿主每 tick 传入的驱动契约
├── diagnostics/        ProcAnim.Core.Diagnostics    状态哈希（回归用，不参与运动）
├── species/            ProcAnim.Core.Species        跨物种装配枢纽（BodyFactory）
│   ├── lizard/         ProcAnim.Core.Species.Lizard
│   ├── humanoid/       ProcAnim.Core.Species.Humanoid
│   ├── spider/         ProcAnim.Core.Species.Spider
│   ├── centipede/      ProcAnim.Core.Species.Centipede
│   ├── cicada/         ProcAnim.Core.Species.Cicada
│   ├── vulture/        ProcAnim.Core.Species.Vulture
│   ├── tentacle_plant/ ProcAnim.Core.Species.TentaclePlant
│   ├── deer/           ProcAnim.Core.Species.Deer
│   ├── daddy_long_legs/ ProcAnim.Core.Species.DaddyLongLegs
│   └── dropbug/        ProcAnim.Core.Species.DropBug
├── godot/              ProcAnim.Core.Terrain        引擎适配器（**归宿主程序集**，见 §1.2）
└── smoke/ spider_smoke/ cicada_smoke/ tentacle_plant_smoke/ deer_smoke/
    daddy_long_legs_smoke/ dropbug_smoke/           无引擎回归工程
```

两处**有意的目录 ≠ 命名空间**：`godot/` 的适配器实现 `Terrain` 的接口却不属于内核程序集，
留在顶层是回迁隔离区（拷 `core/` 时它跟着走，但由宿主 csproj 单独编入）；
`AssemblyInfo.cs` 是程序集属性，无命名空间。

跨物种引用由 `core/smoke` 的 **`[CORE-MODULARITY]` 源码扫描**强制（与 §1.2 的 TypeRef
引擎边界扫描并列）：`species/<物种>/` 下出现白名单外的另一物种命名空间即回归 FAIL。
白名单当前只有一条 **Humanoid → Lizard**（人形腿复用 `Limb` 的 opt-in `LookaheadTicks`
与 `MoveIntentDeadzone` 常量）。扫描走源码而非 IL：编译期常量在 IL 里被内联，
元数据扫描看不见——`MoveIntentDeadzone` 恰好就是这一类。

### 1.1b 文件清单

| 文件 | 职责 | ≙ RW |
|------|------|------|
| `physics/BodyChunk.cs` | 带质量球形粒子，纯数据（Pos/LastPos/Vel/Radius/接触态 + RotationChunk 朝向参照与派生 `Rotation`） | BodyChunk（含 rotationChunk/Rotation） |
| `physics/ChunkConnection.cs` | 距离连接：软弹簧 + 硬约束（Rigid/PullOnly/PushOnly + SoftOnly 姿态弹簧档）；构造时两端互绑 RotationChunk（后建覆盖）；逐连接释放诊断计数 | BodyChunkConnection / ConnectToPoint |
| `physics/Body.cs` | chunk 容器与 tick 顺序：受力→积分→约束松弛→地形碰撞→卡角时恢复碰撞新增的姿态违反→卡链释放 | GenericBodyPart.Update 帧序 + BodyPart.Reset |
| `physics/ContactManifold3D.cs` | 单 tick 固定容量接触法线集合；固定迭代投影到非正交墙/地接触可行锥 | 3D 接缝扩展 |
| `physics/SphereTerrain.cs` | 球 vs 地形命中解算（无反弹+切向摩擦），Body/Limb 共用 | PushOutOfTerrain 语义 |
| `terrain/ITerrainQuery.cs` | **唯一接缝**：射线 + 球体穿透（MTD）两原语 + TerrainHit（零法线 = HitFromInside） | —（Godot 移植层） |
| `host/TickContext.cs` | 每 tick 环境包（重力/地形/tick 序号），传值、内核不持引擎对象 | — |
| `host/MoveTargetKind.cs` | 并列控制器共用的推进目标来源观测枚举；调试层只读目标数据，不依赖具体物种 | — |
| `species/lizard/Limb.cs` | 腿粒子：单点追目标 IK + plant-and-trail + FindGrip 射线落点 + 闲置休息位 | BodyPart/Limb/LizardLimb |
| `species/lizard/LizardLocomotionController.cs` | 蜥蜴专属运动控制器：重力开关、支撑系、意图重定向、推进力、翻越三件套 | Lizard 移动块 |
| `species/humanoid/Arm.cs` | 手臂粒子：三模式追猎（Dangle/HuntAbsolute/HuntRelative）+ 臂长钳制（adaptVel/exaggerate 甩动感）+ 腋窝排斥；不回传力 | Limb 基类追猎核心 + ScavengerHand 机械部分 |
| `species/humanoid/HumanoidLocomotionController.cs` | 人形（双足）运动控制器：清醒近地失重 + 站立力偶伺服（摔倒/爬起零状态机）+ 髋高度伺服 + knuckle 撑点俯仰泵 + 手臂优先级链（昏迷→投掷→蓄力→指向→持物→撑地→闲置）+ Conscious/PointTarget/Carrying/Throw API | Scavenger.Act + ScavengerHand.Update 优先级链 |
| `species/lizard/BreedParams.cs` | 蜥蜴品种参数表（纯出生配置，运行时零回读） | LizardBreedParams 运动子集 |
| `species/humanoid/HumanoidParams.cs` | 人形品种参数表（同为纯出生配置；数值 = Scavenger 反编译直接换算） | Scavenger 构造参数 |
| `species/BodyFactory.cs` | 通用装配器 + 蜥蜴四预设（`AllBreeds()`）+ 人形三预设（`AllHumanoids()`：scavenger/brute/waif）+ 秃鹫四预设（`AllVultureBreeds()`），三张路由表互不混装 | LizardBreeds / Scavenger 构造 / Vulture 构造 |
| `species/centipede/CentipedeLeg.cs` | 蜈蚣足端：真实地形抓点 + 确定性行波；抓握调制本节支撑，不刚性反拉身体 | 独立 3D 扩展 |
| `species/centipede/CentipedeLocomotionController.cs` | 蜈蚣专属控制器：双端表面轨迹、逐节局部支撑、全向贴面与有上限自避 | 独立 3D 扩展 |
| `species/centipede/CentipedeParams.cs` | 全局运动参数 + 默认体型曲线 + 任意节出生参数与稀疏逐节覆写 | 独立 3D 扩展 |
| `species/centipede/CentipedeFactory.cs` | 质量加权装配器 + short/long/armored/ribbon 四个稳定 ID 预设 | 独立 3D 扩展 |
| `species/spider/SpiderLeg.cs` | 蜘蛛足端粒子：plant-and-trail、全方向 FindGrip、稳定 pole 两段 IK 膝点 | BigSpider/Spider 足端与图形层 IK 分层 |
| `species/spider/SpiderLocomotionController.cs` | 蜘蛛专属运动控制器：多锚点抓地汇总、支撑低通、归一化推进、线性链拖尾 | 独立 3D 涌现实现 |
| `species/spider/SpiderBreedParams.cs` | 有序线性身体链 + 显式腿对锚点配置 | BigSpider 拓扑的可配置扩展 |
| `species/spider/SpiderFactory.cs` | 小型/大型正式预设 + 三节多锚点测试预设 | BigSpider 两节八腿拓扑 |
| `species/cicada/CicadaLocomotionController.cs` | 蝉专属控制器：双 chunk 差分升力、悬停、显式停驻、起飞与 Charge | Cicada.Update / Act |
| `species/cicada/CicadaParams.cs` | 蝉出生参数（尺寸、飞行动力、翼/触须表现尺度） | Cicada 个体差异的确定性子集 |
| `species/cicada/CicadaFactory.cs` | 双 chunk 身体装配 + light/dark 两个预设 | Cicada 构造 |
| `species/cicada/CicadaWingState.cs` / `species/cicada/CicadaTentacleState.cs` | 四翼与四条单点触须的固定 tick 表现状态；不向身体回传推进力 | CicadaGraphics |
| `species/tentacle_plant/TentacleChain.cs` | 独立多节触手原语：段长/柔性解算、局部导引折线、静态地形避让与回退 | Tentacle / TentacleChunk / TentacleProps |
| `species/tentacle_plant/TentaclePlantController.cs` | 锚定式拟态草控制器：确定性三维游荡、目标充能、Windup、突刺、抓取与回收 | TentaclePlant.Update |
| `species/tentacle_plant/TentaclePlantParams.cs` / `species/tentacle_plant/TentaclePlantFactory.cs` | 拟态草出生参数、安装框架、目标/效果纯值类型，以及 original/short/hunter 三个稳定预设 | TentaclePlant 构造与种内扩展 |
| `species/deer/DeerLeg.cs` | 独立多节腿段链：出生初始理想长、随 RestAmount 变化的当前 reach、固定次数距离约束、地形抓点、可及极限拖身、持久有向 bend pole 与摆动内段纵向/正交双通道形态响应 | DeerTentacle / Tentacle |
| `species/deer/DeerLocomotionController.cs` | 鹿专属控制器：恒重力支撑、非均匀推进、换步互锁、犹豫、连续体高与 COM balance | Deer.Act / Deer.Update |
| `species/deer/DeerParams.cs` / `species/deer/DeerFactory.cs` | 鹿独立参数表与头、重叠躯干、轻鹿角、四条多节腿装配；original/compact/strider 三个稳定预设 | Deer 构造 + DeerTentacle 构造 |
| `species/daddy_long_legs/DaddyTentacle.cs` | 独立多段触手：逐段扫掠/贴面/MTD、停驶落点保持、LastPos 优先残余回滚、物理半径裁边的 tick-end 邻边审计、无张力断边与原子后缀恢复、正交 Task/Needed 职责、打断与外部够取效果 | DaddyTentacle + Tentacle |
| `species/daddy_long_legs/DaddyLongLegsLocomotionController.cs` | 无前向完整图球团控制器：整链支撑汇总、移动期 1.2× / 停驶后至多 1g 的连续重力回补、方向抓点推进、职责预算、换步与确定性卡住退化 | DaddyLongLegs.Act |
| `species/daddy_long_legs/DaddyLongLegsParams.cs` / `DaddyLongLegsMorphology.cs` | 独立预设参数与按 seed 冻结的可变球/触手形态、材料 frame landmark | DaddyLongLegs 构造的随机出生形态 |
| `species/daddy_long_legs/DaddyLongLegsFactory.cs` / `DaddyLongLegsTargetContracts.cs` | brother/daddy/terror 三稳定 ID、无状态确定性装配，以及按触手编号的纯值外部目标/效果接缝 | DaddyLongLegs / DaddyTentacle + 3D 宿主扩展 |
| `species/dropbug/DropBugLocomotionController.cs` | 掉落虫控制器：三节短链自撑、站稳计数与前后不对称重力、运行时收放静息长度的悬挂态、弹道俯冲、蓄力扑击、越障抬升与确定性卡住抖动 | DropBug.Act + DropBugAI 的运动子集 |
| `species/dropbug/DropBugLeg.cs` | **纯图形件**腿：步频随头部实际位移驱动，静止严格为零；不回传力、不计支撑（反编译实证腿为 `Limb[2,2]` 图形层） | DropBugGraphics |
| `species/dropbug/DropBugParams.cs` / `DropBugFactory.cs` | 掉落虫独立参数表与三节短链装配；original/nimble/bulky 三稳定 ID（未知 ID 快速失败） | DropBug 构造 |
| `species/vulture/VultureFlightController.cs` | 秃鹫飞行控制器：重力常开 + 拍翅同步升力脉冲、悬停锚、滑翔下降、起飞/降落由 MoveTarget 几何涌现、头部伺服 | Vulture.Act |
| `species/vulture/VultureWing.cs` | 翅膀段链粒子：只抗拉绳约束 + 行波 Flap / 射线抓附 Grab 两模式；除抓地悬挂拉力外对身体零回传 | VultureTentacle |
| `species/vulture/VultureBreedParams.cs` | 秃鹫品种参数表（与蜥蜴表平行、不混表；vulture/king/swift/quad 四预设） | Vulture 构造 + IsKing/IsMiros 分支 |
| `diagnostics/DeterminismHasher.cs` | FNV-1a 64 状态哈希折叠（沙盒探针与无引擎回归共用） | — |
| `terrain/PlaneTerrainQuery.cs` | ITerrainQuery 纯解析实现（无限平面），测试用 | — |
| `godot/RaycastTerrainQuery.cs` | **引擎适配器**（PhysicsDirectSpaceState3D 包装），归宿主程序集编译 | — |
| `smoke/` | 蜥蜴 / 蜈蚣 / 秃鹫 / 人形的无引擎冒烟回归 console 工程（§7.2） | — |
| `spider_smoke/` | 蜘蛛确定性、拓扑、两段 IK 与生命周期无引擎回归 | — |
| `cicada_smoke/` | Cicada 独立无引擎回归（飞行/停驻/Charge/3D 姿态） | — |
| `tentacle_plant_smoke/` | 拟态草独立无引擎回归（装配、三面游荡、攻击时序、目标效果、生命周期与确定性） | — |
| `deer_smoke/` | 鹿独立无引擎回归（装配、恒重力支撑、多节腿步态、地形、生命周期、确定性与机制消融） | — |
| `daddy_long_legs_smoke/` | DaddyLongLegs 独立无引擎回归（seed 形态、整链支撑、职责/换步、全向地形、打断、外部够取、生命周期、确定性与机制消融） | — |
| `dropbug_smoke/` | DropBug 独立无引擎回归（装配、前后不对称重力、悬挂收放与击飞冷却、俯冲、蓄力、越障、卡住、负重、表现腿、生命周期、确定性与九机制消融） | — |

### 1.2 依赖面（这是「解耦」的准确定义）

- 内核程序集 `ProcAnim.Core` **只使用 GodotSharp NuGet 的纯托管数学结构**（`Vector3`/`Mathf`）。
  注意准确性：GodotSharp 包里 `GD`/`Node`/`PhysicsServer3D` 也是**编译期可达**的——
  「不引 Godot.NET.Sdk」只挡住场景树源生成器，不是编译器强制。真正的强制是
  `core/smoke` 的 **TypeRef 边界扫描**：内核程序集出现允许清单（`Vector3`/`Mathf`）之外的
  任何 `Godot.*` 类型引用即回归 FAIL（离线、秒级，回迁后照跑）。
- 运行时实证：`core/smoke` 在**纯 .NET 进程**（非 Godot 可执行文件）里驱动内核确定运行。
  即：内核对引擎的依赖 = 一组 float 结构体的数学库用法，与引擎运行时零耦合。
- 保留 `Godot.Vector3` 而非自造数学类型是**有意为之**：回迁目标同为 Godot 4 C#，
  换类型是纯翻译成本 + 浮点语义漂移风险（`Normalized()` 零向量特判、Lerp 展开形式），
  确定性哈希会因此作废。
- `core/godot/RaycastTerrainQuery.cs` 是唯一需要引擎运行时的文件，**故意不在内核程序集里**
  （core csproj 排除 `godot/**`，宿主程序集自行编入）。
- 内核**不使用 `global using` 或 csproj `<Using>` 隐式导入**：每个文件的 using 块就是它的
  真实依赖面，读文件头即可知道它跨了哪几层。`[CORE-MODULARITY]` 扫描把这条也纳入断言
  （出现任一即 FAIL）——否则隐式导入会让 per-file using 不再等于依赖图，模块边界扫描变成假绿。

### 1.3 内核明确不做（≙ CLAUDE.md 非目标）

AI 寻路（`MoveDir`/邻近 `MoveTarget` 从外面来）、动态目标扫描与 gameplay 目标所有权、
战斗、游泳/水中运动、正式渲染与美术、
碰撞体/Area 的创建、宿主根节点的移动（集成姿态见 §8.3）、任何日志输出
（内核零 `GD.*`/`Console.*`）。

## 2. 装配契约（各物种 Params → Factory）

```csharp
BreedParams p = BodyFactory.Heavy();            // 或 Default/Sprinter/Hexapod/ByName(name)
LizardLocomotionController controller = BodyFactory.CreateLizardController(origin, p);
```

- `BreedParams` 是**纯出生配置**：工厂读表装配，内核运行时不回读、零行为分支。
  改品种 = 重新装配一只（沙盒数字键换品种即此路径）。
- `ByName` 未知名**静默回落 default**（内核零日志）；调用侧要告警自行比对返回值 `Name`。
- 装配结果：脊柱 = `SpineSegments` 个 chunk 的 Rigid 链（头…髋）+ 隔节防折叠支柱
  （`BodyStiffness`，SoftOnly PushOnly）；腿对沿脊柱均匀锚定、相邻对出生错位相反
  （对角步态相位种子）；尾巴 = 渐细 PullOnly 链。
- **朝向拓扑不变量**（smoke `[CORE-ROTATION]` 断言，宿主自装身体也应遵守）：建
  `ChunkConnection` 时两端自动互绑 `RotationChunk`（≙ RW BodyChunkConnection 构造副作用，
  后建覆盖）；工厂装配完**显式钉定**脊柱——头参照髋（`Rotation` = 头髋长基线 = 全身轴前向）、
  中段参照后一节（真·本段轴；3 节脊柱时后一节即髋 ≙ RW 中→髋，四节以上仍是相邻段——
  统一指髋会退化成跨关节长弦）、
  髋参照头（指向后方，`LizardLocomotionController.TickLimbs` 翻转，≙ RW LizardLimb `connection.index==2` 补偿）。
  腿的每锚点步进方向由此导出：头/髋锚 = 脊柱长基线轴，中段锚（hexapod 中腿对）= 本段朝向。
  钉定不走建链顺序的巧合（RW Lizard 的 头→髋 是防折叠连接恰好最后建的副产物，我们显式化，
  仿 RW Deer 构造后重申指向的先例）。
- **推进追踪点不变量**（`LizardLocomotionController` 构造契约，2026-07 追加）：构造函数签名是
  `LizardLocomotionController(body, head, hips, spineFollower)`——`spineFollower` 必须是脊柱链紧邻头部的下一节
  （≙ RW `bodyChunks[1]`；`BodyFactory` 固定传 `chunks[1]`；spine=2 时与 `hips` 是同一 chunk）。
  配套 `LizardLocomotionController.HeadLinkLength` 必须是头到 `spineFollower` 这一条连接的静止长度（单节，不是
  脊柱全长）。`LizardLocomotionController.ApplyLocomotionForce` 只主动驱动 `Head`/`SpineFollower` 两点，链尾
  `Hips`（spine≥3 时）永远被动拖行只挨 `ChunkConnection` 约束——宿主手动装配身体（不经
  `BodyFactory`）必须遵守同一约束，否则多节脊柱会在头到髋的欠约束自由度上折叠成 V 形
  （反编译 `Lizard.cs:2277-2293` 核实：RW 原版同样只驱动 `bodyChunks[0]`/`[1]`，`bodyChunks[2]`
  从不被直接追踪；`straightenOut` 恢复力的判定轴与施力点同样钉在 `spineFollower`，不是 `hips`）。
  **墙角恢复不变量**（2026-07 深挖修复）：`straightenOut` 不再把本地 V 折叠也沿目标轴推；目标误朝向
  与局部弦向撑开分离，并用 `StraightenOutNeeded` 保存跨 tick 恢复需求。`SpineCornerStuckTicks`
  由「髋接触 + 髋低速 + 100° 进入/120° 退出滞回」导出，不依赖仍在移动的头速；达到 10 tick 后：
  ① 只恢复碰撞相对松弛末**新增**的 SoftOnly 支柱违反，候选位移经 `ContactManifold3D` 固定迭代
  投影，并在写回前用 `TerrainRadius` 做最终 `SpherePenetration` 校验；不重跑整套约束、不重复表面
  摩擦；② `Hips.TerrainSqueeze` 在 10→30 tick 从 1→0.05，地形有效
  半径下限 0.025m，正式 `Radius`/约束/腿锚/渲染不变。`Shift` 保留恢复状态；`Teleport`/`Launch`
  清零并立刻恢复碰撞半径。所有计数映射强度均显式饱和到 `[0,1]`（Godot `InverseLerp` 本身
  不 clamp）；180° 辅助绕上一 tick 的 `SupportNormal`，零输入会切断旧意图方向历史。三锚六腿在
  RW Caramel/SpitLizard 有拓扑先例，腿粒子不向身体回传
  反力，因此不得再把该问题解释成「hexapod 第三腿对把 hips 钉住」。
  **上墙交接姿态不变量**（2026-07 FrontMount 修复）：多节脊柱不能只检查内部夹角——
  Head→SpineFollower 即使近 180° 展开，整条仍可能沿墙法向成为水平旗杆。无中段腿的
  多节拓扑在 Head 腿拿到射线背书的墙面落点时，即可用未经全局混合/低通的 `GripNormal`
  预构造局部爬升方向；腿同步换步时仅以当前 `Head.TerrainContact+ContactNormal` 补位。
  `FrontMountGain=0.35` 让 SpineFollower 绕 Head 纯切向回摆并让 Head 沿面走，强度按
  `RunSpeed` 缩放，速度注入只补到目标值的 0.75，不逐 tick 累积。预摆内部角到 120° 时
  停止追加伺服（不是硬角度钳制）；前段进入沿墙 30°、`SupportNormal·localNormal≥0.8`、
  后段踩同面或 Crest 均熄火。
  不得缓存过期墙法线、沿法线吸墙、直接驱动 Hips 或伪造抓地。后段随后由
  `RearBraceGain=0.15` 回摆到已经正确的前段轴后方；有中段腿的拓扑不启用这条辅助。

### 2.1 参数可行域（M4 调参教训，超出即近瘫）

| 参数 | 默认 | 可行域 | 越界症状 |
|------|------|--------|----------|
| `LimbSpeed` | 0.15 | **≥ 0.12** | 脚追不上身体，抓地长期 <1，重力开关反复打开 |
| `StepLength` | 0.7 | **≤ 0.75** | 同上（步幅大 = 脚要追更远） |
| `LegPairDisplacement` | 0.45 | ≤ 0.5 | 外撇过远参与「追不上」叠加 |
| `LimbQuickness` | 0.6 | 0.4~0.8 | 过低脚飘、过高抖 |
| `BaseSpeed`/`MaxMoveSpeed` | 0.06/0.08 | 成对调，Max 略高于 Base | Max 过低推不动坡，过高贴墙滑升失控 |

**「笨重感」不要用腿参数表达**（它们碰抓地循环）——用 `SpineSegments`、`BodySizeFac`、
`TailSegments/TailStiffness`、`BodyStiffness`、`SmoothenLegMovement=false`（绿蜥式拖沓）表达。

### 2.2 蜈蚣并列后端

```csharp
CentipedeParams p = CentipedeFactory.Long();
CentipedeLocomotionController controller =
    CentipedeFactory.CreateController(origin, p);
```

`CentipedeParams`、`CentipedeSegmentParams`、`CentipedeSegmentOverride` 同样是纯出生配置；
工厂解析出逐节深拷贝后，控制器运行时不回读源对象。最少 2 节、无编译期上限，当前 smoke
固定覆盖 32 节。相邻节用质量加权 Rigid 连接，隔一节用 SoftOnly PushOnly 防折叠支柱。
稳定 ID 为 `centipede/short`、`centipede/long`、`centipede/armored`、
`centipede/ribbon`，未知 ID 快速失败。

蜈蚣不是 `LizardLocomotionController` 的模式分支：它以双端、带 `ArcLength` 的表面轨迹
驱动各节目标，并以真实 `CentipedeLeg` 抓点决定逐节支撑。领航端由宿主写
`RequestedLeadEnd` 显式选择，`LeadEnd` 只报告已应用状态；`MoveDir`/`MoveTarget` 不负责
推断或切换头尾。两端各自保存有向表面切线；输入投影在换面时退化，则沿当前领航端既有
切线平行运输续行，而非用世界轴猜方向。完整装配、运动、观察与验证契约见
[`centipede_controller.md`](centipede_controller.md)。

### 2.3 蜘蛛装配契约

```csharp
SpiderBreedParams p = SpiderFactory.SmallSpider(); // 或 LargeSpider/ByName(name)
SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(origin, p);
```

- `BodySegments` 必须至少两项，按列表顺序装成唯一的 Rigid 线性链；本后端不表达分支或环。
- 每个 `LegPairSpec.AnchorSegmentIndex` 显式指定锚点节，同一节可挂任意多对腿，也允许多节挂腿。
  `spider-small` / `spider-large` 固定为两节、四对腿全部挂第 0 节；第 1 节无腿。
  `SyntheticMultiAnchor()` 只用于回归，证明三节、多锚点没有被控制器写死。
- 未知 `SpiderFactory.ByName` 名称快速失败；所有节半径/质量/连接参数、腿长、足端参数和锚点
  索引在装配前验证，避免坏配置静默退化。
- 身体总推进先按抓地比例缩放，再按锚点上的真实抓地贡献归一化分配；增加腿或挂腿身体节不会
  线性增速。无腿的后续身体节只由连接和连续拖尾姿态弹簧跟随。
- `SpiderLeg.Pos` 是唯一足端运动/抓地状态。`RootPos → KneePos → Pos` 是渲染姿态：
  上下两段长度由余弦定理解，持久 `BendPole` 在共线与换面时保持手性，膝不参加碰撞或承力。
  足端最大可达距离小于两段长度之和，保证可见弯折。
- `LegPairSpec.StepLength` 继续表达静态工作区的名义伸展；可选 `TrailReleaseRatio` 独立决定
  抓点落后多少腿长后抬腿。正式预设另启用 `UseExplicitTouchdownLead`：抬腿时保留足端
  横向工作区，并把 `TouchdownLeadRatio` 定义的 AEP 冻结在世界空间，摆动期间不再追逐
  每 tick 前移的身体。四对腿使用由前至后递减的 lead，左右腿反相、相邻腿对交替，组成
  两组确定性的对角四足步态；大蜘蛛保留长腿、四腿最低支撑和慢抓握，也能完成一次可见前摆。
  `ForwardStepBias` 仍只调整静态候选中心，不代替正式摆动的冻结 AEP。
- 身体急转时，仍在旧世界抓点上的腿可以短暂跨过新的局部中线；这属于合法接触期，不能集体
  强制松脚。该腿下一次抬起并捕获 AEP 时，若其已确认的上次抓面与当前支撑面仍近似一致，
  会先把跨线横向分量关于本腿根部面镜像一次。仅回到正确半边仍不够：正但很小的站距也会
  被显式 AEP 永久复制，因此同一 tick 还会把横向分量向该槽位由
  `FanAngle/PairLateral/StepLength` 推导的名义宽度回收 60%。只有本腿、配对腿的上次抓面
  都与当前支撑面一致且彼此同面（法线三组 dot 均不小于 0.85）时才回收；不同法线的窄墙/
  棱角多面抓握不套用。冻结后的世界 AEP 不逐 tick 重映射，已植脚也不滑动。因此没有新增
  转向模式，也不会以碎步、瞬时失抓或牺牲抱边来隐藏换向过渡。
- 已抓点若越出可达环，会以 `ReachRecovery` 开始一轮完整摆动；已处于摆动的腿不会被
  逐 tick 再次初始化。控制器先为全部腿更新同一 tick 的根部坐标，再按“相位匹配或硬超距”
  的确定性优先级发放有限松脚许可，因此不会出现后腿不断清零摆动、只向前挪一点的假步态。
- 落脚仍按固定顺序搜索真实 TerrainHit。直接/当前支撑/历史抓面/世界向下/局部扇形候选
  统一按足端球心到 AEP 的距离仲裁；同侧横向反投影另行保存，只在命中本侧横向面或前方
  正交新面、且仍处于有限 AEP 距离余量时才可替换旧支撑候选。左腿只找左侧、右腿只找右侧，
  使超出窄端面轮廓的腿抱住相邻侧面，又不会让普通墙角的远侧面抢走合适落点。它不增加
  “窄墙”状态；命中仍须通过可达环及目标面真实接触背书。
- `MoveDir` / `RunSpeed` / `MoveTarget` / `AtMoveTarget` 及
  `Shift` / `Teleport` / `Launch` 与蜥蜴入口同形；生命周期操作会完整覆盖足端、膝、pole、
  抓点和步态状态。地面、墙、斜坡、角落、天花板没有模式字段。

### 2.4 Cicada 装配契约

```csharp
CicadaParams p = CicadaFactory.Light(); // 或 Dark / ByName
CicadaLocomotionController controller =
    CicadaFactory.CreateController(origin, Vector3.Forward, p);
```

- Cicada 是与 Lizard **并列**的 locomotion backend，不复用 `BreedParams`、`BodyFactory` 或
  plant-and-trail `Limb`。两者只共享 `Body` / chunk / connection / terrain 原语。
- `CreateController` 会冻结一份参数快照，运行时不再回读调用侧的可变 `CicadaParams`；
  `BodyMass` 由 Cicada 后端换算为力响应，不要求共享 `Body` 改写 Lizard 的积分语义。
- 身体固定为前体、后体两个 chunk + 一条 Rigid 连接；默认 RW 单位换算为半径约
  `0.1875m / 0.175m`、连接长 `0.35m`。前体 `RotationChunk=Rear`，后体反向引用。
- 四翼和四条触须是确定性表现状态。它们可在停驻时贴面，但不产生升力、不计支撑、不向身体回传力。
- `MoveDir` 是完整 3D 单位向量，`RunSpeed` 是 `[0,1]` 强度；可选 `MoveTarget` 同样是完整 3D
  路径点。宿主 AI/导航负责给点，内核不内置寻路。
- `RequestPerch(TerrainHit)` 只接受有效法线，缓存点、法线、切向前向和 collider ID；地板、墙、
  天花板走同一公式。稳定停驻时 `Up=Normal`、另外两轴位于切平面，翼/触须不得穿面；
  普通碰撞不会自动落地，表面验证失败时自动复飞。
- `TakeOff` / `TryStartCharge` 是显式动作。Charge 方向开始时锁定，动态对象伤害/抓取不属于
  `ITerrainQuery`，首版只处理静态地形反弹或中断。
- `Shift` 平移全部世界坐标记忆并保留状态；`Teleport` / `Launch` 清飞行目标、停驻和 Charge。
- RW 的 `stamina` 主要服务负载、黏附和被抓交互；这些宿主接缝未引入前，不得把它误作普通飞行燃料。

### 2.5 秃鹫装配契约（VultureFlightController）

```csharp
VultureBreedParams p = BodyFactory.Vulture();      // 或 KingVulture/Swift/QuadWing/VultureByName(name)
VultureFlightController bird = BodyFactory.CreateVultureController(origin, p);  // origin = 肩线中心
```

- 与蜥蜴并列的**飞行**后端，只共享 `Body`/chunk/connection/`ITerrainQuery`；`VultureBreedParams`
  与 `BreedParams` 是两张互不混装的表（`AllVultureBreeds()` / `IsVultureBreed(name)` 路由；
  未知名回落基准 vulture，同蜥蜴 `ByName` 的静默回落语义）。
- 身体 = 四 chunk 刚架（前脊柱 / 后脊柱 / 双肩，六条 Rigid 全三角化，`RestLength` 取出生几何）
  + 头 PullOnly 拴绳（`WeightA=0`：头的重量拖不动身体）。翅膀挂在肩 chunk 上。
- **与地面生物的根本差异：重力常开，没有重力开关。** 升力 = 与拍翅相位同步的 sin² 脉冲
  （谷值恰为 0），只注入后脊柱单 chunk，由约束松弛摊到四个躯干 chunk，周期均值与重力平衡
  ——**悬停时的上下颠簸是这套机制的直接后果（≙ RW 手感），不是 bug**。下降不施向下力：
  意图朝下时倒拨拍翅相位（`FlapGlideRate`），冻结在低升力半区滑翔。
- **升力逐翅注入，不设 `AirBorne` 外层门**（≙ RW 写在每只翅膀的 Fly 分支里）：混合翅态下仍在
  拍的翅膀继续托身体——「单翅失能 → 失衡侧倾」的涌现以此为前提。全翅 Grab（栖息）时天然零注入。
- **没有 locomotion 状态机**：模式在每只翅膀上（`VultureWing.WingMode.Flap|Grab`），
  `AirBorne`/栖息由翅膀组合涌现。起飞/降落由 `MoveTarget` 几何触发（落点贴地探测 + 进入
  `LandEngageRadius` → 全翅 Grab + 制动 + 切换锁 `LandingModeLockTicks`；栖息 + 远/悬空目标 →
  全翅 Flap + `TakeoffBoostTicks` 助推）。切换锁存在的意义是防止俯冲降落被坠落自救掰回 Flap。
- `VultureWing` **不复用 `Limb`**（plant-and-trail 是地面步态状态机）：段链粒子 + 只抗拉绳约束，
  对身体零回传，唯一例外是抓地时的悬挂拉力。`Flap` 的扫掠幅度（5~15m）故意远超可达距离——
  伺服饱和截断才是有效机制，不要把它当成几何目标去「修正」。
- 注速一律做 headroom 钳制（`MaxFlySpeed`/`MaxRiseSpeed`）：连续喂点必须显式封顶，同蜥蜴
  `MaxMoveSpeed` 的论证。
- 直喂契约的飞行版补充（实测教训）：巡航路点须离地形 **≥ 降落贴地探测深度（1.2m）**，否则内核
  会如实降落；下降段要给滑翔垂度留约 1m 余量，否则擦墙顶。

### 2.6 拟态草装配契约（TentaclePlantController）

```csharp
TentaclePlantParams p = TentaclePlantFactory.Original(); // 或 Short / Hunter / ByName
TentaclePlantMount mount = new(mountPoint, outwardNormal, tangentHint, colliderId);
TentaclePlantController plant =
    TentaclePlantFactory.CreateController(mount, p, seed);
```

- 拟态草是与 Lizard 并列的**固定式伏击后端**，不复用 `BreedParams`、`BodyFactory`、
  `Limb` 或移动输入。原作当前 DLL 的 `Tentacle` 自身也是独立类，不继承 `BodyPart` / `Limb`。
- `TentaclePlantFactory` 提供 `Original/Short/Hunter/AllPresets/ByName/CreateController`；
  稳定 ID 为 `tentacle-plant/original|short|hunter`，未知名称快速失败。创建时冻结参数快照。
- `TentaclePlantMount(Point, OutwardNormal, TangentHint, ColliderId)` 定义宿主安装真相。
  核心正交化得到 `Outward/Tangent/Bitangent`；地板、墙、天花板走同一局部公式，
  不按世界 `Up` 分支。退化 hint 使用固定回退，不能引入随机 roll。
- 原作基准为 2 body chunk、0 connection + 独立 8 段触手，理想长度 `300px=7.5m`、
  根/梢半径 `8px/1px=0.2m/0.025m`。本项目的 `TentacleChain` 只共享 chunk、
  `ITerrainQuery` 和固定 tick 底座，不修改蜥蜴的积分或 plant-and-trail 语义。
- 三维游荡区是以 `Root + Outward × WanderCenterDistance` 为球心、以
  `WanderRadius` 为半径并裁掉 mount 安装平面后方部分的轴对称球冠；`GuidePoints`
  只公开 0–2 个真实折点（根与目标为隐含端点），`BacktrackFrom` 为 `-1` 或首个需回卷的
  触手段索引。它们表达当前局部绕障和回退证据，是核心输出；Godot 沙盒只渲染，不参与决策。
- 宿主每 tick 写 nullable `Target`（`TentaclePlantTargetSnapshot` 纯值，字段为
  `StableId/Position/VelocityPerTick/Radius/Mass/HostVisible/HostGrabbable`）。核心不扫描动态
  物体、不持有 Node/对象引用；`TargetEffect` 每 tick 覆盖，携带
  `TargetId/CaptureStarted/Held/PositionCorrection/VelocityDelta/Released/ConsumeRequested`，
  由 gameplay 权威目标应用。
- `TentaclePlantPhase` 为
  `Wandering/Tracking/Windup/Striking/Recovering/Holding`。original 对有效可见目标充能
  90 tick，过半进入明显 Windup，再突刺 10 tick；突刺结束后抓取窗再衰减 40 tick。
  命中后约 80 tick 完全缩回，再以距根 `0.5m` 或额外强拉 30 tick 请求吞入。
  扑空后没有凭空增加硬 cooldown；`CanGrab` 余窗处于 Recovering 时即可并行重新积累充能。
- `Shift` 是世界 rebase，完整平移 mount、段链、目标记忆、wander goal、guide points 和
  插值历史；`Remount` 是地形不随体移动的重新安装，清攻击/抓取/导引并从新根重建；
  `ReleaseHeldTarget` 显式释放并通过下一次 `Tick` 的 `TargetEffect.Released` 通知宿主。

### 2.7 鹿装配契约（DeerLocomotionController）

```csharp
DeerParams p = DeerFactory.ByStableId("deer/original");
DeerLocomotionController deer =
    DeerFactory.CreateController(spawnFloorPoint, initialForward, p);
```

- Deer 是新的并列后端，不继承或调用 `LizardLocomotionController`，也不复用 `Limb`、
  `BreedParams` 或 `BodyFactory`。它只共享 `Body` / `BodyChunk` / `ChunkConnection`、
  `ITerrainQuery`、`TickContext` 和确定性哈希底座；既有共享积分与其它物种代码不需要改动。
- `DeerFactory` 提供 `Original/Compact/Strider/AllPresets/TryByStableId/ByStableId/CreateController`；
  稳定 ID 为 `deer/original|compact|strider`，未知 ID 快速失败，创建时深拷贝并冻结参数快照。
- 身体拓扑是头 + 大幅重叠的有序躯干链 + 大而轻的鹿角 chunk。头与鹿角互设
  `RotationChunk`；躯干逐节钉定朝向参照。四条腿锚在靠前躯干节上，每条都是独立的
  `DeerLegSegmentState[]` 多节物理链，不把蜥蜴单粒子腿改造成通用段链。
- 头相对首躯干的 3D 目标轴为 `normalize(0.85Up+0.60Forward)`；鹿角物理代理的目标中心为
  `Head + normalize(Up+0.18Forward)*antlerLink`，因此在头上方略向前，而不是躯干内。
  original 的 `7.5m` 是出生初始理想链长，hard 最大腿长为 `10m`；当前 reach 按
  `MaxLength*Lerp(1/3,1,(1-RestAmount)^5)` 连续变化。`6→2.64m` BodyCenter 目标是明确的
  3D 映射，因为原作 `preferredHeight` 只直接驱动动态腿长，没有同名的身体高度伺服。
- `DeerLegSlotParams` 逐腿冻结前后外撇和左右外撇；控制器以身体 forward、真实支撑法线 up
  及其叉乘 right 构造 3D 工作区。控制器的持久 support frame 在换面时平行运输；
  `DeerLeg` 在当前真实 Root→Tip 法平面内分别构造正交的前后与左右解剖基；旧 pole 在上一
  frame 的 `Forward/Up/Right` 分量会重建到新 frame，再以踩实/摆动 `0.12/0.22 rad/tick`
  的确定性上限沿有向圆转向。主链仍沿候选弦预展开以保持台阶可达性；所有约束结束后，仅
  Swinging 内段获得有上限的 longitudinal 主通道和较弱正交外撇通道速度修正，Attached 不介入。
- `Body.GravityScale` 始终为 1。确认抓地腿按直立度和左右展开算连续支撑，再把升力与推进
  以不同权重沿头/躯干分布；失抓不会切换成蜥蜴的关重力路线。四腿统一评分换步、同对互锁、
  前方无抓点犹豫、候选滞回、超距/遮挡释放、可及极限拖身、连续地板预看与休息降高都在
  同一套连续机制里完成，没有攻击或 locomotion 模式枚举。
- `AtMoveTarget` 可立即取得休息资格；普通无输入必须超过 `RestDelayTicks`（正式预设为
  160/160/200 tick）。取得资格后，远于当前 reach 的旧脚只在四脚确认支撑时逐条重落，
  不能把动态缩限直接当作四条腿的同 tick 物理失效。
- 宿主接口与并列移动后端同名同义：`MoveDir` / `RunSpeed` / nullable `MoveTarget`，以及
  `AtMoveTarget`、`Shift` / `Teleport` / `Launch`。完整 DLL 取证、原作数值、3D 偏离理由、
  观测面和验证边界见 [`deer_controller.md`](deer_controller.md)。

### 2.8 长腿爸爸装配契约（DaddyLongLegsLocomotionController）

```csharp
DaddyLongLegsParams p = DaddyLongLegsFactory.ByStableId("daddy-long-legs/daddy");
DaddyLongLegsLocomotionController daddy =
    DaddyLongLegsFactory.CreateController(spawnPoint, p, stableSeed);
```

- 工厂入口没有 `forward`：身体是出生时生成的完整连接球团，没有头尾轴。
- 同一参数与 `ulong stableSeed` 冻结同一球数、半径、质量、完整图静息距离、触手数、逐条长度/
  段数和 Fibonacci sphere 材料偏好；不同 seed 的形态差异由专项 smoke 扫描。
- 每条触手无条件使用 `MinimumSegmentsPerTentacle=3`；原作只有启用 MMF 时才把最小段数钳到 3。
- 触手按索引轮流锚到身体球，每条 `DaddyTentacle` 自有段链，不复用 `Limb`、`SpiderLeg`、
  `TentacleChain` 或其它物种参数。
- 三个稳定 ID 为 `daddy-long-legs/brother|daddy|terror`；未知 ID 快速失败。
- 形态上限与查询公式、当前 DLL 数值、Terror 缩限理由和全部 3D 取舍见
  [`daddy_long_legs_controller.md`](daddy_long_legs_controller.md)。
- 参数校验还证明最大触手数下的最短初始长度分配可装配，并要求最短可能生成的 `LinkLength`
  严格大于球壳/自避最小间距；这两个条件是后缀恢复候选可验证的装配前提。

### 2.9 掉落虫装配契约（DropBugLocomotionController）

```csharp
DropBugParams p = DropBugFactory.ById("dropbug/original");  // 或 Original/Nimble/Bulky
DropBugLocomotionController bug = DropBugFactory.CreateController(origin, forward, p);
```

- 身体固定为**三节短链**：头 / 中 / 尾（6/8/6px 换算），头-中、中-尾 Rigid + 头-尾 PushOnly
  防对折，`WeightA` 按质量反比（≙ RW `weight -1`），外加**仅运动时注入**的自撑力对。
- 三个稳定 ID 为 `dropbug/original|nimble|bulky`；未知 ID **快速失败**（不静默回落）。
- **腿是纯图形件**：`DropBugLeg` 的步频按头部实际位移比例驱动、静止严格为零，**不回传力、
  不计支撑**。这是反编译实证（原作腿为 `Limb[2,2]` 图形层），不是本项目的简化。
- **本后端有三点此前九个后端都没有的机制**，回迁时需特别注意：
  1. **运行时形变**：悬挂时三条连接的**静息长度**按 `HangFactor` 插值收缩（12→5 / 14→2 /
     8→0 px），中/尾停止碰撞埋入锚面，退出瞬时恢复。`RestLength` 是既有公开可变字段，
     **共享层零配合**——但这意味着宿主不能假设连接静息长度在生命周期内恒定。
  2. **弹道攻击**：脱悬俯冲先削速再施 21/16px 方向功率冲量；腾空期间持续头朝目标、中尾反向
     修正；高于目标 6.25m 且距 8.75m 内加水平修正；触地即停 + 20 tick 冷却。
  3. **前后不对称支撑**：站稳（`footingCounter > 10`，失稳 −3/tick、宽限帽 35）时前两节
     ×0.8 阻尼 + **全额**抵消重力，尾节只抵消 `Lerp(0.5, 1, stuck)` 且**无阻尼**（尾巴自然
     下垂）；倒退行走时尾节按前节处理。
- **悬挂点 3D 判据**（≙ 原作「空 tile + 上方 2 实心 + 下方空 + floorAltitude ≥6 tile」）：
  法线向下 ≥cos45° + 实体厚度探针 0.3m + 法向净空 0.6m + 世界竖直落差 ≥3m。贴附半径 1m +
  最后 1.25m 爬升辅助；**更高的锚由宿主给邻近可达位**（不移植原作的天花板攀爬）。
- **`Launch` 会设 40 tick 悬挂重贴附冷却**（≙ 原作 stun 窗口内 `Consious=false` 挡住重贴附）。
  没有它，≤0.30 m/tick 的击飞会被吸附伺服整个吃掉（2026-08-04 外部评审 P1）。
- 地面蓄力扑击：`charging +1/15`（头 `+c²`、中 `−4c` px），可及 =
  `LerpMap(dot(扑向, 身体轴), −0.1, 0.8, 0, 300px, 0.4)`——**侧对显著缩短**，目标出可及即
  逐 tick 放弃。越障抬升按「前进受阻且头落在中段后面」的原作字面条件**涌现**（实测点火于
  反转朝向）。卡住抖动 = 30 tick 窗口净位移 + 整数模数伪随机（原作 `Random` 的确定性等价）。
- **显式跨 tick 状态只有四个**：`HangFactor` / `PounceCharge` / `Diving` / `AttackCooldown`。
  其余（站 / 走 / 坠 / 倒退 / 越障）全部涌现，没有 locomotion 模式枚举。
- 控制器另暴露八个 `Enable*` 机制开关（`EnableTailGravityAsymmetry` / `EnableFootingGrace` /
  `EnableHangMorph` / `EnableDiveSteering` / `EnablePounceReachGate` / `EnableObstacleHop` /
  `EnableStuckShake` / `EnableBackwardsWalk`），**默认全 true，仅供 smoke 消融红灯使用**——
  宿主不要在运行时改动它们（会改变哈希）。
- 完整 DLL 数值、3D 取舍与有意偏离原作的清单见 [`dropbug_controller.md`](dropbug_controller.md)。

## 3. 驱动契约（tick 与渲染）

### 3.1 固定步长

- 逻辑固定 **40 tick/s**（`dt = 0.025s`）。内核零 delta 依赖：`Vel` 语义 = 「米/tick 位移」，
  积分 `Pos += Vel` 不乘 dt。**重力预乘步长平方**后传入：
  `gravityPerTick = (0, -g·dt², 0)`，默认 g=36 m/s²（≙ RW 0.9px/tick²）→ `-0.0225`。
- 每 tick 固定顺序：
  ```csharp
  terrain.Bind(space);                        // 仅 Godot 适配器需要（物理帧内合法）
  controller.MoveDir = …; controller.RunSpeed = …;    // 输入（§4；也可改写 MoveTarget）
  chunk.Vel += …;                             // 可选：外力（拖拽/击退），只许写 Vel
  controller.Tick(new TickContext(gravityPerTick, terrain, tick));
  ```
- `LizardLocomotionController.Tick` 内部序（勿拆开自driving，锚定 ≙ RW 帧序）：
  站稳判定/重力开关 → 输入反转检测 → 推进力/持久拉直 → terrainSqueeze 门控 →
  `Body.Tick`（受力→积分→约束→碰撞→卡角结构恢复→卡链释放）→ 头速顶死计数 + 局部卡角计数
  → 腿 → 支撑法线更新 → 持久拉直需求衰减。
- `CentipedeLocomotionController.Tick` 同样由宿主一次性调用：应用 `RequestedLeadEnd`/派生意图 →
  延伸表面轨迹 → 逐节目标与自避 → 逐节力/`Body.Tick` → 行波足端 → 支撑观察。
  宿主不得拆开或另排其中阶段。
- `SpiderLocomotionController.Tick` 内部序同样不可拆分：解析方向/路径点 →
  读取上 tick 抓地并更新重力开关 → 按抓地贡献施加归一化推进与线性链拖尾姿态力 → `Body.Tick` →
  顶死换步 → `SpiderLeg.Tick`（足端积分、可达环、碰撞、两段 IK）→ 汇总下一 tick
  `SupportNormal`。换序会让抓地、推进和膝姿态读取不同相位。
- `CicadaLocomotionController.Tick` 内部序（锚定 RW `base.Update → Act`）：
  先按上一轮模式配置身体并执行 `Body.Tick` → 读取本 tick 接触、停驻表面和 Charge 撞击
  → 更新 `Mode` / `FlightPower` / 输入目标 → 向前后 chunk 注入下一 tick 使用的差分飞行动力
  → 更新稳定 3D 姿态框架、四翼与触须。渲染不反向影响物理。
- `TentaclePlantController.Tick` 内部序：`Body.Tick` → `TentacleChain` 路径/物理 →
  感知与攻击标量 → 注入下一 tick 形态力 → `Root`/`Hand`/tip 耦合 → 抓持回收效果。
  宿主不得拆开此序，也不得把目标效果提前到同 tick 反向改写段链。
- `DeerLocomotionController.Tick` 内部序：直喂目标导出 → 解析休息资格并更新连续体高与
  各腿当前 reach → 持久 frame 与当前/前方地板预看 → 旧抓点受力前复验 → 从本 tick 合法抓点计算支撑并注入
  不均匀升力/推进 → COM balance、防折与躯干姿态 → `Body.Tick`（重力恒开）→
  四条 `DeerLeg` 段链 → 仅在本 tick 已确认支点物理失效时的同对紧急落脚 → 统一评分换步与
  同对互锁 → 单次发布下一 tick 支撑。宿主不得单独 tick 腿链，也不得在支撑发布和
  下一 tick 受力之间改写抓点；这样会把支撑低通推进两次或让失效抓点多承力一拍。
- `DaddyLongLegsLocomotionController.Tick` 内部序：`Body.Tick` 施完整重力并消费上一 tick 阻尼 →
  `UnconditionalSupport-.025` → body `K=4` residual → 三 landmark 材料 frame → 解析原始 MoveTarget/MoveDir 与卡住历史 →
  锁存/验证 detour，得到 locomotionMove → 固定索引逐条触手积分、逐段地形查询和贴面 →
  最终链约束后的 tentacle `K=4` residual（先回可行 LastPos，最终才 Anchor 回退）→ 物理半径裁边的
  相邻链边审计、拓扑阻断无张力断边和至多一个全验证原子后缀候选（guide obstruction 只清任务，
  不进入拓扑恢复）→ 汇总全部 Locomotion task 的贴面支撑 →
  移动期同 tick 对全团注入不钳 1 的 `1.2×` 抗重力；真实 episode 结束后钳至 `1g` 并衰减质量质心共同速度 →
  无力偶推进/共同脱困增量 → 职责分配与换步 →
  发布低通观测量与下一 tick 阻尼。
  到点与卡住检测仍看原始 carrot；宿主不得单独 tick 触手或先应用效果再反向改变本 tick 支撑。
- `VultureFlightController.Tick` 内部序（≙ RW `Vulture.Act`）：解析直喂目标与到达迟滞 →
  拍翅相位/振幅 → 逐翅模式决策（坠落自救 → 降落 → 起飞 → 逐翅自主）→ 身体阻尼/俯仰配平 →
  悬停或巡航推进（或栖息爬行）→ **逐翅升力注入** → 降落制动/起飞助推 → 头部伺服 →
  `Body.Tick` → 翅膀 tick → 支撑汇总 → 清除派生意图。升力必须留在 `Body.Tick` 之前，
  换序等于把脉冲挪到另一个相位上，周期均值与重力的平衡即失效。

### 3.2 渲染插值

渲染帧读 `chunk.LerpPos(t)` / `limb.LerpPos(t)`；拟态草另读 `Root`、`Hand` 和
`Segments`，鹿另读每条 `DeerLeg.Segments[i].LerpPos(t)`，Daddy 另读每条
`DaddyTentacle.Segments[i].LerpPos(t)`。`t` = 物理插值分数 ∈ [0,1)。
Godot 宿主：`t = (float)Engine.GetPhysicsInterpolationFraction()`（沙盒即此）。
渲染永远比物理「晚」不到一个 tick；**逻辑一律不读渲染帧率**。

### 3.3 宿主时基 ≠ 40Hz 时（主项目是 60Hz 物理）

主项目无固定步长设施（调研 §8.1）；内核本身不持有时基（`Tick` 一次 = 一步），
**由宿主在自己的 tick 入口装一个 0.025s 累加器**驱动：

```csharp
_acc += delta;                       // 宿主物理帧（60Hz，delta≈16.7ms）
while (_acc >= 0.025) {
    _acc -= 0.025;
    KernelTick();                    // 上面 §3.1 的一个 tick
}
float t = (float)(_acc / 0.025);     // 渲染插值分数（60Hz 下每帧 0~1 个内核 tick，插值抹平）
```

确定性注意：累加器起点与 delta 序列决定 tick 时刻，**回归探针必须直接按 tick 驱动**
（不经变步长 delta），与本仓库 `--determinism` 模式同构。

### 3.4 出生 / 传送 / 冲量

- 出生 = `CreateLizardController(origin, p)`（沙盒热切换品种即整体替换）。
- **rebase = `LizardLocomotionController.Shift(delta)`**：整体平移，速度/抓握/站稳状态**原样保留**——
  只适用于「地形随你一起平移」的场景（浮点原点重置、整个世界搬家）。不要手写逐字段
  平移：漏掉 `LastPos` 会让运动扫掠射线扫过整张地图，漏掉 `HuntPos` 会让脚飞回旧落点，
  漏掉世界系 `MoveTarget` 会让身体转头追旧原点。`LastMoveTarget` 观测量也同步平移
  （smoke 的 [CORE-SHIFT] 逐字段精确断言钉死了这份完备性）。
- **瞬移/复位/换房 = `LizardLocomotionController.Teleport(delta)`**：Shift + 全腿强制松手 + 站稳清零——
  地形不动时旧抓握点在新位置是空气，保留它们会悬空关重力永久漂浮；旧 `MoveTarget`
  也不再满足「邻近可达点」契约，因此 Teleport 会清 null，宿主须按新位置重喂。
  落地后步态自动重建。
- **跳跃/击飞 = `LizardLocomotionController.Launch(velPerTick)`**：全 chunk 加同一速度增量，全腿强制松手、
  站稳计数清零——重力当 tick 回归，身体进入弹道，落地后 plant-and-trail 自动恢复
  （smoke 的 [CORE-LAUNCH] 断言覆盖；沙盒 `--yank` 是它的场景版）。Teleport/Launch 都会
  清 `StraightenOutNeeded`/`SpineCornerStuckTicks`/`MaxSpineCornerStuckTicks`/转身辅助（含手性历史），
  把 `TerrainSqueeze` 恢复 1，并开始新的恢复诊断生命周期；
  Shift 则全部保留。若宿主以后做可回滚完整快照，这些动态恢复状态必须与 Pos/Vel 一起序列化。
- 想「删掉重来」（长途瞬移后不在乎连续性）：整体重建仍然最简单。

蜈蚣暴露同名 `Shift`/`Teleport`/`Launch`：`Shift` 连同 `SurfaceTrail`、逐节目标与脚的
当前/预定抓点整体平移；`Teleport` 另外作废轨迹、抓握、支撑和 `MoveTarget`；`Launch`
统一叠加全身体速度并交还重力。两类控制器可以共用宿主生命周期事件，但状态不可互换。

鹿同样暴露 `Shift`/`Teleport`/`Launch`：`Shift` 平移 body、所有腿段、当前/候选抓点、
地板缓存、插值历史和 `MoveTarget`，保留速度、冷却、支撑与步态相位；`Teleport` 另外让四腿
全松、清支撑/地板/直喂目标记忆，并从深休息原子唤醒到完整 reach 与活动站高；`Launch` 给 body
和腿段统一冲量、保留 `MoveTarget` 和连续 `CurrentRideHeight`，但同样清休息/犹豫、恢复完整
reach，并立即作废 `AtMoveTarget` 等下一 tick 重算。三者都不切换重力——Deer 的
`GravityScale` 全程仍为 1。

Daddy 同样暴露 `Shift`/`Teleport`/`Launch`：`Shift` 平移完整图身体、全部触手段、
地形落点、MoveTarget、外部目标快照、卡住历史与拓扑恢复参考点，保留恢复 phase、职责和连续
支撑；`Teleport` 保留出生形态但清旧落点、外部任务、MoveTarget、支撑、卡住历史与恢复暂态，
恢复最低 locomotion 预算并把
`UnconditionalSupport` 置 1；旧外部任务在下一 tick 发布一次 Released。`Launch` 给所有身体球和
触手段同速冲量、清地形落点/恢复暂态并把 `UnconditionalSupport` 清 0，但保留 MoveTarget
与外部目标输入供落地后续接。不能只 Shift 身体球而漏掉独立段链。

拟态草不用移动生物的 `Teleport/Launch`：`Shift` 只用于世界 rebase 并保留攻击/抓取连续性；
地形不随体移动的换洞必须调用 `Remount(newMount)`，它会清目标、抓取、攻击和旧导引；
`ReleaseHeldTarget()` 立即释放内核目标，并在下一次 `Tick` 发出 `Released`；之后按
`Target` 是否仍存在进入 Tracking 或 Wandering。固定生物没有合法的击飞语义，
不要为了接口整齐向根或段链硬塞 `Launch`。

## 4. 输入契约（移动后端的方向/速度/路径点；固定后端的目标快照）

| 输入 | 类型/域 | 语义 |
|------|---------|------|
| `LizardLocomotionController.MoveDir` | 世界系单位向量或零 | 移动**意图**方向。给 XZ 平面意图即可：撞墙/上坡时被支撑系自动重定向为沿面上爬（走/爬无模式键）。零 = 无意图（触发闲置姿态计时，并切断 180° 转身的旧方向历史）。`MoveTarget` 非 null 时由内核在该 tick **临时导出覆盖**，tick 末清零，不冒充下一 tick 的宿主方向。 |
| `LizardLocomotionController.RunSpeed` | [0,1] | 意图强度（≙ AI.runSpeed）。**统一死区 `MoveIntentDeadzone = 0.1`**：≤0.1 时推进/步态/顶死检测/闲置退出全部视为零输入——不存在「推着走但腿不迈」的半激活带。有效输入域实为 {0} ∪ (0.1, 1]。两种驱动模式共用（油门始终归宿主）。 |
| `LizardLocomotionController.MoveTarget` | `Vector3?`，默认 null | **可选第三旋钮：路径点直喂**（≙ RW 寻路器给 FollowConnection 的下一路径格中心——RW 的原始形态）。非 null 时进入直喂模式：MoveDir 由内核导出（头→点方向，撞墙仍走重定向涌现）；到点基准是喂点沿 `SupportNormal` 抬 `RideHeight`，实际推进胡萝卜**跳过射线构造**，意图顶住支撑面时会沿面旋转，因此重定向窗口里不一定与到点基准重合。External 模式始终没有方向驱动在棱边/悬崖处的 Fallback 空中退化分支。**契约**：喂点必须是**邻近的可达路径点**（导航网格/路径采样贴地；与头之间无墙阻隔——隔墙远点 RW 同样走不过去）；喂点与换点节奏归宿主（≙ 寻路器逐格递进），`AtMoveTarget` 到达即换下一点或清 null（到点即视为无意图，停下/闲置涌现）。`Shift` 随世界平移它；`Teleport` 作废并清 null。 |
| `LizardLocomotionController.MoveTargetArriveRadius` | 米，默认 0.4 | 直喂模式到点判定半径（头到到点基准「喂点+法线抬升」的 3D 距离）。按寻路格距/路径点密度调。 |
| `LizardLocomotionController.Shift(delta)` | 方法 | rebase：地形随体平移时用（§3.4）。 |
| `LizardLocomotionController.Teleport(delta)` | 方法 | 瞬移：地形不动时用（Shift + 松手 + 站稳清零 + 清 `MoveTarget`，§3.4）。 |
| `LizardLocomotionController.Launch(velPerTick)` | 方法 | 跳跃/击飞冲量（§3.4）。 |

- 每 tick 写入（不写则保持上次值——AI 决策频率可以低于 tick 频率）。
- **两种驱动模式怎么选**：宿主有寻路器/贴地路径点 → 用 `MoveTarget` 直喂（到点基准贴地、
  不走 Fallback，≙ RW 原始形态）；玩家直控/只有方向没有点（受击失衡、简单游荡）→ 用
  `MoveDir`（内核自己构造胡萝卜，棱边处可能短暂退化为空中目标）。可逐 tick 切换：
  只清 `MoveTarget` 即停；要由方向驱动立即接管，则同 tick 清 null 并写新 `MoveDir`。
  `HasMoveIntent` / `AtMoveTarget` / `LastMoveTargetKind` 是配套观测（§5.2）。
- **不要**直接写内核其它状态（`SupportNormal`/`GripCounter`/…都是内核私有语义）；
  唯一例外是外力注入 `chunk.Vel`（§3.1 相位）。
- 宿主侧「Grounded=false」（走出平台、被剥夺支撑）：放空输入即可，重力开关自己会
  因抓空而恢复坠落、落地自动回归步态（`--yank` 回归即此场景）。
  **主动位移事件（跳跃/击飞/弹射）必须走 `Launch`**——只放空输入不会给内核向上的
  冲量，视觉身体会继续抓在原地看着权威根飞走。

蜈蚣使用同一三项移动输入：`MoveDir`、`RunSpeed`、可选 `MoveTarget`；到点半径字段名为
`ArriveRadius`。此外宿主在选择或更换领航端时用 `RequestedLeadEnd` 显式请求
`CentipedeLeadEnd.Start` 或 `.End`；请求保持到下次变更，并在下一次 `Tick` 的确定性边界
生效，`LeadEnd` 是控制器已经应用的可读状态。`MoveDir`/`MoveTarget` 不会自动推断或切换
头尾。若需要按方向、目标或战术自动选端及去抖，由宿主/AI 完成后再写请求。
当 `MoveDir` 与当前表面法线近乎平行时，控制器会把该端此前保存的有向切线运输到新表面；
宿主无须在平台外角临时把输入改成 `Down`，该续行规则也不会触发换端。
`MoveTarget` 仍必须是宿主提供的**邻近可达点**，不是把远处最终目的地直接交给内核。
控制器不做 AI 寻路；宿主读取 `AtMoveTarget` 后负责换下一点。

沙盒交互与 `--lead=start|end` 都显式锁定 `RequestedLeadEnd`，不会自动换端。只有未传
`--lead` 的无头 default 巡逻脚本演示一种宿主层策略：按路线方向评分两端，连续 3 tick
确认后写请求。该策略不进入 `CentipedeLocomotionController`，核心既不运行也不观察其评分/
去抖状态；回迁时可直接替换为目标项目 AI 的决策。

### 4.1 宿主 tether 配方（姿态 1 的闭环，≙ §8.3）

权威根（`CharacterBody3D`）与内核身体之间每宿主 tick 三档处置，全部用 §4 的入口：

| 根的行为 | 判据（建议值） | 内核动作 |
|---|---|---|
| 常规移动 | 偏离 ≤ `MaxTether`（建议 1.5m） | `MoveDir = 朝 (rootPos + rootVel·k)`，`RunSpeed` 按距离饱和——身体像追路径点一样拖行 |
| 被甩开（追不上/卡地形） | 偏离 > `MaxTether` | `Teleport(超出量)`——硬拽回签约距离内（用 Teleport 不用 Shift：旧抓握点已无意义） |
| 瞬移/复位/换房 | 单 tick 根位移 > 传送阈（建议 2m） | `Teleport(rootDelta)` 全量 |
| 跳跃/击飞 | 事件驱动 | `Launch(rootImpulse × dt/tick 换算)` 后照常给意图 |

`MaxTether` 是**宿主侧参数**（内核不知道根的存在）；把它连同阈值写进主项目的
snapshot→内核映射层。两个**接线时必须调的已知张力**（终审 C9）：

- **爬墙涌现 vs 贴地导航根**：根绕墙走时，内核的意图重定向可能让身体就地爬墙——
  与根的分歧会被第二档 `Teleport` 拽回，但表现是「爬两步被拽走」。若品种不该爬墙，
  宿主侧把 `MoveDir` 压平（去掉指向墙内的分量）即可关掉这条涌现；要爬墙怪就接受分歧。
- **纠偏落点的地形安全**：`Teleport` 的落点应基于根的位置（`CharacterBody3D` 保证
  非嵌入），不要落在内核身体的几何附近乱推——嵌入虽会被 S4 MTD 解出，但可能与
  根方向形成来回拉锯。落点取「根位置 + 品种站高」最稳。

### 4.1b 秃鹫（VultureFlightController）的输入面差异

三旋钮同名同义，差异全部来自「会飞」：

- `MoveDir` 是**完整 3D** 意图（含竖直分量，不像人形被压平）；`MoveTarget` 同样是 3D 路径点，
  到点半径字段名为 `MoveTargetArriveRadius`（默认 0.6m）。共用同一 `MoveIntentDeadzone=0.1`。
- `AtMoveTarget` **带迟滞**（进 1× / 出 2× 半径）：悬停时升力谷值的下沉会把身体带出到点半径，
  无迟滞会在「悬停↔爬回」间逐 tick 抖动。迟滞态**绑定具体目标**——宿主换点即复位，否则新点
  落在旧点的 2× 出圈半径内会被误报到达（评审确认缺陷，smoke `[CORE-VULTURE-CONTRACT]` 钉死）。
- 悬停锚（`HoverAnchor`）只在**真到点**时取喂点：零油门 + 远处残留目标不得绕过油门自动巡航。
- `Shift`/`Teleport`/`Launch` 与蜥蜴同名同义（`Shift` 保留飞行/抓附状态并平移全部世界坐标记忆）。
- 观测面：`AirBorne`/`AnyWingAttached`（是否栖息/挂住）、`SupportValue`、`WingFlap`/
  `WingFlapAmplitude`（拍翅相位与展开度，渲染用）、`HoverAnchor`、`LandingBrakeTicks`/
  `TakeoffBoostTicks`/`ModeSwitchLockTicks`、`LastMoveIntent`，以及逐翅
  `VultureWing.Mode`/`Segments[i].LerpPos(t)`/`GripPos`/`GripNormal`/`Attached`/`Gripping`。
  全部只读；翅膀段是渲染与抓附的唯一真相，不要另建一套骨架去反推。

### 4.1c 鹿（DeerLocomotionController）的输入与观测差异

- 三旋钮仍是 `MoveDir` / `RunSpeed` / nullable `MoveTarget`，到点半径字段名为
  `MoveTargetArriveRadius`。方向会投影到当前可站立支撑面；墙面达不到坡角门时只作为阻挡，
  不会像 Lizard 那样成为抓脚面。`MoveTarget` 必须是宿主射线/导航投影得到的
  **邻近可达表面点**，不是已抬到鹿身中心高度的点；`CurrentRideHeight` 是沿重力反方向
  定义的垂直高度，内核会沿 `WorldUp` 抬高后再做 3D 到达判定，最后才把运动投影到支撑切面。
- 鹿没有 `ApplyGravity` 状态：`Body.GravityScale` 恒为 1。宿主判断站姿应读
  `PlantedLegCount`、`RawSupport` / `TotalSupport`、`SupportNormal` 和 `CurrentRideHeight`，
  不能把“抓到任意脚”等同于关闭重力。
- 调试/AI 可读 `Hesitation`、`DriveScale`、`RestAmount`、`IdleTicks`、`CurrentLegReachScale`、
  `HasCurrentFloor` / `HasAheadFloor`、
  `BodyDragImpulse`、`MaxPairAirborneRun`；后者是含 Launch 弹道的实例历史高水位，当前连续值应读
  `PairAirborneTicks`。3D 倾覆边界读质量加权 `BalanceOffset`、真实足端凸包
  `SupportMargin` / `SupportHalfWidth` / `SupportHalfLength` 和 `LeanDegrees`。这些都是只读连续量，
  不构成休息、行进或跌倒模式枚举。
- 每条 `DeerLeg` 公开段链、当前/候选抓点、抓地与冷却、支撑贡献、换步/落地序号、可及比、
  `BendPole` 和约束误差，供渲染与诊断使用；`MaxConstraintError` 在最终 MTD 后测量，固定端点、
  缓变形态长度和无穿透优先均可能贡献，不是纯 solver residual。宿主不得写这些状态来伪造支撑。

### 4.1d 长腿爸爸（DaddyLongLegsLocomotionController）的输入与观测差异

- 三旋钮仍是 `MoveDir` / `RunSpeed` / nullable `MoveTarget`，到点半径字段名为
  `MoveTargetArriveRadius`。`MoveDir` 是完整 3D 世界方向，但只表达运动意图：身体没有 head、hips、
  forward 或 world-up 姿态，控制器也不会为它补一根隐式前向轴。`MoveTarget` 以质量加权
  `BodyCenter` 到宿主直喂点的 3D 距离判定；仍只接受邻近可达点，到点后宿主负责换点或清空。
- 连续支撑观测为 `RawSupport`（各 Locomotion task 触手 `sqrt(贴面比例)` 并对到达落点加权后，
  按全部触手数归一化）、`UnconditionalSupport`（Create/Teleport 后从 1 每 tick 减 `.025`）、
  `ContinuousSupport=max(RawSupport^.21,UnconditionalSupport)`（原作是 `.30`；项目为连续 3D
  稀疏整链接触有意改为 `.21`）、`EffectiveSupport`（前者的低通
  观测值）、`GravityCancellation`（出生/移动期为 `ContinuousSupport*1.2`；至少一个 movement
  episode 真结束后静止时至多为 `1g`）、
  `DirectionalSupport`、`DriveScale` 与 `SupportNormal`。`SupportNormal` 通常汇总实际贴面段法线；
  若某条触手只有经复验的 arrival reward、当前没有实际接触，则使用其已验证 `LandingNormal`。
  全部都无贡献时保留上次有效值。它只代表支撑法线合成，不是生物的“上”，宿主不得据此改写
  材料 frame。
- 职责与恢复观测为 `LocomotionTentacleCount`、`ArrivedTentacleCount`、`StuckCounter` /
  `StuckAmount`、`StuckDetourActive` / `StuckDetourDirection` / `StuckEpisodeSerial`、职责/释放/换步
  serial，以及逐触手落点、reach/validation/residual 失效与 LastPos 恢复 serial、
  `TickQueryCount` / `PeakQueryCount` /
  `QueryBudgetExceeded`。地形拓扑诊断另读逐触手 `BacktrackFrom`、`TerrainRecoveryActive` / phase、
  topology/guide obstruction 连续计数与恢复/release serial。逐触手再读正交的 `Task` /
  `NeededForLocomotion`；`Role` 只是兼容 HUD
  的派生色，再读 `GripFraction`、`SupportContribution`、
  `AtGrabDestination`、`SearchFailureTicks` 和段接触；不存在“任一尖端抓住 = 整体抓稳”的布尔捷径。
  detour 侧向权重为 `.75`，attempt 最短/最长 `80/400 tick`；超时且仍 stuck 时，连续 attempt 的
  锁存侧向必须成对精确反向，smoke `[DADDY-CORE-STUCK-RETRY]` 直接断言这条重试契约。
- `StunTentacle(index,ticks)` 按编号打断单条触手：它立即退出职责、清空支撑与目标并软化；其余触手
  由同一个预算分配器接管。宿主应从 `FindIdleTentacle()` 取得真正空闲编号，再以
  `TryAssignExternalTarget(index,snapshot)` 指派非运动够取；同一稳定 ID 的快照可逐 tick 更新，
  `ClearExternalTarget(index)` 取消；无当前外部任务时取消是幂等 no-op，不影响运动支撑或落点。
  等待下一 tick 发出旧目标 `Released` 的触手不会被
  `FindIdleTentacle()` 返回。`ExternalReach` 与 `Stunned` 都清旧地形接触记忆、不吃 adhesion，且不计
  运动支撑；free-duty Locomotion 虽显示为 Idle，仍会搜索、贴面、支撑并参与换步。
- `TargetEffects[index]` 是每 tick 覆盖的纯值输出：`Reached` / `Held` / `Released` 加建议的
  `PositionCorrection` / `VelocityDelta`。目标实体、伤害、吞入和是否接受拉扯始终归宿主权威；
  `Reached` 与 `Held` 都是当前 tick 尖端在到达半径内的电平值（不锁存），`Released` 才是一次性
  边沿；`PullTowardBody=false` 时只报告够到，不请求拉向身体。快照的 `VelocityPerTick` 供宿主状态链和
  确定性/rebase 使用；当前核心不额外预测位置，宿主应先更新 `Position` 再回喂。
- 真实相邻链边一旦被地形阻断，该条触手从同 tick 起不得再输出 ExternalReach 的
  `Reached/Held` 或拉扯；若持续阻断使任务失效，核心为旧 StableId 排队且只发布一次
  `Released`。guide obstruction 也可使任务失效，但只触发重搜/释放，不设置 `BacktrackFrom`、
  不拆边和不重建后缀。
- `Shift` / `Teleport` / `Launch` 与其它移动后端同名同义；三者都覆盖全部身体球与全部触手段。
  `Shift` 平移阻断点并保留正在尝试的恢复 phase；`Teleport` 清旧地形/外部目标/路径点、单条 stun
  与恢复历史并恢复出生支撑，`Launch` 清恢复暂态并释放地形、
  清出生支撑但保留外部目标快照和 `MoveTarget`，之后靠连续支撑与职责预算自然恢复；专项 lifecycle
  smoke 会先制造单条 stun 和外部目标，再直接断言 `Teleport` 已清 stun 且下一 tick 发布 Released。

### 4.1e 掉落虫（DropBugLocomotionController）的输入与观测差异

三旋钮同名同义（`MoveDir` / `RunSpeed` / 可选 `MoveTarget` + `AtMoveTarget`），新增的都是
「伏击者」专属：

- `CarriedMass`（float）：宿主叼着的猎物质量，连续影响推进与站稳；不是布尔状态。
- `AttackTarget`（`DropBugAttackTarget?` = 点 + 每 tick 速度）：可**固定或逐 tick 随动**。
  俯冲与扑击都读它；它只是纯值快照，真实实体、伤害与命中判定归 gameplay 权威。
- `TryAssignHangAnchor(in TerrainHit, ITerrainQuery)` / `ClearHangAnchor()`：宿主显式给悬挂
  锚。返回 false 时 `LastHangRejection`（`DropBugHangRejection`）说明是七个判据中的哪一条挡
  下的——**不要重试同一个锚**。
- `ReleaseHangDive()`：脱悬俯冲。`TryStartPounce()` / `CancelPounce()`：地面蓄力扑击。
  `PounceReach(direction)` 是**只读查询**，宿主可在起跳前问「这个方向够不够得着」——侧对方向
  的可及距离显著短于正对。
- `Shift` / `Teleport` / `Launch` 与其它移动后端同名同义。**`Launch` 额外设 40 tick 悬挂
  重贴附冷却**（见 §2.9）；悬挂中 `Teleport` 不弹飞。
- 观测面：`Footing` / `FootingCounter`、`HangFactor` / `Hanging` / `HangState` / `HangAnchor` /
  `HangRegrabDelay`、`PounceCharge` / `ChargingPounce`、`Jumping` / `Diving` /
  `AttackCooldown` / `LastDiveLandingTick`、`MovingBackwards` / `Sitting`、`StuckSignal` /
  `StuckShake`、`RunCycle`（步频）、`TravelDir` / `Forward` / `Up` / `Right`，以及
  `Legs`（**纯表现**，不要拿它反推支撑）。另有 `PounceLeapSerial` / `PounceAbandonSerial` /
  `DiveSerial` / `HopSerial` 四个只读事件流水号，供宿主检测「事件确实发生过」。

### 4.2 人形（HumanoidLocomotionController）的输入面差异

三旋钮语义与蜥蜴一致（`MoveDir`/`RunSpeed`/可选 `MoveTarget`，同一 `MoveIntentDeadzone`），
差异与新增（≙ 反编译 Scavenger）：

- **直喂到点判定是水平距离**（地面生物有站高，3D 距离会把「站在点正上方」误判成不到）；
  `MoveDir` 的竖直分量被压平丢弃——人形不爬墙，撞墙只沿墙水平滑移，正对墙推 = 停下。
- `Conscious`（bool，默认 true）：**眩晕/死亡的唯一开关**（≙ RW `!Consious ⇒ Act() 不跑`）。
  false ⇒ 站立力偶/伺服/推进/手臂链全停 → 瘫倒涌现；true ⇒ 力偶回归自动爬起。
  没有 knockdown/getup 状态机。注意：完全对称的直立姿态是确定性的不稳定平衡点——
  宿主要「击晕倒地」需配一次轻推（`Launch` 小冲量），零随机内核没有噪声帮它倒。
- `PointTarget`（`Vector3?`）：指向手势（主手伸向世界点，sin 伸缩）。世界坐标语义：
  `Shift` 平移、`Teleport` 作废清 null。
- `Carrying`（bool）：主手切躯干系携带位；被持物本体归宿主——读 `MainHandPos`/`MainHandDir`
  硬钉即可（≙ RW GraphicsModuleUpdated 对 grasp 0 的钉定，宿主侧一行）。
- `StartThrowCharge(dir)` / `ReleaseThrow()`：蓄力期强制停驶（≙ RW 瞄准时 moving=false）+
  主手拉到身后蓄力位抖动；出手返回被投物初速（米/tick），投掷物本体归宿主（从
  `MainHandPos` 起飞）。未蓄力/重复释放返回 null；昏迷自动弃蓄力（≙ RW Stun 掉武器）。
- 重力开关的判据是「清醒且近地」而非「腿抓稳」（人形的腿不承重）；`Launch`/坠远
  自动回归重力，落地站稳（`GroundedCounter ≥ GroundedTicks`）后恢复失重伺服态。

### 4.3 拟态草的目标/效果接缝

拟态草不接受 `MoveDir`、`RunSpeed` 或 `MoveTarget`。AI/宿主每 tick 选择至多一个猎物，
把其稳定 ID、位置、速度、半径和有效/可见信息复制进 nullable
`TentaclePlantController.Target`。该快照是本 tick 唯一动态目标输入；核心不枚举场景对象，
也不经 `ITerrainQuery` 查询生物。

`Tick` 返回 `void`，并覆盖只读 `TargetEffect`：

| 输出 | 宿主语义 |
|---|---|
| `TargetId` | 本 tick 效果所属的稳定目标 ID |
| `CaptureStarted` | 本 tick 手端首次几何命中；宿主建立抓取关系 |
| `Held` | 内核仍保持 `HeldTargetId` 对应目标 |
| `PositionCorrection` / `VelocityDelta` | 由 gameplay 权威目标应用的建议位置/速度修正 |
| `Released` | 解除抓取；宿主同 tick 清关系 |
| `ConsumeRequested` | 已拉至根部或强拉宽限到期；宿主决定吞入、销毁或其它玩法结果 |

宿主拒绝抓取、目标逃脱/死亡或 ID 变更时，应在下一 tick 的 `Target` 如实反映，并在需要时调用
`ReleaseHeldTarget()`；不得通过回写 `Phase`、`HeldTargetId` 或段位置伪造结果。

## 5. 输出契约（渲染与 AI 的观测面）

### 5.1 渲染读什么（沙盒 `BodyRenderer` 是参考实现）

| 观测 | 用途（沙盒配色） |
|------|------|
| `Body.Chunks[i].LerpPos(t)` / `.Radius` | 身体球体 |
| `Body.Connections`（跳过 `SoftOnly`） | 骨架连线（防折叠支柱是姿态弹簧，不是骨头，不画） |
| `LizardLocomotionController.ApplyGravity` | 身体色：true 红=坠落 / false 青=抓稳（站/爬涌现态的唯一可视开关） |
| `Limb.LerpPos(t)` / `.Radius` / `.Anchor` | 脚球 + 腿线 |
| `Limb.Gripping` / `.ReachingForTerrain` / `.IdlePose` | 脚色：绿=抓稳推进 / 橙=迈步找落点 / 灰蓝=摆动或闲置 |
| `SpiderLocomotionController.ApplyGravity` / `.SupportNormal` | 蜘蛛抓稳状态与完整表面朝向 |
| `SpiderLeg.LerpRoot(t)` / `.LerpKnee(t)` / `.LerpPos(t)` | 蜘蛛两段腿：根→膝→足端 |
| `SpiderLeg.Gripping` / `.ReachingForTerrain` / `.GripNormal` | 蜘蛛真实抓点状态；膝点不作为物理接触 |
| `TentaclePlantController.Root` / `.Hand` / `.Segments[i]` 的插值位置 | 拟态草根、根粗梢细段链和末端手；`GuidePoints` 作为 0–2 个折点补在根与当前目标之间，`BacktrackFrom` 索引 `Segments` |
| `TentaclePlantController.Phase` / `.CanGrab` | 拟态草按 Wandering/Tracking/Windup/Striking/Recovering/Holding 配色与攻击窗提示 |
| `VultureWing.Segments[i].LerpPos(t)` / `.Mode` / `.Attached` | 秃鹫翅膀段链（渲染与抓附的唯一真相）；其余观测量见 §4.1b |
| `DeerLocomotionController.Head` / `.Trunk[i]` / `.Antler` | 鹿的头、粗重叠躯干链和大轻鹿角物理代理；鹿角目标在头上方略向前，渲染层不可把它当躯干球；半径直接取 chunk 数据 |
| `DeerLeg.Segments[i].LerpPos(t)` / `.BendPole` | 鹿多节腿的正式几何；从锚点依次连每段，不用单粒子腿或渲染层 IK 代替 |
| `DeerLeg.AttachedAtTip` / `.Gripping` / `.CandidatePoint` / `.SupportContribution` | 鹿足端踩住、确认、候选与连续支撑调试状态；沙盒据此区分落脚和摆动 |
| `DaddyLongLegsLocomotionController.Body.Chunks` / `.Body.Connections` | 长腿爸爸可变数量的重叠身体球与完整图静息距离；全部连接都是出生体型，不要从任意两球推导前向 |
| `DaddyTentacle.Segments[i].LerpPos(t)` / `.TerrainContact` / `.ContactNormal` | 每条独立触手的正式几何与逐段贴面状态；支撑来自整链比例，不另画只承重的“脚尖” |
| `DaddyTentacle.Task` / `.NeededForLocomotion` / `.Role` / `.LandingPoint` / `.IdealLandingPoint` / `.GripFraction` | 真实任务、运动预算标记、派生职责配色、当前/理想落点与连续贴附比例；free-duty 也显示理想落点 |
| `RawSupport` / `UnconditionalSupport` / `ContinuousSupport` / `EffectiveSupport` / `GravityCancellation` / `LocomotionTentacleCount` | 长腿爸爸瞬时贴面、出生支撑、直接物理支撑、低通观测、移动期 1.2× / 停驶后至多 1g 回补与职责预算调试量；这些连续量取代抓稳/坠落二态着色 |

### 5.2 AI / 游戏逻辑可读

两个控制器共同提供 `LegsGripping`（抓地腿数）、`ApplyGravity`（是否坠落态）、`SupportNormal`
（支撑面法线：平地≈上、爬墙≈墙法线——可判「正在爬墙」）、`StallTicks`（顶死程度）、
`AtMoveTarget`、`LastMoveTarget` / `LastMoveTargetKind`。蜘蛛另可逐腿读取
`SpiderLeg.GripNormal` / `HasGrip` / `BendPole` / `KneePos`；弯腿姿态是正式只读输出。
鹿的同名移动观测外，另按 §4.1c 读取连续支撑、高度、犹豫、足端凸包 balance 与逐腿段链状态；
鹿没有 `ApplyGravity`，也不把 `DeerLeg` 强转成 `Limb`。
长腿爸爸按 §4.1d 读取连续支撑、方向侧支撑、职责与卡住恢复量；它同样没有 `ApplyGravity`，
也没有可供 AI 读取或写入的 forward。外部够取结果只认 `TargetEffects`，不得从段链距离自行制造
命中、伤害或吞入事件。
蜥蜴专属的 `StraightenOutNeeded` / `SpineCornerStuckTicks` /
`MaxSpineCornerStuckTicks`（姿态恢复需求、局部髋部卡角与本次恢复生命周期峰值；
Teleport/Launch 开新生命周期；诊断/AI 可读，不得由宿主写）、
`Limb.GripNormal`/`HasGrip`、`BodyChunk.TerrainContact`/`ContactNormal`、
`BodyChunk.Rotation`（chunk 朝向 =「参照 chunk → 自己」单位向量，≙ RW BodyChunk.Rotation。
工厂钉定不变量：头参照髋 → Rotation = 全身轴前向；中段参照后一节 → 本段轴前向；髋参照头 →
指向后方，消费侧翻转；尾链互绑自然指向（段 → 后一段 = 朝身体，尾尖 → 前一段）。退化：
无参照 → Up、两点近重合（模长 ≤1e-5，Unity kEpsilon）→ **零向量**（照抄 RW/Unity
normalized 语义），消费端自行回退。
渲染面朝向 / 附着物局部系记忆（RW 矛/獠牙钉在身上随身转的原语）/ AI 观测都从这里读——
插值版用 `LastPos` 自行 Lerp 后重算）。注意它在 3D 中只是 **forward 方向向量**，不是完整
旋转或局部坐标系：渲染/附着物消费端必须再结合稳定的 up（通常取 `SupportNormal`，必要时沿用
上一帧 up）构造正交 `Basis`/Quaternion；forward 与 up 近共线时必须显式选备用 up，避免 roll
突跳。该补充是 3D 扩展约束——RW 的 2D 单方向向量本身即可唯一确定平面旋转。
全部是**只读观测**；写它们后果自负。

### 5.3 蜈蚣观察面

蜈蚣渲染同样读取 `Body.Chunks`、非 `SoftOnly` 连接；足端用 `CentipedeLeg.LastPos`
到 `Pos` 自行插值。
宿主/调试可另外读取 `Segments`、`Legs`、`SurfaceTrail`、`LeadEnd`、`LeadChunk`、
`SupportedSegmentCount`、`SupportRatio`、`AtMoveTarget`；每个 `CentipedeSegment`
公开 `SupportPoint`、`SupportNormal`、`Forward`、`Side`、`TargetCenter`、`ColliderId`、
`SupportConfidence`/`Supported`。`SurfaceTrail` 采样公开 `Point`、`Normal`、
`ColliderId`、`ArcLength`。这些同样是只读观测，不是宿主施力入口；领航端的写入口是
`RequestedLeadEnd`，不要回写 `LeadEnd`。

### 5.4 人形的观测面（沙盒 `HumanoidRenderer`/`HumanoidSandboxDriver` 是参考实现）

渲染：chunk/腿沿用 §5.1 语义（人形身体色三档：青=清醒近地失重态 / 红=坠落 / 灰紫=昏迷）；
`Arm.LerpPos(t)`/`.Radius`/`.Shoulder` 画手球与臂线，`Arm.Mode`+`GrabPos` 配色
（黄=撑地锁点 / 紫=指向、蓄力、出手 / 绿=持物、休息位 / 灰蓝=垂摆）；`KnucklePos`
（撑点参考的可视化）；`Carrying` 时被持物硬钉 `MainHandPos`。
AI / 游戏逻辑：`Uprightness`（躯干轴·up ∈[-1,1]，摔倒检测/爬起进度）、`Grounded`/
`GroundedCounter`/`ApplyGravity`、`Facing`（水平朝向——人形的 `BodyChunk.Rotation` 是躯干
**上轴**不是前向，前向读这里）、`KnucklePos`/`KnucklePlants`、`LegsGripping`、
`ThrowChargeTicks`/`ThrowAnimTicks`/`ThrowDir`、`MainHandPos`/`MainHandDir`（持物/武器
钉定与朝向）、`Arm.ReachedSnapPosition`（≙ RW 握拳/张开贴图切换的判定位）。

### 5.5 拟态草观察面

渲染读取 `Body`、`Root`、`Hand`、`Segments` 及其插值位置；段半径由根到梢递减，
手端是正式物理/抓取输出，不由 renderer 另算。mount 的 `Outward/Tangent/Bitangent`
提供稳定三维朝向，避免侧墙/天花板安装时 roll 翻转。

AI、gameplay 与诊断可读 `Phase`、`AttackCharge`、`CanGrab`、`Extension`、
`AttackSerial`、`HeldTargetId`、`WanderGoal`、`GuidePoints`、`BacktrackFrom` 和地形查询计数。
`TargetEffect` 是唯一 gameplay 目标效果出口（§4.3）。除 nullable `Target` 输入外，
上述状态均只读；沙盒调试线不能反向驱动核心。

## 6. ITerrainQuery 契约（唯一接缝的全部语义）

```csharp
public interface ITerrainQuery
{
    bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit);
    bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth);
}
public readonly struct TerrainHit { Vector3 Point; Vector3 Normal; ulong ColliderId; }
```

实现方必须满足：

1. **线段射线**：from→to 有限段，命中返回最近交点与表面法线。
2. **HitFromInside 必须开启**：起点已陷入 collider 时返回命中且 **`Normal = 零向量`**
   ——内核所有调用点都特判零法线（直接归一化会 NaN）。纯实现参考 `PlaneTerrainQuery`。
3. **SpherePenetration = 球体重叠的最小平移向量（MTD）**：交叠时给出「沿 `pushDir`
   平移 `depth` 即完全脱离」；**深度嵌入（球心已在 collider 内）也必须给出有效方向**
   ——这是嵌入恢复的唯一通道（射线的零法线给不出方向；曾导致出生嵌入永久冻结）。
   恰好相切（depth=0）算未交叠，静息接触不抖。Godot 实现走
   `PhysicsDirectSpaceState3D.GetRestInfo`（`core/godot/RaycastTerrainQuery.cs` 参考，
   含法线缺失时「接触点→球心」的确定性兜底），Jolt 4.7 实证可用。
   为什么必须有它：三根「球心射线」对与运动平行的墙面擦边侵入全盲（实测穿墙 5cm
   无接触）——球形碰撞的承诺由这条原语兜底，砍掉它 = 回到评审 P1-2/P1-3 的坑。
4. **radius 是当 tick 的地形有效半径**：正常等于 `BodyChunk.Radius`；局部卡角触发
   `TerrainSqueeze` 时可缩小，但不得低于 0.025m。运动扫掠延长、Body 的重力/旧法线探针、
   `LizardLocomotionController.UpdateFooting` 的髋部近地宽限探针、`SphereTerrain.Resolve` 与 `SpherePenetration`
   必须统一使用它；正式半径、约束和渲染不变。
5. **只打「与程序化身体发生接触的静态地形」**：不含生物自身、道具、门等动态物，
   但不能在适配器层预先只留“可站立面”。墙、过陡斜坡和顶面仍须能被射线/MTD 命中，
   供遮挡、身体/段链碰撞和抓点失效使用；“能否产生支撑”由各物种在命中后按法线判定。
   - 本仓库：碰撞掩码层 1（白盒的地板、墙、台阶和顶面都在层 1）。
   - 主项目：基准掩码为 `PhysicsCollisionLayers.ProceduralContactGround`（层 20，1<<19）
     + **排除宿主自身 RID** + `CollideWithAreas=false`——沿用其 `ContactPlanner`
     的被动只读/物理 tick 规范。接线时必须确认该层覆盖目标物种需要碰撞的所有
     静态阻挡；若墙/台阶在另一静态层，就把它并入 `CollisionMask`，不得为 Deer
     只查导航可站立面。参考实现已内建 `CollisionMask` 属性 + `SetExclusions(rids)`。
   - 查询参数对象**复用实例**（参考实现即此；射线参数/球形参数/球 shape 各一份常驻。
     `IntersectRay/GetRestInfo` 返回的 Dictionary 是引擎 API 固有分配，无非分配变体）。
6. **同 tick 内幂等**：同参重复查询须同结果（内核不缓存，一 tick 会多次查询）。
7. **ColliderId 是不透明等同性令牌**：0 表示没有碰撞体；非 0 只承诺同一碰撞体在其
   生命周期内相等，不承诺跨进程数值稳定。确定性探针通过 `FoldOpaqueId` 按首次出现顺序
   规范化它，保留“相同/不同表面”关系，不把 Godot ObjectID 标签误判成运动漂移。
8. **Jolt 验证点**：本仓库 `project.godot` 已启用 Jolt——零法线 HitFromInside、
   斜坡/棱线法线连续、`GetRestInfo` 的 MTD 语义**均已在 Jolt 上实证**（全矩阵 +
   embed/wallside 配置）。主项目同为 Jolt，后端语义风险已消除；接入时跑一遍
   `--route=stand`/`--route=wall` 等价场景做环境级确认即可。
9. **拟态草安装例外**：`TentaclePlantMount.Point` 是宿主确认的洞口安装点，
   `ColliderId` 标识安装地形。合法埋地只限根部代理；可见段和手仍必须遵守射线/MTD 净空。
   目标视线和 `GuidePoints` 也只看这里定义的静态地形；动态猎物由 §4.3 的纯值快照提供。

### 6.1 查询量级（性能预算参考）

- 实测：default（8 chunk + 4 腿）平地巡走 **26.3 射线/tick/只 + 12 形状查询/tick/只**
  （每 chunk/每脚各 1 次 SpherePenetration；`core/smoke` 顺带输出射线计数）。
- 射线构成：身体每 chunk 1~3 根（运动扫掠+支撑+接触法向探针）、每脚 1~2 根、
  推进目标 1~2 根；**FindGrip 采样带 6~11 根只在「迈步找落点」的腿上发生**
  （plant-and-trail 天然限流）。攀爬/多 chunk 品种峰值估算 ~50-60。
- 对照主项目规格 §10.4（24 只并发 ≤3.0ms/tick）：接入后按其流程实测。
  可用的节流阀门（都不改行为语义，回迁时按需做）：尾链 chunk `CollideWithTerrain=false`
  （纯拖尾装饰时）、FindGrip 跨 tick 分摊、远端 LOD 降 tick 率。
- 人形（3 chunk + 2 腿 + 2 手臂）实测 **17.4 射线/tick/只 + 7 形状查询/tick/只**：
  比蜥蜴还省——手臂扇扫（10 根/次）每 tick 只允许一只手轮询（tick 奇偶交替，确定性 +
  预算双保），撑点探地/髋伺服探地各 1 根/tick，plant-and-trail 限流对腿照旧生效。
- 拟态草查询由目标视线、固定候选导引探针和可见段碰撞构成；候选顺序与单 tick 上限必须固定。
  `TickQueryCount` / `PeakQueryCount` 进入 smoke 峰值门。具体预算以
  `tentacle_plant_smoke` 和 Godot matrix 当前输出为准，文档不预写未测数字。
- DaddyLongLegs 按 Jolt primitive units 计费：Raycast=`1`、SpherePenetration=`2`。令身体球为 `B`、
  完整图连接数 `C=B(B-1)/2`、触手总段 `S`、触手数 `T`、每次扇扫 `R`、body/tentacle 共用
  residual 上限 `K=4`，保守式为 `(5+2K)B + 14C + (11+2K)S + T(R+5) + 1`。
  后两项包含所有触手同 tick 各尝试一个原子后缀候选时的前驱球复验、逐段球壳和相邻链边验证。
  Brother/Daddy/Terror 的总段帽为 `64/120/144`，公式下限为 `1658/2870/4021`，硬预算为
  `1700/2900/4050` units/tick；预算适配器只包现有 `ITerrainQuery`，耗尽时确定地返回 miss，
  不旁路到场景树。`Body.Tick` 后每个身体球最多四次 residual sphere MTD，检测到相反方向二周期
  时回上一 tick `LastPos`。最终链约束后每个触手段同样最多四次；检测到二周期或第 4 次后仍命中，
  先复验并回到可行 `LastPos`，只有历史位置也不可行才收回 Anchor。随后用物理半径裁边审计真实
  相邻边；拓扑阻断立即卸掉跨边张力，整条触手连续阻塞达到 6 tick 后才尝试原子后缀恢复。有限
  候选内存在可行项时至多在候选数次尝试内接回；封闭或过窄几何保持断开并确定性重试。tick-end
  穿透门为 `2mm`。Daddy 专属 snag 阀为 `SnagStretchRatio=4`、
  `SnagReleaseTicks=120`，不进入共享连接语义。

## 7. 确定性守则与回归

### 7.1 守则（改内核必须维持）

- 内核**零 delta、零墙钟、零随机**；一切并列判定用固定序打破平局（列表序/固定回退向量）。
- 退化情形显式特判（零向量归一化、两点重合方向），回退值固定。
- 确定性承诺 = **同机同构建 bit-exact**（与 RW 同级）；跨平台浮点一致性不承诺。
- 哈希算法唯一（`DeterminismHasher`，FNV-1a 64 折叠 Pos/Vel 原始位），
  Godot 探针与无引擎回归共用——两边可互证。

### 7.2 三层回归（由快到慢，全部真断言——只打印不判定的探针是假绿）

```bash
# ① 无引擎冒烟（秒级；改内核后的最快反馈）。退出码即判定：
#    双跑 bit-exact + 哈希对基线（钉死在 Program.cs 的 ExpectedHash，防「确定但错误」）
#    + 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 + Launch 恢复
#    + MoveTarget 到达/取消/传送契约 + RotationChunk 拓扑（互绑/覆盖/钉定/尾链/退化语义）
#    + wall-pose 墙顶顶死+侧扰动稳定性 + heavy 上墙回摆收敛（推墙/停驶双场景）
#    + 蜈蚣装配/显式头尾切换/表面课程/生命周期/自避/查询增长
#    + 蜈蚣脚跨薄墙恢复（中心扫掠/低速球壳 MTD/停驶抓点/同侧碰墙对照）
#    + 秃鹫（起飞→巡航→悬停→降落栖息全流程哈希 / 击飞恢复 / Shift 逐字段 / 装配不变量 /
#      评审修复契约：无假到达、零油门不漂移、混合翅态波峰注入、俯冲自救吸附）
#    + 人形八断言（DET/STAND/STUN/ACT/SHIFT/GRAVITY/HANDS/GAIT）
#    + TypeRef 引擎边界扫描。
dotnet run --project core/smoke

# ② 蜘蛛无引擎冒烟：小/大双跑独立哈希 + 正式/多锚点拓扑 + 两段 IK/pole +
#    Shift/Teleport/Launch 完整生命周期 + 极不等长腿可达环 + 反折拖尾恢复 +
#    未确认抓点超时/目标面背书 + 180° 朝向与直接天花板重抓 +
#    小/大型蜘蛛逐腿完整步数、每步前向追回、抬脚高度、支撑期根部位移、
#    同 tick 重置、紧急步和后腿微步比例门 + 小/大型左右 90° 与 180° 急转后的
#    身体对齐、足端/目标本侧槽恢复、腿对分离、IK/pole 连续性与前进门。
dotnet run --project core/spider_smoke

# ③ Cicada 无引擎冒烟：飞行/停驻/起飞/Charge/3D 姿态与生命周期。
dotnet run --project core/cicada_smoke

# ④ 拟态草无引擎冒烟：三预设、三向安装、游荡/绕障/回卷、攻击时序、
#    TargetEffect、Shift/Remount/释放目标、同 seed 双跑/不同 seed 与 8/16 段查询增长。
dotnet run --project core/tentacle_plant_smoke

# ⑤ 鹿无引擎冒烟：三预设拓扑、恒重力支撑与不均匀力、四条多节腿完整步态、
#    滞回/超距/遮挡释放/可及极限拖身、同对互锁（含休息收腿全过程）、
#    RestDelay 前不主动放脚、每次收腿 release→同腿 replant 逐事件闭合、犹豫、体高、COM balance、
#    斜坡/台阶、三预设 180° 换向真实段链弓向、精确 frame 运输消融、
#    MoveTarget、Shift/Teleport/Launch（含已到达目标的 Launch 后逐 tick 重算）、
#    深休息后强生命周期唤醒恢复、稳态 tick 零托管分配、
#    双跑/40vs400Hz/微扰/状态哈希覆盖，并逐项关闭
#    support/pair/hesitation/release/balance/stance/antler/bend 验证门自身会红。
dotnet run --project core/deer_smoke

# ⑥ 长腿爸爸无引擎冒烟：27 个 seed 形态、完整图装配、整链支撑与连续重力、方向推进、
#    职责预算/换步、五类解析地形、单触手打断接管、外部够取、五 seed 卡住恢复、生命周期、
#    查询/自分离/高站姿/持续换步与反向移动、无慢性自旋、Teleport 清 stun/发布 Released、
#    STUCK-RETRY 精确反向重试、
#    body+tentacle tick-end 2mm 残余地形门、静止墙边落点锁与中性支撑、
#    邻边拓扑阻断/guide obstruction 分离、无张力断边、原子后缀重建、外部 Released exactly-once、
#    双跑/40vs400Hz/微扰，并逐项关闭二十四项机制（含主动/被动抓握区分、渐进剥离、
#    slack guide、无助推起步重落点、换步支撑余量、support-lift、independent-duty、
#    idle-landing-stability、idle-support-neutrality、residual-terrain）验证门会红。
dotnet run --project core/daddy_long_legs_smoke

# ⑦ 长腿爸爸 Godot 矩阵：40 配置覆盖普通与 12-body 形态的双跑/40vs400Hz、微扰、
#    三预设四种 seed 形态、平地/深休息起步/点按、三 seed 高站姿→水平移动高度保持、
#    sparse-gait 稀疏形态饥饿阀、坡/墙/
#    静止墙边/天花板/内外角、打断接管、五 seed 卡住脱困、外部够取、击飞与生命周期；另有
#    合计 30 个预期红灯消融（27 无引擎 + 3 Godot）。course 整体隔离在 z=-48m，
#    从约 17.2° 斜坡上表面出生，不与 flat/idle-start 的长触手查询域重叠。
#    判定以脚本结尾横幅为准（当前：40 configurations + 30 ablations）。
./tools/run_daddy_long_legs_matrix.sh

# ⑧ 蜘蛛 Godot 矩阵：40/400Hz、微扰、小/大标准路线、大蜘蛛直线步态、
#    小/大窄墙双侧抱持、墙—墙 L 角、墙→天花板，以及小/大型三向急转恢复。
./tools/run_spider_matrix.sh

# ⑨ Cicada Godot 矩阵：双预设、40/400Hz、微扰、三面停驻、起飞、Charge 与撞墙。
./tools/run_cicada_matrix.sh

# ⑩ 拟态草 Godot 矩阵：floor/wall/ceiling idle、hit、miss、occluded、
#    双跑/40vs400/1mm 微扰与三预设；哈希由脚本当前基线判定。
./tools/run_tentacle_plant_matrix.sh

# ⑪ 鹿 Godot 矩阵：三预设、双跑/40vs400/微扰、斜坡、台阶、墙前停住、90° 转向、
#    三预设各自的 180° 反转、
#    摆动腿真实弓向、粗糙错高、休息、击飞、MoveTarget 与生命周期；退出码聚合八个预期红灯注入。
./tools/run_deer_matrix.sh

# ⑫ 掉落虫无引擎 smoke：21 门全真断言——装配与未知 ID 快速失败、前后不对称重力、
#    失稳宽限、行走/头领航/失稳注力比、18° 坡、越障点火、倒退接近、悬挂判定七分支、
#    悬挂收放（团缩/静止/静息长度/碰撞开关）、退出与悬挂中 Teleport 不弹飞、
#    悬挂中击飞 40 tick 重贴附冷却、俯冲、蓄力（后坐/侧对可及/逃逸放弃）、卡住抖动、
#    负重梯度、表现腿、生命周期、查询预算与 2mm 残余穿透门；九个机制各含消融红灯。
dotnet run --project core/dropbug_smoke

# ⑬ 掉落虫 Godot 矩阵：25 配置 = walk 双跑/40vs400Hz/微扰 + slope/hop/stuck/backward/
#    hang/hang-exit/hang-launch/dive/pounce/pounce-abandon/carry/launch/lifecycle +
#    nimble/bulky 变体各覆盖 walk/hang/dive/pounce（pounce 起跳窗口按预设蓄力时长参数化）。
./tools/run_dropbug_matrix.sh

# ⑭ 蜥蜴 Godot 全矩阵（分钟级；改共享物理内核后必跑）。pipefail + 哈希基线 + 路点下限 +
#    [RESULT] 判定聚合，任何一项红即非零退出：
./tools/run_matrix.sh

# ⑮ 抽离/移植类改动的金标准：改动前后各捕获一次全矩阵输出，逐字节 diff 为空。
#    M5 抽离即以此验收（9 配置 bit-exact 零漂移）。
```

当前 45 配置 = 蜥蜴 20 + 蜈蚣 13 + 秃鹫 4 + 人形 8（`humanoid` ×2 双跑 /
`humanoid-40` 时基不变性 / `humanoid-yank` 击飞限时回正续走 / `humanoid-stun`
昏迷瘫倒+苏醒爬起 / `humanoid-act` 指向→持物→蓄力停驶→出手动作脚本 /
`humanoid-brute`·`humanoid-waif` 变体巡逻）。
无引擎冒烟另含人形八断言（`[CORE-HUMANOID-DET/STAND/STUN/ACT/SHIFT/GRAVITY/HANDS/GAIT]`，
含陡壁不可爬、贴墙接触不计撑地的重力开关边界门与双足步态门）。
既有 20 项 **Lizard Godot 矩阵**除原有时基/品种/嵌入/擦墙门外，固定包含多节脊柱的 `wall-heavy`、
`wall-hexapod` 路点下限，以及四条事件相对回归：`turn-hexapod`（平地 180° 反转后 25 tick 内
展开并对准）、`wall-turn-hexapod`（竖墙上沿面反转，钉死转轴必须是 `SupportNormal`，恢复期间
离开目标墙不得连续超过 2 tick）、`wall-tail`（按逐连接计数只归因 PullOnly 尾链；连续逐节释放
合并为同一脱困 episode，首释放到稳定的最大耗时 ≤40 tick，结束时不得仍卡链）、
`wall-corner`（只认 x=-5.8 目标墙的正确朝向脊柱接触，检查换面/Fallback/抬升；不再误把出生点旁
Step 侧面当墙）。另逐 tick 断言碰撞后结构恢复没有留下 >2mm 穿透。跨品种统一 `<100°` 已删除；
通用姿态硬门改为 SoftOnly 支柱低于静止长度 10% 的最长连续 run。
`carrot-turn-heavy`/`carrot-turn-hexapod` 另在纯平地稳定行进中把 External 目标侧转约 90°（实际方向点积门控）：
头—SpineFollower 前向构型、全身轴与展开姿态必须在 25 tick 内恢复；中段局部轴相对头部局部轴的
最大角度领先、连续领先 tick 与过 60° 对齐阈值的时间差只作诊断输出，尚不以任意审美阈值判红。
（2026-07-30 重跑：两条配置的诊断项均已归零、恢复 7/6 tick——RearBrace 轮的顺带结果，
历史对照见 [`archive/known_issue_three_chunk_turn_response.md`](archive/known_issue_three_chunk_turn_response.md)。）

蜈蚣纯 .NET smoke 的 short/long 双跑 bit-exact 基线为 `655A21496C00E86A` /
`59CBCF993DF8ACD8`；解析课程覆盖地面、18°斜坡、内角墙、外角墙顶与天花板；固定
`Start`、恒 `+X` 的解析下阶梯另断言真实立面、低地落脚、继续前进、不回访身体内部及
不成团。薄墙足端回归另钉住 released foot 的中心扫掠与低速球壳 MTD、停驶 stance 的
既有抓点遮挡，以及同侧碰墙不得误复位。18 节 long 另在 0.4m 窄墙上分别固定 Start/End
向前翻越，要求实体头尾到远侧、取得真实远侧抓足、继续离墙后停驶收敛。共同硬门包含
2 mm 穿透、40 tick 换面、20 tick 逐连接深断链、
`40 + 8×节数` 尾端通过与 16→32 节查询增长 ≤2.25 倍。

Godot 侧新增 13 项 Centipede 矩阵：四预设巡逻、short 双跑/40Hz/微扰、short/long
全向课程、armored 固定头下阶梯、long 固定 End 窄墙前向翻越、嵌入恢复与擦墙。
与蜥蜴/秃鹫/人形配置合计 **45 项完整矩阵，当前全部 GREEN**。蜈蚣最终哈希快照：

- short/long/armored/ribbon：`0F040547BFD02043` / `B66DAAB5D006190E` /
  `A6EDF4704829C261` / `EB6011908D0FAA19`；
- course-short/course-long/step-down-armored：`BB6696619749832D` /
  `A2BE4857DB102C19` / `ECC5207E14979A28`；
- narrow-wall-long-end：`413E289A97ABD487`；
- embed-long/wallside-long：`2C8B2D67731F2B7E` / `501B7C44E06FA68B`。

真实 Jolt 课程中，short/long 的 `maxNoneRun=4/10`、`maxBlockedRun=0/0`、
`maxConnectionRun=3/8`，最大尾端滞后分别为 15/89 tick，对应预算 80/184 tick，
穿透均为 `0m`。固定头下阶梯的领/尾端于 tick 51/121 落地，净前进 3.387m，
终态非相邻间距为半径和 1.917 倍，严重成团连续 0 tick。
固定 End 窄墙场景完成一次前向翻越后停驶 381 tick，终态连接偏差 7%，穿透 `0m`。

DaddyLongLegs 的本轮地形卡腿专项覆盖：物理半径裁边的 tick-end 邻边审计、比 `TerrainSkin`
更薄的墙、阻断边沿链迁移但触手 episode 连续、正交双墙、候选末端球失败与候选自避失败、失败
候选零部分提交、合法凸角绕行、拓扑阻断/guide obstruction 分离、ExternalReach exactly-once
`Released`，以及 Shift 保留 phase、Teleport/Launch/Stun 清恢复暂态。关闭
`EnableTerrainBacktrack` 时组合门必须退出 1。串行换腿另以普通/起步两夹具断言 1-tick stun
同步清槽、同一安全窗不二次释放、下一窗口由其它腿接管，并在起步夹具重新置位 pending；真实
触手 tick 会在控制器检查前把计时归零。Godot `wall/corner/outer/ceiling/stuck` 另用真实 Jolt
统计逐边与逐触手阻塞 run，并要求终态无阻断边、残余穿透与查询预算同时通过。三个正式预算为
`1700/2900/4050`，公式下限为 `1658/2870/4021`。2026-08-05 失速-回冲修复轮完整实跑的 Daddy
无引擎 / Godot flat 基线为 `47F9584427FCD54A` / `FCC938B3D329B276`（微扰
`9657299AD24335D4`），全矩阵查询峰值为 `1667/4050`；地形路线最长相邻边/整触手阻断 episode
为 9 tick，全部终态阻断边为 0、tick-end 穿透为 `0m`。Daddy 的 40 项 Godot 配置 + 30 个预期
红灯消融（27 无引擎 + 3 Godot）全绿；**七套 Godot 矩阵合计 170 项**
（45+16+9+17+18+40+25）全部 GREEN。后续仍以各脚本钉死常量和 PASS 输出为真相源，
**不能用这份快照替代重跑**。

可执行基线真相源分别位于：

- Lizard / Centipede：`tools/run_matrix.sh`、`core/smoke/Program.cs` 与
  `core/smoke/CentipedeSmoke.cs`；
- Spider：`tools/run_spider_matrix.sh` 与 `core/spider_smoke/Program.cs`；
- Cicada：`tools/run_cicada_matrix.sh` 与 `core/cicada_smoke/Program.cs`；
- TentaclePlant：`tools/run_tentacle_plant_matrix.sh` 与 `core/tentacle_plant_smoke/Program.cs`；
- Deer：`tools/run_deer_matrix.sh` 与 `core/deer_smoke/Program.cs`；
- DaddyLongLegs：`tools/run_daddy_long_legs_matrix.sh` 与
  `core/daddy_long_legs_smoke/Program.cs`；
- DropBug：`tools/run_dropbug_matrix.sh` 与 `core/dropbug_smoke/Program.cs`；
- Humanoid：`tools/run_matrix.sh`（HASH_HUMANOID_* 六条）与 `core/smoke/Program.cs`
  的 `HumanoidExpectedHash`；
- Vulture：`tools/run_matrix.sh`（vulture 四配置）与 `core/smoke/Program.cs`
  的 `ExpectedVultureHash`。

有意改变某一后端行为时只更新对应真相源；共享原语改动则全部重新审计，不能用批量改哈希
代替行为断言。文档数字只作当前状态快照。

### 7.3 回迁后的回归形态

主项目无单元测试工程、`tests/` 为空，惯例是 **headless 场景冒烟**（`MotionSmoke` 模式：
`--headless --scene …` + PASS 标记；其 ClockProbe 已验证过「两次构建 40 步轨迹逐 float 一致」，
确定性标准同构）。建议：对应物种的 `core/*_smoke` 原样带走（拟态草为
`core/tentacle_plant_smoke`，鹿为 `core/deer_smoke`，长腿爸爸为
`core/daddy_long_legs_smoke`，掉落虫为 `core/dropbug_smoke`，均为纯 .NET 秒级回归），另加一条
`tech_validation/m10_motion/` 风格的场景探针跑真实地形（等价本仓库 `--determinism` 模式）。

## 8. 迁移路线与集成姿态（主项目对接面调研结论，2026-07）

### 8.1 对接面快照

- 单 csproj 单程序集（`Godot.NET.Sdk/4.7.0`、net8.0、`Nullable` 开、
  `EnableDynamicLoading` 开、无 sln、无 ProjectReference 先例）；命名空间
  `RandomRoomRuntime.*`，文件夹 = 命名空间段小写。
- 物理 60Hz（引擎默认，未显式设 tick 率）、Jolt 后端；**无固定步长/累加器设施**（§3.3 因此存在）。
- 已有一套程序化运动子系统 `scripts/enemies/visual/`（Snapshot 输入 + Manual 时钟
  Skeleton3D + ContactPlanner 被动射线，Gate-C 已过，尚无正式怪物接线 = 处于 M10.4 前）。
  其规格 **§4.3 明确预留「确定性/固定步长内建不满足时可自研求解器作为可替换后端」**
  ——本内核对号入座的口子。
- 硬边界（规格 §7）：gameplay 的 `CharacterBody3D` 是位置/导航/伤害权威；
  视觉层不建碰撞体、不动根节点。

### 8.2 两条迁移路线

| | A. 源码拷入（主项目惯例） | B. 首个 ProjectReference |
|---|---|---|
| 做法 | `core/*.cs` 拷进 `scripts/enemies/visual/motion/kernel/`，命名空间 sed 归化为 `RandomRoomRuntime.Enemies.Visual.Motion.Kernel` | `core/` 连 csproj 拷进仓库任意路径，主 csproj 加 `<ProjectReference>`（`EnableDynamicLoading` 已开） |
| 优点 | 零构建结构改动，与 definition/rig 迁入先例一致 | 保住独立程序集边界，`core/smoke` 的 TypeRef 边界扫描原样有效 |
| 代价 | 边界扫描失效（内核并入主程序集，无法按程序集扫）——解耦只剩纪律约束，建议留 grep 检查：kernel 目录禁 `GD.`/`Node` | 引入该仓库第一个多程序集结构 |

两条路源码逐字相同（差一行 namespace）；**选择留给回迁时的主项目决策**，本契约两者兼容。

### 8.3 两种集成姿态（关键架构决策）

**姿态 1：规格兼容（默认推荐）——内核当「可替换视觉后端」，身体拴在权威根上。**
`CharacterBody3D` 照旧走导航/`MoveAndSlide`；内核身体活在世界系、脚踩真实地形，
但推进意图指向宿主根：`MoveDir = (hostPos + hostVel·k − bodyAnchor)` 的方向、
`RunSpeed` 按距离饱和——身体像 RW 生物追路径点一样**追着权威根拖行**，
其中 `bodyAnchor` 由所选后端定义（Lizard 用 `Head.Pos`，Deer 用 `BodyCenter`）。各后端自己的
支撑、重力和地形响应照常涌现；这不意味着 Deer 具有重力开关或爬墙语义。
追不上/瞬移/跳跃击飞的三档处置见 **§4.1 tether 配方**
（`Shift`/`Launch` 即为此姿态补的接线 API）。视觉层不建碰撞体（内核本就没有
collider，天然合规）、不动根（内核只算自己的 chunk 位置）。

下表先给现有 **Lizard 分支**的 `MonsterMotionSnapshot` 映射；不可把其类名、重力开关或
`BreedParams` 照抄到 Deer：

| Snapshot 字段 | → 内核 |
|---|---|
| `LocalVelocity`（局部） | 转世界系后与根位置合成追踪目标（上式） |
| `Speed01` | `RunSpeed` |
| `LookDirection` | 不进内核（头部朝向属渲染层修饰） |
| `Grounded=false`（走空/坠落） | 放空输入即可（重力开关自然坠落，落地自恢复） |
| 跳跃/击飞/弹射事件 | `LizardLocomotionController.Launch(冲量)`——只放空输入不会给向上冲量（§4.1） |
| 根瞬移/复位/换房 | `LizardLocomotionController.Teleport(rootDelta)`（§4.1；Shift 仅用于地形随体平移的 rebase） |
| `VariantSeed` | 出生时微调 `BreedParams`（纯装配期，运行时仍零随机） |
| `Mode`/`Alertness`/`Health01` | 映射到品种/`RunSpeed` 上限等出生或输入参数 |

**Deer 分支**使用同一 tether 三档，但映射必须按鹿的后端语义实现：

- 常规追踪以 `DeerLocomotionController.BodyCenter` 为 `bodyAnchor`，写
  `MoveDir = normalize(hostPos + hostVel·k - deer.BodyCenter)` 和饱和后的 `RunSpeed`。
  若宿主改用 `MoveTarget`，必须先把根/路径样本投影为邻近可达的**地形表面点**，
  不能直接喂权威根的身体中心高度（§4.1c）。
- `Grounded=false` 不得改写不存在的 `ApplyGravity`；Deer 的 `GravityScale` 恒为 1，
  普通走空由抓点失效后的常开重力恢复。主动跳跃/击飞仍必须调
  `deer.Launch(impulse)`，它会释放腿但不切换重力模式。
- 根瞬移/复位/换房调 `deer.Teleport(rootDelta)`；只有地形连同世界一起 rebase
  才调 `deer.Shift(delta)`。`VariantSeed` 若要表达种内差异，只能在出生时微调深拷贝的
  `DeerParams`，不读或构造 `BreedParams`。
- 站姿/恢复观测读 `PlantedLegCount`、`TotalSupport`、`SupportNormal`、
  `CurrentRideHeight` 和 `BalanceOffset`，不从“重力开关”反推 Deer 状态。

**DaddyLongLegs 分支**也以 `BodyCenter` 为 tether 的 `bodyAnchor`，但意图不压平：墙、顶与棱角
路径点都保留完整 3D 方向。它没有 `Grounded`/`ApplyGravity` 或 forward；宿主只读连续
`ContinuousSupport` / `EffectiveSupport` / `GravityCancellation` / `DirectionalSupport` 和逐触手
`Task`/`NeededForLocomotion`，不能从根朝向
重建身体姿态。主动击飞仍走 `Launch`，换房走 `Teleport`，世界 rebase 走 `Shift`。若 AI 要用
空闲触手够取目标，必须先 `FindIdleTentacle`，逐 tick 回喂纯值快照并消费 `TargetEffects`；
gameplay 对目标的实际修正、伤害与吞入不进入视觉 tether 层。

> 状态如实说明：本仓库交付到「API 与配方齐备」；tether 循环本体（读根、算三档、写
> 输入）活在主项目的 snapshot 映射层里，**闭环要在主仓接线后才算验证完成**——
> 在那之前 M5 的准确状态是「内核抽离完成 + 集成契约就位」，不是「默认集成姿态已闭环」。

**姿态 2：RW 忠实——内核当位置权威，根跟随内核。**
`MoveDir/RunSpeed` 直接来自 AI，宿主根每帧贴到所选后端的权威点：Lizard 为
`Hips.Pos`（或质心），Deer 与 DaddyLongLegs 为各自 `BodyCenter`。运动手感 100% 由所选内核决定（Deer 仍是地面
支撑/跌落，不会因此获得爬墙），但违反规格 §7 现行边界——
适合作为**新怪物原型**走规格修订（M10.4+ 的决策），不适合塞进现有怪物。

> 建议路径：姿态 1 先落地验证（不动任何现有边界），姿态 2 留给需要「真爬墙怪」的品种。

**固定生物例外：拟态草不走两种移动姿态的 tether。**
宿主安装根始终是位置/导航/伤害权威；出生时把地形点、洞外法线、切向提示和 collider ID
写入 `TentaclePlantMount`，核心只模拟根外的手和段链。世界原点重置调用 `Shift`；
真正换洞/换安装面调用 `Remount`，不能把每帧根微动当作连续 Remount。
AI snapshot 另选出一个猎物转成 `TentaclePlantTargetSnapshot`；tick 后由 gameplay 层消费
`TentaclePlantTargetEffect`。视觉层仍不建动态 collider、不移动宿主根，也不直接移动猎物。

### 8.4 版本对齐

内核 csproj 的 `GodotSharp` 版本须与目标仓库 Godot 版本一致（当前两边同为 4.7.0，零动作）；
路线 A 则无此事（跟随主 csproj）。

## 9. 单位与常量表

| 量 | 约定 |
|----|------|
| 长度 | 米。1 RW tile (20px) = 0.5 m，1px = 0.025 m |
| 速度 | `Vel` = 米/tick 位移（积分不乘 dt） |
| 时基 | 40 tick/s，dt = 0.025 s（仅存在于宿主换算层，内核不见 dt） |
| 重力 | 默认 36 m/s²（≙ RW 0.9px/tick²）→ `gravityPerTick = 36×0.025² = 0.0225` |
| 基准体 | 头 0.20m / 髋 0.25m / 脊柱节长 0.3m / 腿长 0.55m / 脚 0.06m / 尾节 0.15m（缩放因子全 1 时） |
| 拟态草 original | 触手理想长度 7.5m / 8 段 / 根半径 0.2m / 梢半径 0.025m（原作 300/8/8/1px 直接换算） |
| 摩擦双档 | 抓稳 0.8/0.5、坠落 0.999/0.3（AirFriction/SurfaceFriction，数值直取 RW，LizardLocomotionController 按重力开关切换） |
