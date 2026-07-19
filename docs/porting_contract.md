# M5 回迁契约：ProcAnim.Core → random-room-runtime

> 本文是 M5 的核心产物：定义运动内核与宿主之间的**全部边界**——在本仓库里宿主是白盒沙盒，
> 回迁后宿主是 [`random-room-runtime`](../../random_room/random-room-runtime/) 的怪物系统。
> 内核侧事实以本仓库 `core/` 为准；主项目侧事实以其 `docs/procedural_monster_visual_spec.md`
> （下称「规格」）为准，引用处标注章节号。主项目对接面的调研快照见 §8。

---

## 0. 回迁三步（TL;DR）

1. **拷 `core/` 一个文件夹**进主项目（两条路线见 §8.2；命名空间归化 = 一次 sed）。
2. **按主项目射线规范实现 `ITerrainQuery`**（§6 有对照模板；`core/godot/RaycastTerrainQuery.cs`
   是最小参考实现，40 行）。
3. **宿主 tick 里装 0.025s 累加器**驱动 `Walker.Tick`，渲染读 `LerpPos(插值分数)`（§3）；
   回归先跑 `core/smoke`（秒级、无引擎），再照主项目 MotionSmoke 惯例加一条 headless 探针（§7）。

## 1. 模块清单与依赖面

### 1.1 文件清单（`core/`）

| 文件 | 职责 | ≙ RW |
|------|------|------|
| `BodyChunk.cs` | 带质量球形粒子，纯数据（Pos/LastPos/Vel/Radius/接触态） | BodyChunk |
| `ChunkConnection.cs` | 距离连接：软弹簧 + 硬约束（Rigid/PullOnly/PushOnly + SoftOnly 姿态弹簧档） | BodyChunkConnection / ConnectToPoint |
| `Body.cs` | chunk 容器与 tick 顺序：受力→积分→约束松弛→地形碰撞 | GenericBodyPart.Update 帧序 |
| `SphereTerrain.cs` | 球 vs 地形命中解算（无反弹+切向摩擦），Body/Limb 共用 | PushOutOfTerrain 语义 |
| `ITerrainQuery.cs` | **唯一接缝**：单射线原语 + TerrainHit（零法线 = HitFromInside） | —（Godot 移植层） |
| `TickContext.cs` | 每 tick 环境包（重力/地形/tick 序号），传值、内核不持引擎对象 | — |
| `Limb.cs` | 腿粒子：单点追目标 IK + plant-and-trail + FindGrip 射线落点 + 闲置休息位 | BodyPart/Limb/LizardLimb |
| `Walker.cs` | 行走驱动：重力开关、支撑系、意图重定向、推进力、翻越三件套 | Lizard 移动块 |
| `BreedParams.cs` | 品种参数表（纯出生配置，运行时零回读） | LizardBreedParams 运动子集 |
| `BodyFactory.cs` | 通用装配器 + 四预设（default/heavy/sprinter/hexapod） | LizardBreeds |
| `DeterminismHasher.cs` | FNV-1a 64 状态哈希折叠（沙盒探针与无引擎回归共用） | — |
| `PlaneTerrainQuery.cs` | ITerrainQuery 纯解析实现（无限平面），测试用 | — |
| `godot/RaycastTerrainQuery.cs` | **引擎适配器**（PhysicsDirectSpaceState3D 包装），归宿主程序集编译 | — |
| `smoke/` | 无引擎冒烟回归 console 工程（§7.2） | — |

### 1.2 依赖面（这是「解耦」的准确定义）

- 内核程序集 `ProcAnim.Core` **只依赖 GodotSharp NuGet 的纯托管数学结构**（`Vector3`/`Mathf`）。
  不引用 `Godot.NET.Sdk`——场景树、物理服务器、`GD`、`Node` 在内核里**编译期不可达**。
