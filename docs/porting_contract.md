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
3. **宿主 tick 里装 0.025s 累加器**驱动物种控制器的 `Tick`，渲染读 `LerpPos(插值分数)`（§3）；
   回归先跑 `core/smoke`（秒级、无引擎），再照主项目 MotionSmoke 惯例加一条 headless 探针（§7）。

## 1. 模块清单与依赖面

### 1.1 文件清单（`core/`）

| 文件 | 职责 | ≙ RW |
|------|------|------|
| `BodyChunk.cs` | 带质量球形粒子，纯数据（Pos/LastPos/Vel/Radius/接触态 + RotationChunk 朝向参照与派生 `Rotation`） | BodyChunk（含 rotationChunk/Rotation） |
| `ChunkConnection.cs` | 距离连接：软弹簧 + 硬约束（Rigid/PullOnly/PushOnly + SoftOnly 姿态弹簧档）；构造时两端互绑 RotationChunk（后建覆盖）；逐连接释放诊断计数 | BodyChunkConnection / ConnectToPoint |
| `Body.cs` | chunk 容器与 tick 顺序：受力→积分→约束松弛→地形碰撞→卡角时恢复碰撞新增的姿态违反→卡链释放 | GenericBodyPart.Update 帧序 + BodyPart.Reset |
| `ContactManifold3D.cs` | 单 tick 固定容量接触法线集合；固定迭代投影到非正交墙/地接触可行锥 | 3D 接缝扩展 |
| `SphereTerrain.cs` | 球 vs 地形命中解算（无反弹+切向摩擦），Body/Limb 共用 | PushOutOfTerrain 语义 |
| `ITerrainQuery.cs` | **唯一接缝**：射线 + 球体穿透（MTD）两原语 + TerrainHit（零法线 = HitFromInside） | —（Godot 移植层） |
| `TickContext.cs` | 每 tick 环境包（重力/地形/tick 序号），传值、内核不持引擎对象 | — |
| `Limb.cs` | 腿粒子：单点追目标 IK + plant-and-trail + FindGrip 射线落点 + 闲置休息位 | BodyPart/Limb/LizardLimb |
| `LizardLocomotionController.cs` | 蜥蜴专属运动控制器：重力开关、支撑系、意图重定向、推进力、翻越三件套 | Lizard 移动块 |
| `BreedParams.cs` | 品种参数表（纯出生配置，运行时零回读） | LizardBreedParams 运动子集 |
| `BodyFactory.cs` | 通用装配器 + 四预设（default/heavy/sprinter/hexapod） | LizardBreeds |
| `CentipedeLeg.cs` | 蜈蚣足端：真实地形抓点 + 确定性行波；抓握调制本节支撑，不刚性反拉身体 | 独立 3D 扩展 |
| `CentipedeLocomotionController.cs` | 蜈蚣专属控制器：双端表面轨迹、逐节局部支撑、全向贴面与有上限自避 | 独立 3D 扩展 |
| `CentipedeParams.cs` | 全局运动参数 + 默认体型曲线 + 任意节出生参数与稀疏逐节覆写 | 独立 3D 扩展 |
| `CentipedeFactory.cs` | 质量加权装配器 + short/long/armored/ribbon 四个稳定 ID 预设 | 独立 3D 扩展 |
| `DeterminismHasher.cs` | FNV-1a 64 状态哈希折叠（沙盒探针与无引擎回归共用） | — |
| `PlaneTerrainQuery.cs` | ITerrainQuery 纯解析实现（无限平面），测试用 | — |
| `godot/RaycastTerrainQuery.cs` | **引擎适配器**（PhysicsDirectSpaceState3D 包装），归宿主程序集编译 | — |
| `smoke/` | 无引擎冒烟回归 console 工程（§7.2） | — |

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

### 1.3 内核明确不做（≙ CLAUDE.md 非目标）

AI 寻路（`MoveDir`/邻近 `MoveTarget` 从外面来）、战斗、游泳/水中运动、正式渲染与美术、
碰撞体/Area 的创建、宿主根节点的移动（集成姿态见 §8.3）、任何日志输出
（内核零 `GD.*`/`Console.*`）。

## 2. 装配契约（BreedParams → BodyFactory）

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

### 3.2 渲染插值

渲染帧读 `chunk.LerpPos(t)` / `limb.LerpPos(t)`，`t` = 物理插值分数 ∈ [0,1)。
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

