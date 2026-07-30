# 《雨世界》程序化生物动画/运动系统 技术研究报告

> 研究日期：2026-07-18
> 目标：为 `random-room-runtime`（C# / Godot 4.x）的"程序化驱动怪物动作系统"提供 Rain World 底层实现的一手参考。
> 方法：深度检索（103 个 agent、20 个来源、81 条论断、25 条对抗式验证）+ 人工补抓（浏览器直读被 WebFetch 拦截的 wiki + 复刻教程原文）。
> 相关文档：[monster_visual_research.md](monster_visual_research.md)（美术层）、[procedural_monster_visual_spec.md](procedural_monster_visual_spec.md)、[gameplay_design.md](gameplay_design.md)。

---

## 0. 一句话结论 & 诚实的缺口

Rain World 的生物不是"骨骼+关键帧"，而是一套**"物理粒子 + 距离约束"驱动的活体骨架**，再在其上盖一层**可变形网格**做渲染，整个模拟跑在**固定 40 tick/秒**的时间步上，渲染与模拟解耦并用插值平滑。这套架构从 2017 首发一直稳定到 2025 的 Watcher DLC。

> **2026-07-18 重大更新**：原先"腿部 IK/locomotion 无一手资料"的缺口已通过**本机反编译 `Assembly-CSharp.dll`**（ilspycmd 10.1.1）彻底补齐——**详见新增的 [§11 反编译实证](#11-反编译实证真实实现代码级)**，那是全文最硬的一节。下面 §0/§5 的"未确认"措辞保留为历史记录，实际答案以 §11 为准。

**（历史记录）曾经的缺口**：网络公开资料里没有逐行讲清 Rain World 蜥蜴的腿怎么做 IK、脚怎么判定抬/落、爬/走/游怎么切换。GDC 演讲是"艺术+代码"的哲学与流程，不是代码走查；网络深挖细节全部来自社区**反编译**整理的 wiki（结构准，但非官方）。因此本报告对**子问题(2)（四肢 IK/贴地/locomotion 切换）**曾采取两条腿走路：
- **A. 雨世界真实做法**：见 §11（已由本机反编译确证）。
- **B. 可移植做法**：一份**带完整代码的忠实复刻教程**（merxon22, Unity），raycast 贴地 + IK 摆腿的具体算法和常量（见 §5.B，与 §11 的 RW 真实做法互为印证）。

⚠️ 两条在验证阶段被 **0-3 反驳** 的说法，**不要采信**：
1. "四肢和尾巴纯装饰、完全在物理之外" —— 错。RW 有 `appendages`（附肢）会和地形碰撞、能被武器击中（见 §3.3）。
2. "生物图形一律走 TriangleMesh、由 FSprite 的布尔参数选择" —— 过度概括，不成立。

---

## 1. 资料来源与可信度分级

| # | 来源 | 类型 | 可信度 | 覆盖 |
|---|------|------|--------|------|
| S1 | [GDC 2016《The Rain World Animation Process》— Joar Jakobsson & James Therrien](https://www.youtube.com/watch?v=-iXwvoFhPuU)（[GDC Vault](https://www.gdcvault.com/play/1023475/Animation-Bootcamp-Rainworld-Animation)） | **一手**（开发者本人） | 高（哲学/流程） | 动画哲学、art+code |
| S2 | [Game Developer 采访：Crafting the chaotic ecosystem of Rain World](https://www.gamedeveloper.com/design/crafting-the-complex-chaotic-ecosystem-of-i-rain-world-i-) | 二手（引 Jakobsson 原话） | 高 | "点+距离=帧" |
| S3 | [Unity 官方博客：Exploring procedural design in Rain World](https://unity.com/blog/exploring-procedural-design-rain-world) | 二手（引 Videocult） | 中（引擎厂商） | slugcat 双 chunk、per-creature from scratch |
| S4 | [Rain World Modding Wiki — Code Structure / PhysicalObject](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject) | 社区反编译整理 | 中高（交叉印证） | 类结构、BodyChunk、appendage、graphicsModule |
| S5 | [Modding Wiki — Creating a Custom Object](https://rainworldmodding.miraheze.org/wiki/Creating_a_Custom_Object) | 社区反编译 + 可运行 mod 代码 | 中高 | BodyChunk 构造签名、40 TPS、timeStacker |
| S6 | [Modding Wiki — Futile](https://rainworldmodding.miraheze.org/wiki/Futile) + [MattRix/Futile 源码](https://github.com/MattRix/Futile) | 社区 + **一手源码** | 高（GitHub 可核） | TriangleMesh/CustomFSprite 非 Futile、网格四数组 |
| S7 | [官方 Wiki — Technical Glossary / Tickrate](https://rainworld.miraheze.org/wiki/Technical_Glossary/Tickrate) | 社区反编译 | 中高 | 40 TPS、timeStacker、Lerp/Slerp |
| S8 | [官方 Wiki — Lizards](https://rainworld.miraheze.org/wiki/Lizards) | 社区数据整理 | 中 | 各蜥蜴数值参数（速度/质量/攀爬/舌） |
| S9 | [merxon22 — Recreating Rain World's 2D Procedural Animation, Part 1](https://medium.com/@merxon22/recreating-rainworlds-2d-procedural-animation-part-1-4d882f947e9f) / [Part 2](https://medium.com/@merxon22/recreating-rain-worlds-2d-procedural-animation-part-2-f5faef82aa50) | 第三方**可运行复刻**（含代码） | 中（非 RW 本体，但代码可跑） | **IK/贴地/摆腿具体算法**、降帧渲染 |
| S10 | [gafferongames — Fix Your Timestep!](https://gafferongames.com/post/fix_your_timestep/) | 经典工程博客 | 高（通用） | 固定步长+插值范式 |

> 重要检索限制：`*.miraheze.org` 对自动抓取返回 HTTP 403，本报告中这些页面的引用通过"搜索片段 + 浏览器直读 + 交叉印证"获得；Futile 网格数组这类关键事实已回到 [MattRix/Futile GitHub 一手源码](https://github.com/MattRix/Futile) 核对。

---

## 2. 顶层类结构（子问题 1）

反编译整理出的层次（[S4](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject)、官方 wiki Classification）：

```
UpdatableAndDeletable (UAD)         // 每 tick 被 Update 的一切
  └── PhysicalObject                // 有碰撞、由 BodyChunk 组成；持有 graphicsModule
        └── Creature                // 所有"已实体化(realized)"生物的基类
              └── AirBreatherCreature → Lizard / Vulture / ...
        └── PlayerCarryableItem / Weapon / OracleSwarmer / ...
```

- **每个 realized 物体都对应一个 `AbstractPhysicalObject`**（挂在 `AbstractWorldEntity` 下），做"抽象/实体"两级 LOD：房间没被玩家看到时生物只跑廉价抽象 AI/物理，房间"realized"后才升级成完整 `Creature` 跑真实物理与碰撞。**这是 RW 能同时"模拟整个生态"却不炸性能的关键。**（见 §7）

对 Godot 移植的直接映射：把"持久化的轻量实体"和"重型模拟实体"拆成两个类，只对当前房间/邻近房间激活重型模拟。

---

## 3. 身体 = 物理粒子 + 距离约束（子问题 1 核心）

### 3.1 BodyChunk：一个带质量的圆

> "All PhysicalObjects are composed of BodyChunks. Every BodyChunk has circular collision." —— [S4](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject)

构造签名（由可运行 mod 代码验证，[S5](https://rainworldmodding.miraheze.org/wiki/Creating_a_Custom_Object)）：

```csharp
new BodyChunk(owner, index, position, radius_px, mass)
// 例：new BodyChunk(this, 0, Vector2.zero, 4f, 0.05f)
//     owner=所属 PhysicalObject, index=0, 半径 4 像素, 质量 0.05
```

- 碰撞层只有 **3 层：0 / 1 / 2**（`collisionLayer`，非法值会报错）。
- 每个 chunk 是**圆形碰撞**——整套物理是"一堆圆"，没有多边形碰撞体。
- 每个 `PhysicalObject` 的物体属性（[S4](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject)）：
  - `airFriction` / `waterFriction`：**每帧速度乘以该值**（取值 0~1、通常接近 1；>1 会指数发散）。
  - `buoyancy`：浮力（0=悬浮，负=下沉，正=上浮）。
  - `g` / `Gravity`：个体重力倍率 × 房间重力。
  - `GoThroughFloors` / `CollideWithTerrain` / `CollideWithSlopes`：可整体或**逐 chunk**设置。

### 3.2 BodyChunkConnection：把 chunk 连成骨架的"弹簧/杆"

> `bodyChunkConnections`：Array of objects determining how different chunks are physically tied to each other. Should always be instantiated by the constructor (empty if no chunk connections). —— [S4](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject)

- 构造时**永远实例化**该数组，无连接时为 `new BodyChunkConnection[0]`（[S5](https://rainworldmodding.miraheze.org/wiki/Creating_a_Custom_Object)）。
- 连接是**软的弹簧/约束杆**（有 Normal/Push/Pull/Rotate 等模式），不是完全刚性的杆——这是"软体感"的来源。
- **Slugcat 就是 2 个球形 chunk 用一根连接锁在（名义上）固定距离**，正因为是两点+连接，它才能翻滚旋转（[S3](https://unity.com/blog/exploring-procedural-design-rain-world)：*"two spherical chunks locked to each other at a fixed distance, enabling it to tumble and rotate"*；官方 wiki 亦记为头 chunk + 下身/髋 chunk）。

### 3.3 附肢（appendages）—— 介于物理与装饰之间的第三态

这是修正"四肢纯装饰"错误说法的关键（[S4](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject)）：

> `appendages`：A list of appendages this instance has. **Appendages don't directly collide with body chunks, but still collide with terrain and can be hit by weapons.** Instances with appendages should implement `PhysicalObject.IHaveAppendages`.

所以 RW 的身体其实是**三层**：
1. **BodyChunk**：完整圆形碰撞 + 质量，核心骨架（头、身、髋…）。
2. **Appendage（附肢）**：不与 chunk 互撞，但**会撞地形、能被武器命中**——如蜥蜴的尾、舌等细长部件。
3. **graphicsModule（图形层）**：纯视觉 + 程序化动画层（见 §5、§6）。

"四肢是不是物理"这个问题的正确答案是：**看它属于哪一层**——有的做成 chunk（进物理），有的做成 appendage（半物理），纯装饰腿在 graphicsModule 里画。

### 3.4 数值积分：Verlet 式"受力速度 vs 瞬时速度"

> **Forcing velocity**（`vel` 字段）："the velocity the chunk *wants* to have due to gravity, buoyancy, and other forces"，对多 chunk 生物**很少等于**瞬时速度。
> **Instantaneous velocity**：最近两 tick 位置之差。
> —— [Technical Glossary / Velocity](https://rainworld.miraheze.org/wiki/Technical_Glossary/Velocity)

即：每个 chunk 独立受力（重力/浮力/摩擦/AI 施力）累积到 `vel`，再由 `BodyChunkConnection` 距离约束把彼此拉回目标距离——**标准的 Verlet 约束粒子求解器**。

**移植配方（每 tick）**：
```
每个 chunk 存 pos, lastPos（上一 tick 位置）, vel（受力累加器）
1) 施力：vel += (重力/浮力/AI力) * dt ;  vel *= friction
2) 积分：newPos = pos + vel*dt   （或 Verlet: newPos = pos + (pos-lastPos)*friction + accel*dt²）
3) 约束松弛：对每条 connection，把两端 chunk 拉回目标距离（迭代若干次）
4) 地形碰撞：圆 vs tile 解穿透
5) lastPos = 旧 pos ; pos = newPos
瞬时速度 = (pos - lastPos)/dt
```

---

## 4. 动画哲学：点 + 距离 = 一帧（一手原话）

> "Contrary to the classic Disney style of animation, where you draw every frame... I have a bunch of points in space and I connect them at certain distances. So, for example, if you have 10 points connected to each other they are acting as a frame."
> —— Joar Jakobsson，[S2](https://www.gamedeveloper.com/design/crafting-the-complex-chaotic-ecosystem-of-i-rain-world-i-)

关键含义：**没有关键帧**。每 tick 由约束求解出的点集**本身就是当前帧**。动画 = 物理解算的副产品，这也是为什么"动画"和"locomotion AI"在 RW 里是同一件事（GDC 演讲主旨：*look / animation / locomotion AI 三者交织*，[S1](https://www.youtube.com/watch?v=-iXwvoFhPuU)）。

---

## 5. 四肢 IK / 贴地 / locomotion 切换（子问题 2）

### 5.A 雨世界真实做法（已确认部分 + 缺口）

**已确认**：
- 视觉与程序化动画在 `graphicsModule`（`GraphicsModule` 基类；蜥蜴是 `LizardGraphics`）里完成。`PhysicalObject` 通过 `InitiateGraphicsModule`（房间可见时）/`GraphicsModuleUpdated`（每次更新后）/`DisposeGraphicsModule`（房间不可见时）管理它。**武器等物体没有 graphicsModule，生物有。**（[S4](https://rainworldmodding.miraheze.org/wiki/Rain_World_Code_Structure/PhysicalObject)）
- 腿在图形层被程序化摆动、末端向"抓取点/地形点"做 IK 收敛；尾/舌走 appendage（§3.3）。蜥蜴的舌是有射程参数的攻击（`tongueAttackRange`，[S8](https://rainworld.miraheze.org/wiki/Lizards)）。

**未确认（全网无存活一手资料）**：
- 腿到底是"额外 BodyChunk"、"独立约束链"，还是"纯 IK 图形层"——**无法从现存来源断定**。
- 脚如何判定抬起/落下、贴地用什么（raycast？就近地形吸附？抓取 chunk 锚定？）——**未确认**。
- 走/爬/攀壁/游泳/抓杆如何切换、由什么状态机/AI 信号驱动——**未确认**。

> 取证建议：这些只能用 **dnSpy/ILSpy 反编译自有的 `Assembly-CSharp.dll`**，直接读 `LizardGraphics`、`Limb`、`GenericBodyPart`、`Lizard.Act`/` ...Movement` 等类。社区明确"源码不公开、只能自行反编译，且反编译副本不得再分发"（[Getting Started Coding](https://rainworldmodding.miraheze.org/wiki/Getting_Started_Coding)）。

### 5.B 可移植做法：merxon22 的忠实复刻（有代码、可直接抄）

这份 Unity 复刻（[S9 Part 2](https://medium.com/@merxon22/recreating-rain-worlds-2d-procedural-animation-part-2-f5faef82aa50)）给出了**完整的腿部程序化动画算法**——正是你们缺的那块，且与引擎无关，Godot 可照搬：

**1) 何时迈步：重心-支撑多边形平衡判定**
```
centerOfMass = body.position.x
若 centerOfMass 落在两只脚的 x 之间 → 平衡，不迈步
否则 → 触发失衡一侧迈步
（用 footDisplacementOnX 偏移修正交叉腿的阈值）
```

**2) 落点：向下 raycast + 过冲预判**
```csharp
ray = Physics2D.Raycast(body.position + (footDisplacementOnX,0,0), Vector2.down, 10);
posDiff = (ray.point - foot.position) * (1 + overShootFactor);  // overShootFactor = 0.8f 预判前冲
endPos  = foot.position + posDiff;
```

**3) 交替步：靠 lerp 进度互锁**
```
thisFootCanMove = (otherFoot.lerp > 1) && (lerp > otherFoot.lerp);
// 即：另一只脚迈完 且 上一步是另一只脚迈的，本脚才能动
// 初始化时把一只脚 lerp 设 0.01 打破对称
```

**4) 抬脚弧线：二次贝塞尔 + 缓动**
```
stepSize = distance(startPos, endPos)
midPos   = startPos + posDiff/2 + (0, stepSize * 0.8f)   // 中点抬高 = 步长×0.8
lerp    += dt * stepSpeed        // stepSpeed = 3f
pos      = QuadraticBezier(startPos, midPos, endPos, ease(lerp))
ease(x)  = 1 / (1 + exp(-10*(x-0.5)))   // sigmoid（≈EaseInOutCubic）
```

**5) 关键常量（可调，一套参数复用多种步态）**
| 参数 | 值 | 含义 |
|------|----|----|
| `footDisplacementOnX` | ±0.25 | 左右脚横向偏移 |
| `stepSpeed` | 3 | 迈步速度（lerp 增速） |
| `overShootFactor` | 0.8 | 落点前冲预判 |
| 抬高系数 | 0.8 × 步长 | 弧线中点抬升 |

> 作者强调：**爬行/追击/恢复等不同步态只改数值、不改代码**——这与 RW"per-creature from scratch 但共享 substrate"一脉相承（§6）。

> Part 1（[S9](https://medium.com/@merxon22/recreating-rainworlds-2d-procedural-animation-part-1-4d882f947e9f)）主要讲**外观**：用 IK 约束摆骨架 + Render Texture/Shader 做像素化 + **把渲染降到 ~12 FPS**（模拟高频、显示低帧，得到 RW 那种"顿感"）。

---

## 6. 各生物差异与共性（子问题 4）

**共性**：所有生物共享 §2–§3 的 chunk/connection/appendage/graphicsModule 基础设施。

**差异（关键设计取向）**：
> "While they share a few base creature scripts in common, for the most part, each is approached like a brand new project and **coded from scratch**. This adds extra development work, but allows for the biodiversity..." —— Videocult, [S3](https://unity.com/blog/exploring-procedural-design-rain-world)

即**没有一个"万能生物生成器"**：Lizard、Vulture、Centipede… 各自手写身体构建 + 运动代码，只共用底座。代价是开发量大，换来"生物多样性"。

同一大类内则靠**数值参数**区分（[S8](https://rainworld.miraheze.org/wiki/Lizards)，蜥蜴示例）：

| 属性 | Green | Blue | White | Red | 含义 |
|------|-------|------|-------|-----|------|
| baseSpeed | 6.7 | 3.2 | 3.8 | 5 | 基础速度 |
| bodyMass | 7.5 | 1.4 | 2.1 | 3.1 | 体重（影响物理惯性/被撞） |
| 能爬杆 | 否 | 是 | 是 | 是 | locomotion 能力开关 |
| 能爬墙 | 否 | 是 | 是* | 否 | locomotion 能力开关 |
| swimSpeed | 0.6 | 0.35 | 0.25 | 1.9 | 泳速 |
| tongueAttackRange | n/a | 140 | 440 | n/a | 舌（appendage）射程 |

→ 对你们的启示：**共享一套 chunk/约束/IK 底座，per-monster 用"能力开关 + 数值参数 + 少量专属代码"区分**，而不是给每只怪写全套物理。

---

## 7. tick / 确定性 / 性能（子问题 5）—— 移植最该照抄的部分

### 7.1 固定 40 TPS 模拟
> "Rain World typically updates at 40 TPS (ticks per second), or once every 0.025 seconds. Each tick, objects in the game world update, which includes simulating their physics, collision, and AI." —— [S7](https://rainworld.miraheze.org/wiki/Technical_Glossary/Tickrate)
- 来自反编译常量 `RainWorldGame.framesPerSecond = 40`。
- 物理按**固定每-tick 常量**推进（不做 delta 缩放）→ 有利确定性。
- `PhysicalObject.Update` = 每-tick 入口，"每 tick 被调用恰好 40 次，更新每个 chunk、查碰撞…"（[S5](https://rainworldmodding.miraheze.org/wiki/Creating_a_Custom_Object)）。
- 注意：40 是名义值——慢动作会主动降 tick（Echoes 附近 ~30、虚空熔化最低 15），CPU 过载会掉帧，故用词"typically"。这些**只放慢模拟时间，不改固定步长模型**。

### 7.2 渲染与模拟解耦 + 双-tick 插值（timeStacker）
> "unlike physics, which is updated strictly 40 times per second, graphics are updated as quickly as possible. This is exactly what timeStacker was created for... Without the timeStacker, the object may move a little jerkily." —— [S5](https://rainworldmodding.miraheze.org/wiki/Creating_a_Custom_Object)

每个 chunk 存 `pos`（当前 tick）与 `lastPos`（上一 tick）；`timeStacker` = 累积真实时间占 25ms 的比例；`RawUpdate` 每绘制帧最多跑 3 次 `Update`。渲染时的经典写法（[timeStacker 页](https://rainworldmodding.miraheze.org/wiki/User:Alphappy/Live/Rendering/timeStacker)）：
```csharp
drawPos = Vector2.Lerp(chunk.lastPos, chunk.pos, timeStacker);          // 位置线性插值
drawRot = Vector3.Slerp(lastRotation, rotation, timeStacker);           // 旋转球面插值
```
这就是教科书式的 **fixed timestep + interpolation**（对照 [gafferongames](https://gafferongames.com/post/fix_your_timestep/)）。

### 7.3 抽象/实体 LOD（性能核心）
见 §2：远处房间只跑抽象 AI/物理，进入视野才"realize"成完整 Creature。**这是 RW 能"整张地图的生态同时活着"的根本。**

---

## 8. 对 `random-room-runtime`（Godot / C#）的落地建议

按优先级（都与引擎无关或有 Godot 直接对应）：

1. **先落固定步长 + 插值（§7.2）——收益最大、风险最低。**
   模拟放进固定累加器循环（Godot `_physics_process` 或自管累加器，步长 0.025s），每个 chunk 存 `Pos/LastPos`，渲染在 `_Process` 里按累加器余数做 `Lerp/Slerp`。这一步就能让现有怪物动作立刻"顺滑且确定"。

2. **把怪物身体重构为 chunk + 距离约束（§3）。**
   小怪 2–3 个圆 chunk 起步（对标 slugcat 2 chunk）；用 Verlet + 约束松弛（§3.4 配方）。Godot 里可脱离内置 `RigidBody`，自管点+约束，避免物理引擎不确定性。

3. **腿部 IK 直接抄 merxon22 算法（§5.B）。**
   向下 raycast（Godot `PhysicsRayQueryParameters2D/3D`）落点 + 重心-支撑判定迈步 + 贝塞尔抬脚弧 + sigmoid 缓动。参数先用它给的常量（stepSpeed 3、overShoot 0.8、抬高 0.8×步长），再调手感。

4. **渲染用可写顶点的自管网格（§9 结论）。**
   Godot 用 `ArrayMesh`/`ImmediateMesh`（`MeshInstance3D`）或 2D 的 `MeshInstance2D`，每帧写 顶点+UV+顶点色，**不需要法线**（RW 的 Futile 网格就只有 vertices/triangles/uvs/colors）。

5. **三层身体模型（§3.3）指导"哪些部件进物理"。**
   核心 = chunk（进物理）；细长易挂地形的部件（尾/触手）= appendage（只撞地形）；纯装饰 = 图形层。别把所有部件都塞进物理。

6. **per-monster = 能力开关 + 数值 + 少量专属代码（§6），别追求万能生成器。**

7. **抽象/实体 LOD（§7.3）**：你们已有多楼层/多房间地图，天然适合"只模拟当前+邻近房间的怪"。

---

## 9. 渲染层细节（子问题 3）

- `TriangleMesh`（通用可变形三角网格）和 `CustomFSprite`（4 顶点变体）**都是 Joar 为 Rain World 手写的，不属于 Futile 框架**（[S6](https://rainworldmodding.miraheze.org/wiki/Futile)：*"Contrary to popular belief, TriangleMesh is not part of Futile... a creation of Joar. CustomFSprite... four vertices. Also a Rain World creation."*）。
- 已回 [MattRix/Futile GitHub 源码](https://github.com/MattRix/Futile) 核对：Futile 自带的是 `FMeshNode`/`FSprite` 等（全 `F` 前缀），**确无** `TriangleMesh`/`CustomFSprite`。
- Futile 的批渲染网格（`FFacetRenderLayer.cs` 24–27 行）只暴露四个数组：`Vector3[] _vertices`、`int[] _triangles`、`Vector2[] _uvs`、`Color[] _colors`——**没有 normals**。
- 因此变形 = **每帧直接改写 顶点位置 / UV / 顶点色** 三组数组。手绘 sprite 贴在可变形网格上，随骨架点集拉伸变形。

---

## 10. 未解问题（下一步取证清单）

1. **腿/脚/尾的真实实现**：是额外 chunk、独立约束链，还是纯 IK 图形层？贴地用 raycast / 就近吸附 / 抓取锚定？—— 反编译 `LizardGraphics`、`Limb`、`GenericBodyPart`。
2. **locomotion 模式切换**：走/爬/攀壁/游/抓杆由什么状态机 + AI 信号驱动？与共享 chunk 物理如何交互？
3. **各生物具体差异**：蜥蜴脊柱多 chunk + 舌抓、秃鹫翼/飞行 rig、蜈蚣/水蛭链——chunk 数、连接拓扑、网格 rig 各是什么？
4. **BodyChunkConnection 求解器精确参数**：连接类型（Normal/Push/Pull/Rotate）、迭代次数、刚度/阻尼、重力/浮力/摩擦常量——想**数值复刻手感**（而非仅结构）就必须拿到这些。

> 上述都需要在自有游戏副本上跑 dnSpy/ILSpy 反编译（社区一致做法）。若要我基于反编译产物继续整理，请提供 dump 或允许在本机 Godot/工具链里操作。

---

## 11. 反编译实证：真实实现（代码级）

> 来源：本机反编译 Rain World 桌面版 `Assembly-CSharp.dll`（Mono/.NET IL，ilspycmd 10.1.1，2026-07-18）。以下类名、字段、算法均直接来自反编译源码，非社区转述。反编译源码仅本机留存、不入库、不外发；此处是用自己话重述算法 + 少量关键代码片段。

### 11.1 一句话颠覆性结论

**Rain World 蜥蜴 locomotion 的腿不是解析式多关节反向运动学。** `LizardLimb`
在运动层只保存一个追目标的足端粒子：`vel = Lerp(vel, 朝目标方向 * huntSpeed, quickness)`，
够近就吸附。真正的“生物感”来自**踩住不动 → 身体前移 → 脚相对落后 → 超阈值换落点**
的 plant-and-trail 循环，加上足端到身体的可达距离约束。

这不能外推成“Rain World 所有带腿生物都没有关节姿态”。反编译的 `BigSpider`
恰好提供了另一种边界：它仍以单一足端 `Limb` 负责抓地/运动，但图形层从身体根、足端和
上下腿长度派生一个 IK 膝点，再画成两段腿。小型 `Spider` 也用余弦定理解同类弯折。
因此本项目的蜘蛛采用相同分层：**足端粒子是唯一抓地物理点，膝是稳定 pole 驱动的两段 IK
姿态输出，不碰撞、不承力**。

### 11.1a `BigSpider` 对蜘蛛拓扑的直接启发

- 物理身体是两个 BodyChunk 加一条连接。
- 八条腿全部锚在第一节身体，第二节没有腿；这成为本项目
  `spider-small` / `spider-large` 的正式拓扑。
- 原作蜘蛛的移动仍带有显式 AI/格子路径逻辑。本项目只借身体与弯腿分层，不照搬其
  寻路/攀爬状态；地面、斜坡、墙、角落和天花板统一由足端可达命中与抓握法线涌现。
- 为保留自由组合能力，本项目把“两节、腿只挂第一节”视为预设而非控制器限制：
  `SpiderBreedParams` 表达至少两节的有序线性链，每对腿显式指定锚点节；不在本轮表达
  分支或环状身体图。
- 3D 窄墙比 RW 侧视平面多出“端面两侧”的自由度：本项目会沿该腿自身横向反投影
  寻找相邻侧面。左右腿各找本侧；侧面候选只有在法线、本侧关系和 AEP 距离余量都成立时
  才能替换旧支撑候选，仍由真实射线、两段腿可达环和足端目标面接触决定是否抓稳。
  这属于腿级几何采样，不是窄墙 locomotion 状态。

### 11.1b 真实蜘蛛步态对“完整迈步”的补充

Rain World 提供的是身体拓扑、足端粒子与弯腿渲染分层；这次大蜘蛛后腿拖步的修复则补充
参考了真实蜘蛛运动学：

- `Grammostola mollicoma` 的慢速直行呈两组四腿交替：
  `R1/R3/L2/L4 ↔ L1/L3/R2/R4`；快速时仍近似四足交替但时序更可变。
  本项目取其确定性稳定子集，让同一腿对左右反相、相邻腿对交替，而不是让左右腿同步抬起。
- `Cupiennius salei` 的足端轨迹明确区分接触期与向前摆动期；低速时从落地到抬脚、
  从抬脚到再次落地的路程相当。各腿对还有不同的径向工作区，前腿朝前、后腿朝后展开。
  因而修复目标不是单纯增加换步频率，而是让一次换步跨过可测的前向距离，同时保留该腿
  原有的横向/径向通道。
- 实现上把“抬脚时的后极限位置（PEP）→下一落脚前缘（AEP）”视为一轮事务：
  抬脚瞬间冻结世界空间 AEP，摆动期间身体可以继续前进但目标不追着根部移动；抓点越出
  可达环则进入一轮完整 `ReachRecovery`，不在同一 tick 直接重搜并重置摆动。正式品种
  用逐对递减的 `TouchdownLeadRatio`，控制器以最低保留抓地腿数和硬超距许可协调换步。
  这些都是腿级工作区/时序规则，不读取地面、墙或角落类别。
- 急转暴露了世界抓点与身体局部工作区之间的必要边界：接触期足端继续踩住旧点，身体转过
  它是自然的；但下一次抬腿不能原样继承已经跨过新局部中线的横向分量。实现只在捕获新 AEP
  的那个 tick，把同一支撑面上的跨线分量镜像回本腿一侧。后续量化又发现，镜像符号会保留
  `0.06` 腿长左右的过窄幅值，并被以后每一步永久复制；因此同面腿对还会把横向 AEP 向
  `MaxReach × Lerp(0.68,0.82,StepLength) × DesiredReachDirection·outward` 软回收 60%。
  这不是固定站宽，而是复用各腿原有扇角、横向权重与体型得到自己的自然工作区。回收只发生
  在正常抬腿的一个 tick；本腿、配对腿与当前支撑的法线必须互相一致，所以多面抱墙的腿仍
  服从真实表面；不需要转向状态、强制松脚或每 tick 改写世界 AEP。

参考：

- Biancardi et al., [Biomechanics of octopedal locomotion](https://doi.org/10.1242/jeb.057471)
- Weihmann, [Crawling at High Speeds](https://doi.org/10.1371/journal.pone.0065788)
- Weihmann et al., [Hydraulic leg extension is not necessarily the main drive in large spiders](https://doi.org/10.1242/jeb.054585)

### 11.2 身体部件的类层次（图形层，独立于物理 chunk）

腿/尾等在 `graphicsModule` 里，基类是 `BodyPart`（**注意：不是** BodyChunk；chunk 是物理层，BodyPart 是图形层的受力点）：

```
BodyPart（图形层受力点：pos/lastPos/vel/rad/surfaceFric/airFriction/terrainContact）
  ├── GenericBodyPart   // 最简：只积分 + 出地形（松垂小部件）
  ├── Limb              // 会追目标 + 找抓点的 IK 肢
  │     └── LizardLimb  // 蜥蜴腿：plant-and-trail 步态 + 成对
  └── TailSegment       // Verlet 距离约束链（尾巴）
```

`BodyPart` 由 `GraphicsModule owner` 持有（`owner.owner` 才是 `PhysicalObject`）。这解释了为什么"四肢纯装饰"被反驳、但又不是完整物理 chunk：**它们是图形层的独立受力点，会和地形碰撞（`PushOutOfTerrain`），但不参与 chunk 间物理。**

### 11.3 `BodyPart` 的三个关键方法（可直接照抄到 Godot）

**① `ConnectToPoint(pnt, connectionRad, push, elasticMovement, hostVel, adaptVel, exaggerateVel)`** —— 弹簧/距离约束：
```
若 elasticMovement>0: vel += 朝pnt方向 * (距离 * elasticMovement)   // 弹性拉拽
vel += hostVel * exaggerateVel
若 push 或 距离>connectionRad:                                       // 硬距离约束
	修正 = 朝pnt方向 * (connectionRad - 距离)
	pos -= 修正 ; vel -= 修正
vel = (vel - hostVel) * (1-adaptVel) + hostVel                      // 向宿主速度靠拢
```

**② `PushOutOfTerrain(room, basePoint)`** —— 脚 vs 地形（贴地核心）：
- 先 `terrain.TrySnapToTerrain(pos, rad, ...)`：贴到地形表面，`vel.y=0`，`vel.x *= surfaceFric`（摩擦），`terrainContact=true`。
- 再扫周围 **9 格**（8 方向+自身）：对 Solid 格解穿透（分别处理 x/y 面）、对 Slope 格按 4 种斜坡方向解穿透。任何一次接触都置 `terrainContact=true`。
- `terrainContact` 就是"这只脚是否踩实"的判据。

**③ `OnOtherSideOfTerrain(conPos, minRadius)`** —— 判断该部件是否被墙挡在另一侧（防止腿穿墙）。

### 11.4 `Limb`：单点追目标的"IK" + 落点搜索

**模式枚举**（`Limb.Mode : ExtEnum`）：`HuntRelativePosition`（追身体相对静止姿势）/ `HuntAbsolutePosition`（追固定世界落点）/ `Retracted`（收回贴 chunk）/ `Dangle`（松垂受重力）。

**`Limb.Update()` 的"IK"**（核心就这几行）：
```csharp
// 相对模式：目标 = 髋点 + 按身体朝向旋转的相对偏移
absoluteHuntPos = connection.pos + RotateAroundOrigo(relativeHuntPos, 身体朝向角);

if (够近 huntSpeed) { vel = absoluteHuntPos - pos; reachedSnapPosition = true; }   // 吸附
else { vel = Lerp(vel, DirVec(pos, absoluteHuntPos) * huntSpeed, quickness);      // 追
	   reachedSnapPosition = false; }
pos += vel; vel *= airFriction;
if (pushOutOfTerrain) PushOutOfTerrain(room, connection.pos);
```
- `huntSpeed` = 每 tick 最大逼近速度；`quickness` = 速度插值系数（越大越"急促"）。**没有雅可比、没有 CCD/FABRIK。**

**`Limb.FindGrip(room, attachedPos, searchFromPos, maxRadius, goalPos, forbiddenX, forbiddenY, behindWalls)`** —— 找脚落点：
- 在 `searchFromPos` 周围 **9 格**里，对每种地形取候选抓点：**Solid** → 贴其暴露面；**Floor** → 格顶（`+10`）；**Slope** → 斜面上的点；**横/竖梁** → 梁上；`behindWalls` 为真时（爬墙蜥蜴）连**背景墙**都能抓。
- 再用 `terrain.SnapToTerrain` 在 `goalPos ± 20`（步长 5）扫一遍。
- 选**离 goalPos 最近**且**在 `maxRadius`（够得着）内**的候选 → `mode=HuntAbsolutePosition; absoluteHuntPos=抓点; GrabbedTerrain()`。

### 11.4b 地形的两套并行表示：**方块格 + 有机样条**（`FindGrip` 同时吃这两层）

脚落点搜索（§11.4）其实同时查询**两种完全不同的地形表示**：

**① 方块格（`Room.Tile`，20×20 像素）** —— 关卡的骨架。每格类型 Solid/Floor/Slope/Air + 横竖梁/背景墙标志位。`FindGrip` 的"9 格邻域搜索"查的就是这层。墙、天花板、杆子这些**竖直/离散结构**靠它。

**② 有机样条（`TerrainManager` → `TerrainCurve`）** —— 平滑起伏的地面。这才是 `room.terrain.SnapToTerrain` 查的那层。数据结构：
- 关卡作者摆几个 **`Handle`**（控制点：Left/Middle/Right + Height），`Handle.Sample()` 做**三次样条插值** → 一条平滑曲线 `y = f(x)`。
- 运行时把曲线**离散成 ~5px 一段的 `collisionPoints[]`**（线段带）。
- `startX/endX/bottom` 界定横向范围与下方填充高度。

**`TerrainCurve.SnapToTerrain(center, radius, out normal)` = 竖直投影**（约 164–197 行）：
```
找 center.x 附近的曲线段 → 把半径为 radius 的圆"搁"在最高的那段线上
  （num3 = 线段高度 + sqrt(radius² - Δx²)，即圆贴合线段的抬升量）
center.y = 该高度        // ← 只改 y！
normal   = 该线段的垂线   // 返回地表朝向
```
**它只改 y、不改 x** —— 本质是"把脚**竖直**放到平滑地面上，返回落点高度 + 地表法线"，就像**放一根铅垂线让脚落到起伏的地上**。因此单条 `TerrainCurve` 是**高度场**（每个 x 只有一个地面高度，做不了悬垂/洞穴——那些交给方块格的墙/天花板）。

另有 `TerrainCurve.Cast(center, radius, endX, out normal)` = **水平扫掠圆射线**（沿 x 逐段步进找第一个撞点），用于横向移动碰撞——**这才是真正的"射线"**，而 `SnapToTerrain` 不是射线，是竖直投影。

**两层如何联动**：样条会把自己**烙进方块格**——`TerrainCurve.ObstructsTile(x,y)`：若某 20px 格范围内曲线高于格顶 → 该格算 Solid；`TerrainManager.TileAccessibility` 据此把"曲线正下方的格"标成 **`CurvedFloor`**（还记得 §11.6 蜥蜴对 CurvedFloor 的特判吗？就是从这来的）。于是 **AI 寻路看到的是粗方块，脚落地时享受的是平滑曲线**。

> 对你的答疑收束：**没有"预埋抓握点"**。方块格的材质、有机地形的样条 Handle，都是**静态作者数据**；但**具体落脚坐标是运行时算的**——要么在 9 格邻域按材质几何现算，要么把脚竖直投影到样条曲线上。两套表示，一套"就地算点"的取点逻辑。

### 11.5 `LizardLimb`：plant-and-trail 步态（真实的"走路"）

- **数量与配对**：正常 **4 条腿**（Caramel 6 条）；构造时 `new LizardLimb(..., (i%2==1) ? limbs[i-1] : null)` —— 奇数腿链接到前一条偶数腿，**成对**。
- **腿长约束**：每 tick `ConnectToPoint(connection.pos, jointDist, ...)`，`jointDist = 25 * (bodySizeFac+1)/2`（≈25px）。
- **步态循环**（`LizardLimb.Update` 精华）：
  1. **落后度** `num = DistanceToLine(脚, 髋点, 髋点+垂直方向)`：脚相对髋部前后的有符号距离。
  2. **未迈步时**（`!reachingForTerrain`）：脚保持在髋前方的静止姿势位；一旦 `num < jointDist * (-StepLength)`（脚拖到身后超阈值）→ **`reachingForTerrain = true`（触发迈步）**。`StepLength = Lerp(-0.5, 0.5, stepLength*health)`。
  3. **迈步中**：若还没够到目标 → `FindGrip(..., 髋点, jointDist-1, 髋点 + 反向偏移*50, behindWalls=IsWallClimber)` 搜新落点（带 `legPairDisplacement`、`feetDown` 偏移）；够到后播放抓地音效、`gripCounter++`，直到再次拖后 → 松开重来。
  4. **成对腿互斥**：两腿若近于 `rad*3` 互相推开。
  5. **步态协调**：`smoothenLegMovement` 时按各腿 `gripCounter` 错开抬脚时机。
- **模式切换（就在腿这一层）**：
  - `swim > 0.5`（在水里）或受击晕 → **`mode = Dangle`**，`vel.y -= 0.9`（腿垂下），不再迈步 —— **这就是"游泳时腿不走"的实现**。
  - 爬墙蜥蜴（`IsWallClimber`）→ `FindGrip` 的 `behindWalls=true`，能抓背景墙 → 自然爬墙。

### 11.6 locomotion "模式"如何表示 —— 两种范式

**A. Slugcat（`Player`）= 显式双枚举状态机**（最清晰，最值得抄给"玩家角色"）：
- `Player.BodyModeIndex`：`Default / Crawl / Stand / CorridorClimb / ClimbIntoShortCut / WallClimb / ClimbingOnBeam / Swimming / ZeroG / Stunned / Dead` —— 物理行为模式。
- `Player.AnimationIndex`：26 个细分动画态（`CrawlTurn / DownOnFours / LedgeGrab / HangFromBeam / ClimbOnBeam / SurfaceSwim / DeepSwim / Roll / BellySlide / …`）。
- 每 tick 由 `MovementUpdate` 按环境（水/梁/走廊/墙）切 `bodyMode`，再按细节切 `animation`。

**B. Lizard（`Lizard`）= 无枚举，纯参数 + 寻路 + 程序化腿自适应**（怪物的做法）：
- `AI.runSpeed`（0~1，AI 想多快）；`followingConnection` = 寻路给的下一段；`limbsAimFor = room.MiddleOfTile(followingConnection.destinationCoord)`（腿朝下一个路径格瞄）。
- `swim`（0~1 浮点）：进水抬高，`>0.5` 腿 Dangle、改用 `swimSpeed` 推身体 chunk。
- 读地图 **`AItile.Accessibility`**：`Floor / CurvedFloor / Corridor / Climb / Wall / Ceiling / Air / Solid`（`walkable = 非Air且非Solid`）—— **地图逐格声明"这里能怎么走"**，蜥蜴据此施力，腿部程序化贴合地形。
- **结论**：怪物没有"走/爬/游"硬状态机，模式是**寻路可达性 + 施力 + 腿部 FindGrip/Dangle 自适应**涌现出来的。

### 11.6b 身子为什么不从墙/天花板掉下来：**重力开关**（已确证）

> 这一节补上此前"未验证的尾巴"。结论：**没有专门的"吸墙力"，重力本身是被开关的。**

蜥蜴的 `base.gravity` **不是常量**，每 tick 在两档之间切（`Lizard` 移动块，约 1001–1016 行）：

```csharp
if (applyGravity) {                 // 该掉的时候
	base.gravity = 0.9f;
	base.airFriction = 0.999f; surfaceFriction = 0.3f;
} else {                            // 抓着可达地形的时候
	base.gravity = 0f;             // ← 重力直接关成 0！
	base.airFriction = 0.8f;  surfaceFriction = 0.5f;
	base.GoThroughFloors = true;
}
```

开关条件（约 1778 行）：
```csharp
applyGravity = inAllowedTerrainCounter < lizardParams.regainFootingCounter   // 最近没待在"能待的地形"里
			 || NoGripCounter > 10                                            // 连续 >10 tick 没有腿抓住
			 || commitedToDropConnection != default;                          // 主动决定要往下掉
```

翻译：**只要它在"能待的地形"上、且有腿抓着（`NoGripCounter<=10`），重力就被关成 0**——所以它不会从墙上/天花板上掉下来，**因为此刻根本没有重力在拽它**。一旦失去抓握（>10 tick 没腿抓地）、离开可达地形、或主动要掉，`applyGravity` 变真，重力恢复 0.9，它就正常坠落。

**关键点：这跟"走 vs 爬"无关，是"抓住 vs 没抓住"。** 地板、墙、天花板在身体这一层是同一回事——只要脚抓着可达地形，重力就 0。走和爬在身体层也是**统一**的；唯一的身体级开关是"我此刻抓着东西没有"，与地面朝向正交。

### 11.6c 腿的抓握既是"锚"也是"引擎"：力 ∝ `LegsGripping`

`LegsGripping = (graphicsModule as LizardGraphics).legsGrabbing`（图形层实际抓地的腿数）**反馈回身体物理**，身体的移动力**正比于抓地腿数**（约 2449、2484–2495 行）：

```csharp
base.mainBodyChunk.vel += moveDir * (4f * LegsGripping);   // 抓的腿越多，推得越有力
base.bodyChunks[1].vel  -= moveDir * (2f * LegsGripping);
```

且 `LegsGripping <= 0` 时移动逻辑直接 return（约 1109 行）——**没腿抓地就使不上劲**。于是形成闭环：
- 腿去抓地形（§11.5）→ `legsGrabbing` 上升 → ①让 `applyGravity` 保持假（重力 0，吊在墙上）②给身体提供正比于抓地数的牵引力；
- 抓不到 → `NoGripCounter` 涨 → 重力恢复 → 坠落。

**抓地力 = 抗重力的锚 + 前进的引擎，二合一。** 这也是为什么风阻、被撞飞的抗性都按 `LegsGripping` 缩放（抓得牢就稳，抓得少就容易被吹跑/撞飞）。

### 11.7 `TailSegment`：Verlet 距离约束链

尾巴 = 一串 `TailSegment`，每段把自己拉回前一段 `connectionRad` 距离内：
```
若 距离>connectionRad:
	修正 = 朝前段方向 * (connectionRad-距离)
	本段.pos -= 修正*(1-affectPrevious) ; 前段.pos += 修正*affectPrevious   // 双向约束
	stretched = clamp(...)   // 拉直程度 → 渲染时尾巴视觉拉伸/变细
PushOutOfTerrain(...)
```
参数：`tailSegments`（节数）、`tailStiffness` / `tailStiffnessDecline`（刚度沿尾递减）、`tailLengthFactor`。

### 11.8 关键可调参数清单（`LizardBreedParams`，直接给你调手感）

| 参数 | 作用 |
|------|------|
| `limbSpeed` | 腿逼近落点的最大速度（huntSpeed） |
| `limbQuickness` | 腿速度插值系数（0.1~1，越大越急促） |
| `stepLength` | 触发迈步的落后阈值（越大步子越大） |
| `liftFeet` | 静止姿势脚向髋部收拢程度（抬脚感） |
| `feetDown` | 落点向下偏移（脚更贴地/更外撇） |
| `limbGripDelay` | 判定"已抓稳"所需 gripCounter 帧数 |
| `legPairDisplacement` | 成对腿横向错开量 |
| `smoothenLegMovement` | 是否按 gripCounter 协调多腿步态 |
| `walkBob` | 走路时身体上下颠 |
| `limbSize`/`limbThickness` | 腿粗细（渲染） |
| `bodySizeFac`/`bodyStiffnes` | 体型/身体刚度 |
| `swimSpeed` | 泳速 |
| `tailSegments`/`tailStiffness`/`tailStiffnessDecline`/`tailLengthFactor` | 尾巴节数/刚度/长度 |

### 11.9 移植到 Godot(C#) 的精确配方（怪物腿）

```
每条腿 = 一个点 { pos, lastPos, vel, rad, terrainContact }，锚在身体某个 chunk(hip) 上。
每物理 tick（固定 40Hz，见 §7）：
  1) 计算落后度 num = 脚相对髋部的有符号纵深
  2) 若未迈步且 num < -jointDist*stepLength → 进入"迈步"
  3) 迈步中：raycast/格搜（对标 FindGrip）在前方地形找落点 target，
	 否则 target = 髋前方静止姿势位
  4) IK：vel = Lerp(vel, dir(pos→target)*limbSpeed, limbQuickness)；够近则吸附
  5) pos += vel; vel *= airFriction
  6) 距离约束：把脚拉回 hip 的 jointDist 半径内（对标 ConnectToPoint）
  7) 出地形：贴地 + 解穿透，置 terrainContact（对标 PushOutOfTerrain）
  8) 成对腿：过近则互推
  9) 若在水/晕 → 不迈步，vel.y -= 重力（Dangle）
渲染：脚位置 lastPos→pos 用 timeStacker 插值（见 §7.2）
```
Godot 对应：`PhysicsRayQueryParameters2D/3D` 做落点搜索；点+约束自管（不用 RigidBody，保确定性）；腿的渲染用可写顶点网格（见 §9）。**"走/爬/游"不必做硬状态机**——用"可达性地图 + 施力 + 腿 FindGrip/Dangle 自适应"，模式会自己涌现；玩家角色若要精确手感则学 Player 的 BodyMode 枚举。

---

## 12. 移植策略：为什么用射线，而不是细网格

> 本节回答一个关键移植疑问：Rain World 用 20px 细网格，而 `random-room-runtime` 的网格粒度非常大（房间级）。移植是要**拆细网格**，还是**自己打射线**？结论：**打射线，且完全不用拆网格。**

### 12.1 网格大小是"假问题"

Rain World 用 20px 细网格，**不是腿部算法的本质需求，而是它"2D + 几乎不用引擎物理"的补偿**：Joar 必须从像素/格子里**手工重建一个可查询的碰撞世界**（§11.4 的 9 格邻域搜索、§11.4b 的样条 `collisionPoints`），脚才有东西可查。**细网格 = 它表示碰撞的手段。**

`random-room-runtime` 是 **Godot 3D 白盒地图**，**已经有真正的碰撞几何**（地板/墙/楼梯都是实打实的 3D collider）。也就是说，雨世界费劲手搓出来的"可查询碰撞世界"，你们**天生就有**。所以：

> **不用拆网格。脚落点用射线打你们真实的 3D collider —— 这就是 `SnapToTerrain`/`FindGrip` 在 Godot 里最干净的对应物。**
> - RW 的 `SnapToTerrain`（竖直投影）→ Godot 里字面就是一根**向下的 `intersect_ray`**。
> - RW 的 `Cast`（水平扫掠射线）→ 一根**横向的 `intersect_ray`**。
> - Godot 物理引擎免费返回命中点 + 法线。**这一步比雨世界更简单，不是更难。**

### 12.2 两个"网格"必须拆开（RW 自己也是拆开的）

| 层 | 作用 | 移植做法 | RW 对应 |
|----|------|----------|---------|
| **寻路网格**（粗、离散） | "怪能走到哪个房间/格" | **保持现有粗网格**，够用；本就该粗 | `AItile`（可达性） |
| **脚落点**（连续、浮点） | "脚具体踩在哪个坐标" | **射线打真实 collider**，连续精度，**不碰网格** | `Room.Tile` + `terrain`（碰撞） |

RW 里这两层恰好都是 20px，只是因为游戏尺度小；本质是**两套独立表示**。你们这边：寻路层 = 逻辑房间格，碰撞层 = Godot 物理世界，**用不同分辨率天经地义**。**腿活在连续空间里，从不需要寻路网格的分辨率。**

### 12.3 Godot 落地（接到 §11.5 的 plant-and-trail）

脚只在**迈步那一刻**才找落点（平时踩住不动）：
```
1. 算脚"想去"的目标点（髋部前方一点，body-relative）
2. 从目标点朝下(或朝墙面法线方向)打一根短射线 PhysicsRayQueryParameters3D
3. 命中 collider → 取 position + normal；在腿长 jointDist 内 → 踩这, terrainContact=true
			  → 打空 → 这只脚这步没得踩(对应 RW 抓空)
4. 腿长约束 + IK 收敛(§11.5/§11.9)
```
- **爬墙 = 把射线方向从"朝下"换成"朝墙面"，逻辑一模一样**（呼应 §11.4/§11.6b：走爬无分支）。
- **身子不掉墙**：沿用 §11.6b 的重力开关（抓着 collider→重力 0；抓空累计→重力回归）。

### 12.4 成本与缓解（迟早会遇到）

射线不是免费的：`N 怪 × 4~6 腿 × 40 tick/s` 比 RW 的"数组查格 O(1)"贵。两个天然省法你们已具备：
- **plant-and-trail 本身省**：脚**大部分时间踩住不动**，只有迈步瞬间打射线 → 真实射线数远小于"每腿每帧一根"。
- **abstract/realized LOD（§7.3）**：只给当前/邻近房间的怪跑完整腿部射线，远处降级或不跑。

**白盒优势**：你们地板基本是平的，比雨世界的有机起伏地形**更好打射线、落点更可预测**——移植处境比雨世界更舒服。

### 12.5 一句话结论

**网格大小是假问题。不拆网格，也不重造 RW 的格子碰撞——直接用 Godot 射线打已有的 3D collider，就是地形/落点逻辑最干净的移植。粗网格留给寻路，脚落点走连续射线，两者解耦。**

---

## 附：核心事实速查

| 事实 | 值/结论 | 置信 |
|------|---------|------|
| 物理时间步 | 固定 40 TPS = 0.025s；慢动作可降至 15–30 | 高 |
| 渲染 | 与模拟解耦，`Lerp`(pos)/`Slerp`(rot) 双-tick 插值（timeStacker） | 高 |
| 身体单元 | `BodyChunk`（圆，owner/index/pos/半径px/质量），3 碰撞层 | 高 |
| 连接 | `BodyChunkConnection[]` 距离约束（软弹簧，多模式） | 高 |
| 积分 | Verlet 式：forcing velocity(`vel`) + 约束松弛 | 高 |
| slugcat | 恰好 2 chunk 锁定 → 可翻滚 | 高 |
| 附肢 | `appendages`：撞地形/被武器击中，不撞 chunk（`IHaveAppendages`） | 中高 |
| 图形层 | `graphicsModule`（生物有、武器无）；`TriangleMesh`/`CustomFSprite` 手写、非 Futile；网格无法线 | 高 |
| 动画 | 无关键帧；点+距离约束即"帧" | 高（一手） |
| per-creature | 各自 from scratch，共享少量底座；同类靠数值参数区分 | 高 |
| LOD | abstract（廉价）↔ realized（完整物理）两级 | 高 |
| 腿 IK/贴地/步态切换（RW 内部） | **已反编译确证，见 §11**：蜥蜴运动层为单足端 + FindGrip + plant-and-trail；BigSpider/Spider 另在图形层由足端派生两段弯腿 | 高（本机反编译源码） |
| 可移植 IK 方案（merxon22） | raycast 落点 + 重心平衡迈步 + 贝塞尔抬脚 + sigmoid 缓动（含常量） | 中（第三方复刻，代码可跑） |