- 运行时实证：`core/smoke` 在**纯 .NET 进程**（非 Godot 可执行文件）里驱动内核确定运行。
  即：内核对引擎的依赖 = 一组 float 结构体的数学库用法，与引擎运行时零耦合。
- 保留 `Godot.Vector3` 而非自造数学类型是**有意为之**：回迁目标同为 Godot 4 C#，
  换类型是纯翻译成本 + 浮点语义漂移风险（`Normalized()` 零向量特判、Lerp 展开形式），
  确定性哈希会因此作废。
- `core/godot/RaycastTerrainQuery.cs` 是唯一需要引擎运行时的文件，**故意不在内核程序集里**
  （core csproj 排除 `godot/**`，宿主程序集自行编入）。

### 1.3 内核明确不做（≙ CLAUDE.md 非目标）

寻路（`MoveDir` 从外面来）、游泳/水中运动、渲染与美术、碰撞体/Area 的创建、
宿主根节点的移动（集成姿态见 §8.3）、任何日志输出（内核零 `GD.*`/`Console.*`）。

## 2. 装配契约（BreedParams → BodyFactory）

```csharp
BreedParams p = BodyFactory.Heavy();            // 或 Default/Sprinter/Hexapod/ByName(name)
Walker walker = BodyFactory.CreateWalker(origin, p);
```

- `BreedParams` 是**纯出生配置**：工厂读表装配，内核运行时不回读、零行为分支。
  改品种 = 重新装配一只（沙盒数字键换品种即此路径）。
- `ByName` 未知名**静默回落 default**（内核零日志）；调用侧要告警自行比对返回值 `Name`。
- 装配结果：脊柱 = `SpineSegments` 个 chunk 的 Rigid 链（头…髋）+ 隔节防折叠支柱
  （`BodyStiffness`，SoftOnly PushOnly）；腿对沿脊柱均匀锚定、相邻对出生错位相反
  （对角步态相位种子）；尾巴 = 渐细 PullOnly 链。

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

## 3. 驱动契约（tick 与渲染）

### 3.1 固定步长

- 逻辑固定 **40 tick/s**（`dt = 0.025s`）。内核零 delta 依赖：`Vel` 语义 = 「米/tick 位移」，
  积分 `Pos += Vel` 不乘 dt。**重力预乘步长平方**后传入：
  `gravityPerTick = (0, -g·dt², 0)`，默认 g=36 m/s²（≙ RW 0.9px/tick²）→ `-0.0225`。
- 每 tick 固定顺序：
  ```csharp
  terrain.Bind(space);                        // 仅 Godot 适配器需要（物理帧内合法）
  walker.MoveDir = …; walker.RunSpeed = …;    // 输入（§4）
  chunk.Vel += …;                             // 可选：外力（拖拽/击退），只许写 Vel
  walker.Tick(new TickContext(gravityPerTick, terrain, tick));
  ```
- `Walker.Tick` 内部序（勿拆开自driving，锚定 ≙ RW 帧序）：
  站稳判定/重力开关 → 意图重定向 → 推进力 → `Body.Tick`（受力→积分→约束→碰撞）
  → 顶死计数 → 腿 → 支撑法线更新。

### 3.2 渲染插值

渲染帧读 `chunk.LerpPos(t)` / `limb.LerpPos(t)`，`t` = 物理插值分数 ∈ [0,1)。
Godot 宿主：`t = (float)Engine.GetPhysicsInterpolationFraction()`（沙盒即此）。
渲染永远比物理「晚」不到一个 tick；**逻辑一律不读渲染帧率**。

### 3.3 宿主时基 ≠ 40Hz 时（主项目是 60Hz 物理）

主项目无固定步长设施（调研 §8.1），内核**自带累加器**，塞进宿主的 tick 入口即可：

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

### 3.4 出生 / 传送