## 4. 输入契约（AI 有两个旋钮 + 一个可选路径点直喂）

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

## 5. 输出契约（渲染与 AI 的观测面）

### 5.1 渲染读什么（沙盒 `BodyRenderer` 是参考实现）

| 观测 | 用途（沙盒配色） |
|------|------|
| `Body.Chunks[i].LerpPos(t)` / `.Radius` | 身体球体 |
| `Body.Connections`（跳过 `SoftOnly`） | 骨架连线（防折叠支柱是姿态弹簧，不是骨头，不画） |
| `LizardLocomotionController.ApplyGravity` | 身体色：true 红=坠落 / false 青=抓稳（站/爬涌现态的唯一可视开关） |
| `Limb.LerpPos(t)` / `.Radius` / `.Anchor` | 脚球 + 腿线 |
| `Limb.Gripping` / `.ReachingForTerrain` / `.IdlePose` | 脚色：绿=抓稳推进 / 橙=迈步找落点 / 灰蓝=摆动或闲置 |

### 5.2 AI / 游戏逻辑可读

`LizardLocomotionController.LegsGripping`（抓地腿数）、`ApplyGravity`（是否坠落态）、`SupportNormal`
（支撑面法线：平地≈上、爬墙≈墙法线——可判「正在爬墙」）、`StallTicks`（顶死程度）、
`StraightenOutNeeded` / `SpineCornerStuckTicks` / `MaxSpineCornerStuckTicks`（姿态恢复需求、
局部髋部卡角与本次恢复生命周期峰值；Teleport/Launch 开新生命周期；诊断/AI 可读，不得由宿主写）、
`AtMoveTarget`（直喂模式到达信号，宿主换点驱动）、`LastMoveTarget`/`LastMoveTargetKind`
（本 tick 实际推进胡萝卜及其来源分支：Support 钉面 / Crest 翻越 / External 直喂或沿面重定向 /
Fallback 空中退化——Fallback 长期驻留 = 方向驱动在棱边失去地形参照，可视化为红色）、
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
5. **只打「可站立的静态地形」**：不含生物自身、道具、门等动态物。
   - 本仓库：碰撞掩码层 1（白盒全在层 1）。
   - 主项目：掩码 = `PhysicsCollisionLayers.ProceduralContactGround`（层 20，1<<19；
     由 RoomBuilder/LadderBuilder 附着于可行走静态几何）+ **排除宿主自身 RID**
     + `CollideWithAreas=false`——照抄其 `ContactPlanner` 的既有规范
     （规格 §9.2：被动只读、只在物理 tick 内）。参考实现已内建对应 API：
     `CollisionMask` 属性 + `SetExclusions(rids)`。
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

### 6.1 查询量级（性能预算参考）

- 实测：default（8 chunk + 4 腿）平地巡走 **26.3 射线/tick/只 + 12 形状查询/tick/只**
  （每 chunk/每脚各 1 次 SpherePenetration；`core/smoke` 顺带输出射线计数）。
- 射线构成：身体每 chunk 1~3 根（运动扫掠+支撑+接触法向探针）、每脚 1~2 根、
  推进目标 1~2 根；**FindGrip 采样带 6~11 根只在「迈步找落点」的腿上发生**
  （plant-and-trail 天然限流）。攀爬/多 chunk 品种峰值估算 ~50-60。
- 对照主项目规格 §10.4（24 只并发 ≤3.0ms/tick）：接入后按其流程实测。
  可用的节流阀门（都不改行为语义，回迁时按需做）：尾链 chunk `CollideWithTerrain=false`
  （纯拖尾装饰时）、FindGrip 跨 tick 分摊、远端 LOD 降 tick 率。

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
#    + wall-pose 墙顶顶死+侧扰动稳定性 + 蜈蚣装配/显式头尾切换/表面课程/生命周期/自避/查询增长
#    + 蜈蚣脚跨薄墙恢复（中心扫掠/低速球壳 MTD/停驶抓点/同侧碰墙对照）
#    + TypeRef 引擎边界扫描。
dotnet run --project core/smoke

# ② Godot 全矩阵（分钟级；改物理内核后必跑）。pipefail + 哈希基线 + 路点下限 +
#    [RESULT] 判定聚合，任何一项红即非零退出：
./tools/run_matrix.sh

# ③ 抽离/移植类改动的金标准：改动前后各捕获一次全矩阵输出，逐字节 diff 为空。
#    M5 抽离即以此验收（9 配置 bit-exact 零漂移）。
```

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

蜈蚣纯 .NET smoke 的 short/long 双跑 bit-exact 基线为 `4DAD09DE3CB81C31` /
`4E3DFC052BA4E74D`；解析课程覆盖地面、18°斜坡、内角墙、外角墙顶与天花板；固定
`Start`、恒 `+X` 的解析下阶梯另断言真实立面、低地落脚、继续前进、不回访身体内部及
不成团。薄墙足端回归另钉住 released foot 的中心扫掠与低速球壳 MTD、停驶 stance 的
既有抓点遮挡，以及同侧碰墙不得误复位。共同硬门包含 2 mm 穿透、40 tick 换面、20 tick 逐连接深断链、
`40 + 8×节数` 尾端通过与 16→32 节查询增长 ≤2.25 倍。

Godot 侧新增 12 项 Centipede 矩阵：四预设巡逻、short 双跑/40Hz/微扰、short/long
全向课程、armored 固定头下阶梯、long 嵌入恢复与擦墙。与既有 20 项 Lizard 合计
**32 项完整矩阵，当前全部 GREEN**。最终哈希快照：

- short/long/armored/ribbon：`BE58C639D59E1EA2` / `0D1D0D51D5E9C26B` /
  `D595C149C1C6B8EC` / `D834CFF4122082C3`；
- course-short/course-long/step-down-armored：`D6F99637C6D76EE1` /
  `30793ACEDD88F34C` / `3D2594F93BC2F009`；
- embed-long/wallside-long：`FE8E2E356129F7A2` / `E2837F5747FDFBFF`。

真实 Jolt 课程中，short/long 的 `maxNoneRun=1/9`、`maxBlockedRun=0/0`、
`maxConnectionRun=4/7`，最大尾端滞后分别为 15/89 tick，对应预算 80/184 tick，
穿透均为 `0m`。固定头下阶梯的领/尾端于 tick 46/116 落地，净前进 3.387m，
终态非相邻间距为半径和 1.917 倍，严重成团连续 0 tick。

可执行基线真相源位于 `tools/run_matrix.sh`（Godot 矩阵）、
`core/smoke/Program.cs`（Lizard 无引擎）与 `core/smoke/CentipedeSmoke.cs`
（蜈蚣无引擎）。有意改内核时必须同步更新对应真相源；文档数字只作当前状态快照。

### 7.3 回迁后的回归形态

主项目无单元测试工程、`tests/` 为空，惯例是 **headless 场景冒烟**（`MotionSmoke` 模式：
`--headless --scene …` + PASS 标记；其 ClockProbe 已验证过「两次构建 40 步轨迹逐 float 一致」，
确定性标准同构）。建议：`core/smoke` 原样带走（纯 .NET，秒级），另加一条
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
但推进意图指向宿主根：`MoveDir = (hostPos + hostVel·k − Head.Pos)` 的方向、
`RunSpeed` 按距离饱和——身体像 RW 生物追路径点一样**追着权威根拖行**，
腿/重力开关/爬墙全部照常涌现。追不上/瞬移/跳跃击飞的三档处置见 **§4.1 tether 配方**
（`Shift`/`Launch` 即为此姿态补的接线 API）。视觉层不建碰撞体（内核本就没有
collider，天然合规）、不动根（内核只算自己的 chunk 位置）。`MonsterMotionSnapshot` 映射：

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

> 状态如实说明：本仓库交付到「API 与配方齐备」；tether 循环本体（读根、算三档、写
> 输入）活在主项目的 snapshot 映射层里，**闭环要在主仓接线后才算验证完成**——
> 在那之前 M5 的准确状态是「内核抽离完成 + 集成契约就位」，不是「默认集成姿态已闭环」。

**姿态 2：RW 忠实——内核当位置权威，根跟随内核。**
`MoveDir/RunSpeed` 直接来自 AI，宿主根每帧贴到 `Hips.Pos`（或质心）。
运动手感 100% 是内核的（爬墙/翻越/摔落全真），但违反规格 §7 现行边界——
适合作为**新怪物原型**走规格修订（M10.4+ 的决策），不适合塞进现有怪物。

> 建议路径：姿态 1 先落地验证（不动任何现有边界），姿态 2 留给需要「真爬墙怪」的品种。

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
| 摩擦双档 | 抓稳 0.8/0.5、坠落 0.999/0.3（AirFriction/SurfaceFriction，数值直取 RW，LizardLocomotionController 按重力开关切换） |