出生 = `CreateWalker(origin, p)`（沙盒热切换品种即整体替换，**传送首选也是整体重建**）。
手动传送则不要只写 `Pos`——chunk 与 limb 都需同步 `LastPos = Pos`、清 `Vel`，
腿的 `HuntPos`（世界系落点）也要跟着挪，否则下一 tick 的运动扫掠射线会从旧位置
扫过整张地图、脚会飞回旧落点。

## 4. 输入契约（AI 只有两个旋钮）

| 输入 | 类型/域 | 语义 |
|------|---------|------|
| `Walker.MoveDir` | 世界系单位向量或零 | 移动**意图**方向。给 XZ 平面意图即可：撞墙/上坡时被支撑系自动重定向为沿面上爬（走/爬无模式键）。零 = 无意图（触发闲置姿态计时）。 |
| `Walker.RunSpeed` | [0,1] | 意图强度（≙ AI.runSpeed）。内部多处用 `> 0.1f` 判「有输入」。 |

- 每 tick 写入（不写则保持上次值——AI 决策频率可以低于 tick 频率）。
- **不要**直接写内核其它状态（`SupportNormal`/`GripCounter`/…都是内核私有语义）；
  唯一例外是外力注入 `chunk.Vel`（§3.1 相位）。
- 宿主侧「Grounded=false / 被击飞」不需要特殊 API：放空输入（或照常给意图），
  重力开关自己会因抓空而恢复坠落，落地后自动回归步态（`--yank` 回归即此场景）。

## 5. 输出契约（渲染与 AI 的观测面）

### 5.1 渲染读什么（沙盒 `BodyRenderer` 是参考实现）

| 观测 | 用途（沙盒配色） |
|------|------|
| `Body.Chunks[i].LerpPos(t)` / `.Radius` | 身体球体 |
| `Body.Connections`（跳过 `SoftOnly`） | 骨架连线（防折叠支柱是姿态弹簧，不是骨头，不画） |
| `Walker.ApplyGravity` | 身体色：true 红=坠落 / false 青=抓稳（站/爬涌现态的唯一可视开关） |
| `Limb.LerpPos(t)` / `.Radius` / `.Anchor` | 脚球 + 腿线 |
| `Limb.Gripping` / `.ReachingForTerrain` / `.IdlePose` | 脚色：绿=抓稳推进 / 橙=迈步找落点 / 灰蓝=摆动或闲置 |

### 5.2 AI / 游戏逻辑可读

`Walker.LegsGripping`（抓地腿数）、`ApplyGravity`（是否坠落态）、`SupportNormal`
（支撑面法线：平地≈上、爬墙≈墙法线——可判「正在爬墙」）、`StallTicks`（顶死程度）、
`Limb.GripNormal`/`HasGrip`、`BodyChunk.TerrainContact`/`ContactNormal`。
全部是**只读观测**；写它们后果自负。

## 6. ITerrainQuery 契约（唯一接缝的全部语义）

```csharp
public interface ITerrainQuery
{
    bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit);
}
public readonly struct TerrainHit { Vector3 Point; Vector3 Normal; ulong ColliderId; }
```

实现方必须满足：

1. **线段射线**：from→to 有限段，命中返回最近交点与表面法线。
2. **HitFromInside 必须开启**：起点已陷入 collider 时返回命中且 **`Normal = 零向量`**
   ——内核所有调用点都特判零法线（直接归一化会 NaN）。纯实现参考 `PlaneTerrainQuery`。
3. **只打「可站立的静态地形」**：不含生物自身、道具、门等动态物。
   - 本仓库：碰撞掩码层 1（白盒全在层 1）。
   - 主项目：掩码 = `PhysicsCollisionLayers.ProceduralContactGround`（层 20，1<<19；
     由 RoomBuilder/LadderBuilder 附着于可行走静态几何）+ **排除宿主自身 RID**
     （`SetCollisionExclusions`）+ `CollideWithAreas=false`——照抄其 `ContactPlanner`
     的既有规范（规格 §9.2：被动只读、只在物理 tick 内）。
   - 查询参数对象**复用实例**（主项目惯例，避免每射线分配）。
4. **同 tick 内幂等**：同参重复查询须同结果（内核不缓存，一 tick 会多次查询）。
5. **Jolt 验证点**（主项目物理后端是 Jolt，本仓库是 Godot Physics）：接入时先跑
   `--route=stand`/`--route=wall` 等价场景，确认 Jolt 的 `hit_from_inside` 同样
   返回零法线、斜坡/棱线法线连续——这是两个后端间唯一有语义风险的点。

### 6.1 射线量级（性能预算参考）

- 实测：default（8 chunk + 4 腿）平地巡走 **26.5 射线/tick/只**（`core/smoke` 顺带输出）。
- 构成：身体每 chunk 1~3 根（运动扫掠+支撑+接触法向探针）、每脚 1~2 根、
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

### 7.2 三层回归（由快到慢）

```bash
# ① 无引擎冒烟（秒级；改内核后的最快反馈）：
dotnet run --project core/smoke        # PASS + 双跑哈希一致（当前 17A085DE53E2E133）

# ② Godot 全矩阵（分钟级；改物理内核后必跑，命令见 CLAUDE.md §5）：
#    default 双跑 + 40/400Hz 一致 + perturb 变哈希 + wall/stand 路线 + 三品种。

# ③ 抽离/移植类改动的金标准：改动前后各捕获一次全矩阵输出，逐字节 diff 为空。
#    M5 抽离即以此验收（9 配置 bit-exact 零漂移）。
```

当前已知哈希（400Hz、2000 tick）：default `0C757AF36469CD1C`、wall `F3B88D81E286CC8B`、
stand `7069911AEECF1DD2`、heavy `B2AC7CA1BB8DF9D0`、sprinter `30DF00DC82039FC5`、
hexapod `2E31ED4688385CD1`。

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
| 优点 | 零构建结构改动，与 definition/rig 迁入先例一致 | 保住编译期解耦边界与 `core/smoke` 原样可跑 |
| 代价 | 解耦只剩纪律约束（建议留 grep 检查：kernel 目录禁 `GD.`/`Node`） | 引入该仓库第一个多程序集结构 |

两条路源码逐字相同（差一行 namespace）；**选择留给回迁时的主项目决策**，本契约两者兼容。

### 8.3 两种集成姿态（关键架构决策）

**姿态 1：规格兼容（默认推荐）——内核当「可替换视觉后端」，身体拴在权威根上。**
`CharacterBody3D` 照旧走导航/`MoveAndSlide`；内核身体活在世界系、脚踩真实地形，
但推进意图指向宿主根：`MoveDir = (hostPos + hostVel·k − Head.Pos)` 的方向、
`RunSpeed` 按距离饱和——身体像 RW 生物追路径点一样**追着权威根拖行**，
腿/重力开关/爬墙全部照常涌现。视觉层不建碰撞体（内核本就没有 collider，天然合规）、
不动根（内核只算自己的 chunk 位置）。`MonsterMotionSnapshot` 映射：

| Snapshot 字段 | → 内核 |
|---|---|
| `LocalVelocity`（局部） | 转世界系后与根位置合成追踪目标（上式） |
| `Speed01` | `RunSpeed` |
| `LookDirection` | 不进内核（头部朝向属渲染层修饰） |
| `Grounded=false` | 放空输入即可（重力开关自然坠落，落地自恢复） |
| `VariantSeed` | 出生时微调 `BreedParams`（纯装配期，运行时仍零随机） |
| `Mode`/`Alertness`/`Health01` | 映射到品种/`RunSpeed` 上限等出生或输入参数 |

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
| 摩擦双档 | 抓稳 0.8/0.5、坠落 0.999/0.3（AirFriction/SurfaceFriction，数值直取 RW，Walker 按重力开关切换） |
