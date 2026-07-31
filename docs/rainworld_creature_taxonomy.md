# 雨世界生物分类学（反编译实证）

> **用途**：给 `proc_anim_lab` 一张"原作到底有多少种身体架构"的地图。本项目的蜥蜴品种表（[`BreedParams`](../core/BreedParams.cs)）只覆盖蜥蜴一支的形态空间，其余并列后端各有自己的参数表（`SpiderBreedParams`/`CentipedeParams`/`CicadaParams`/`VultureBreedParams`/`HumanoidParams`/`TentaclePlantParams`，互不混表）；这份文档回答"再往下扩会撞到哪些已有先例"。
>
> **证据来源**：`~/workspace/others/Managed_extracted/Assembly-CSharp.dll`（用户自有 Rain World 桌面副本，含 Downpour/MSC + **Watcher** DLC）。整程序集反编译后逐类统计：
> ```bash
> export PATH="$PATH:$HOME/.dotnet/tools"
> ilspycmd ~/workspace/others/Managed_extracted/Assembly-CSharp.dll -p -o <某个仓库外目录>
> ```
> ⚠️ 反编译产物**仅本机学习参考，不得进仓库、不得再分发**（同 [`../CLAUDE.md`](../CLAUDE.md) §3.5）。本文件只记录**结论与结构统计**，不含原作代码片段。
>
> 统计口径：类继承关系由 `class X : Y` 全量提取后建树；身体规格由每个生物类构造函数里的 `bodyChunks = new BodyChunk[...]` / `bodyChunkConnections = new BodyChunkConnection[...]` 提取；肢体由 `new <XxxLimb|Tentacle|TailSegment|GenericBodyPart>(` 提取（生物类自身与对应 `*Graphics` 类分别统计）。

---

## 1. 代码里有三套**正交**的分类轴

| 轴 | 载体 | 决定什么 |
|---|---|---|
| **物种枚举** | `CreatureTemplate.Type`（`ExtEnum`，DLC/Mod 可扩展） | 身份、生成、生物间关系 |
| **模板继承树** | `CreatureTemplate.ancestor` | AI 关系与寻路代价的继承（子模板只覆盖差异字段） |
| **实现类树** | `Creature` 的子类 | **身体构造与运动实现** ← 对本项目唯一重要的一轴 |

三者不重合：**92 个物种枚举 → 54 个 `Creature` 实现类**。差额来自"一个类 + 一张品种参数表"的模式——最典型的就是 `Lizard`：**17 个蜥蜴品种共用同一个 `Lizard` 类**，全部差异走 `LizardBreeds.BreedTemplate()` 产出的 `LizardBreedParams`。这正是本项目 `BreedParams` + `BodyFactory` 结构的原型。

> 蜥蜴 17 品种：Pink / Green / Blue / Yellow / White / Red / Black / Salamander / Cyan（本体 9）+ Spit / Eel / Zoop / Train（MSC/Downpour 4）+ Blizzard / Basilisk / Indigo / Peach（Watcher 4）。

### 物种枚举总量

| 来源 | 数量 | 备注 |
|---|---|---|
| `CreatureTemplate.Type`（本体） | **47** | 含两个抽象模板 `StandardGroundCreature`、`LizardTemplate` |
| `DLCSharedEnums.CreatureTemplateType` | **13** | MirosVulture / SpitLizard / EelLizard / MotherSpider / TerrorLongLegs / AquaCenti / StowawayBug / ScavengerElite / Inspector / Yeek / BigJelly / JungleLeech / ZoopLizard |
| `MoreSlugcatsEnums.CreatureTemplateType` | **5** | HunterDaddy / FireBug / SlugNPC / ScavengerKing / TrainLizard |
| `WatcherEnums.CreatureTemplateType` | **27** | DrillCrab / TowerCrab / Barnacle / SandGrub / BigSandGrub / BigMoth / SmallMoth / BoxWorm / FireSprite / Rattler / SkyWhale / ScavengerTemplar / ScavengerDisciple / Loach / RotLoach / BlizzardLizard / BasiliskLizard / IndigoLizard / PeachLizard / Rat / Frog / Tardigrade / GrappleSnake / Millipede / Angler / RippleSpider / MothGrub |
| **合计** | **92** | |

---

## 2. 实现类树（`Creature` 全部子类）

```
Creature
├─ AirBreatherCreature              ← 唯一的中间基类之一：有呼吸/溺水逻辑
│  ├─ Lizard   Vulture   Scavenger   LanternMouse
│  ├─ Loach   DrillCrab   GrappleSnake   Yeek
│  └─ InsectoidCreature             ← 虫类中间基类
│     ├─ Centipede  Cicada  BigSpider  DropBug  EggBug
│     ├─ Frog  Millipede  MothGrub  BigMoth  Barnacle  StowawayBug
│     └─ NeedleWorm → BigNeedleWorm / SmallNeedleWorm
└─ 直接继承 Creature（27 个）
   Player  Deer  DaddyLongLegs  MirosBird  TempleGuard  BigEel  JetFish
   Leech  Angler  BigJellyFish  SkyWhale  Spider  Snail  Rat  Rattler
   Tardigrade  RippleSpider  SandGrub  TentaclePlant  PoleMimic  BoxWorm
   GarbageWorm  TubeWorm  Fly  Hazer  VultureGrub  Overseer  FireSprite  Inspector
```

**结构性观察**：继承树里**没有**"四足 / 飞行 / 水生"这类分类基类。飞不飞、游不游是模板上的 `canFly` / `canSwim` / `waterRelationship` 字段 + 各生物 `Update()` 里各写各的实现。唯二的中间基类只区分"要不要呼吸"和"是不是虫"。

> 对本项目的意义：原作也没有把 locomotion 模式做成类层级——这与 CLAUDE.md §2.6「模式靠涌现、不做状态机分支」的路线一致，不是我们的简化。

### 肢体侧（`BodyPart` 子类树 + 独立 `Tentacle` 系统）

```
BodyPart
├─ Limb ──── LizardLimb   MillipedeLimb   TardigradeLimb
│            ScavengerHand   ScavengerLeg   SlugcatHand
├─ TailSegment    GenericBodyPart    Fin    DanglerSegment
└─ LizardScale    AxolotlScale    VultureAppendage    VultureFeather

Tentacle                         ← 独立类，不继承 BodyPart / Limb
├─ DaddyTentacle   DeerTentacle   VultureTentacle
└─ LoachLeg        LoachTentacle
```

**关键事实：当前 DLL 中 `Tentacle` 是独立类，不继承 `BodyPart` 或 `Limb`。** 它自己持有
`tChunks[]`、路径/地形回溯状态和一组 `TentacleProps`（`stiff` / `rope` / `shorten` +
目标吸引、沿段对齐、回溯与穿地容错参数）。`DaddyTentacle`、`DeerTentacle`、
`VultureTentacle`、`LoachLeg`、`LoachTentacle` 才是在这套独立段链之上的专用子类。
因此本项目扩展多节触手时应新建并列的段链原语，不能把现有单粒子 `Limb.cs` 当作原作继承依据。

---

## 3. 七大身体架构（按构造函数统计分组）

### A. 珠链 + 显式腿 —— 本项目 M1~M4 已覆盖的形态

| 生物 | chunks | connections | 肢体 |
|---|---|---|---|
| **Lizard** | 3 | 3（含防折叠 push-only） | 4×`LizardLimb`（在 `Lizard` 本体）+ `TailSegment` 链 + `Antennae`（Graphics） |
| **Scavenger** | 3 | 2 | 2×`ScavengerLeg` + 2×`ScavengerHand` + `TailSegment` |
| **Deer** | 6 | 5 | 4×`DeerTentacle`——腿是**多节触手**，前两条挂 `bodyChunks[1]`、后两条挂 `bodyChunks[2]` |
| **BigSpider / SpitterSpider** | 2 | 1 | `Limb` + `GenericBodyPart`（Graphics） |
| **DropBug** | 3 | 3 | `Limb` |
| **Rat**（Watcher） | 2 | 1 | `Limb` + `TailSegment` |
| **Frog**（Watcher） | 2 | 1 | `Limb` |
| **Tardigrade**（Watcher） | 4 | 5 | `TardigradeLimb` |
| **Millipede**（Watcher） | 15 | `n-1` 链式 | `MillipedeLimb` |
| **Yeek**（MSC） | 2 | 1 | `YeekLeg` |
| **Snail** | 2 | 2 | `Limb` + `GenericBodyPart` |
| **Cicada / EggBug / Barnacle** | 2 | 1 | `Limb` |

`Deer`（6 节 + 4 条多节腿）是原作里"长脊柱 + 长腿"的唯一样本，最接近本项目 `heavy` 想去的方向。

### B. 单点身体 + 纯图形腿（腿完全不进物理）

- **Spider**：1 chunk / 0 connection；`SpiderGraphics` 里 8 条 `Limb`（`limbs[4,2]`）**全部挂在同一个 `mainBodyChunk` 上**。
- **Fly / Leech / Overseer / FireSprite / Inspector**：1 chunk、0 connection。Overseer 连腿都没有。

> **腿数与身体物理复杂度彻底解耦**——这是原作最反直觉也最省成本的一条设计。本项目 `SpineSegments` 与 `LegPairs` 互相独立正是同一原则。

### C. 全连接珠团（`n(n-1)/2` 条约束）

| 生物 | chunks | connections |
|---|---|---|
| **Centipede** | `Red ? 18 : (Small ? 5 : Lerp(7,17,size))` | 全对全 |
| **DaddyLongLegs / BrotherLongLegs** | `Random.Range(4, …)` | 全对全 + `DaddyTentacle[4]`（Brother）或 `[Random.Range(5,…)]`，触手绕圆周均布 |
| **TempleGuard** | 5（中心 r=17.5 质量 4，四角 r=9.5 各 10/5） | 全对全 = **刚性板块** |
| **NeedleWorm** | `small ? 3 : 5` | 全对全 |
| **MirosBird** | `list.Count + 1` | 全对全 + `BirdLeg[2]` + `neck = Tentacle(…, 60f)` |

全对全适用于节数少（≤5）或本来就该"一坨互斥球"的软体。节数一多约束数量爆炸，所以长条生物走 D。

### D. 长链条（`n-1` 或稀疏斜撑）

| 生物 | chunks | connections |
|---|---|---|
| **BigEel** | 20 | `n-1` 链式；模板 `bodySize = 100` |
| **GrappleSnake**（Watcher） | `Random.Range(8,16)`，自定义 `GrappleSnakeBodyChunk`（半径/质量沿链递减） | `n-1` |
| **SkyWhale**（Watcher） | 6 或 10 | **`2n-3`** = 链式 + **每节一根跨节斜撑** |
| **BoxWorm**（Watcher） | `bodyChunkCount` | `n-1` |
| **Loach**（Watcher） | 6 | 5 + `LoachLeg`/`LoachTentacle` |
| **Millipede** | 15 | `n-1` |

> **`SkyWhale` 的 `2n-3` 就是本项目防折叠支柱的稠密版**。我们现在（CLAUDE.md M4 "防折叠支柱"）对每对隔一节 chunk 加 `SoftOnly` PushOnly 连接，是稀疏形态；若将来上 5 节以上脊柱，"每节都撑"在原作里有直接先例。

### E. 飞行类

- **Vulture**：5 chunk（4 个 r=9.5 的躯干 + 1 个 r=6.5 的尾），**7~8 条 connections**（4 条躯干边 + 对角斜撑 + 2 条 `Type.Pull` 拉尾）+ `VultureTentacle[2]`（King/Miros 为 4）抓取 + `VultureAppendage`/`VultureFeather` 翅膀。躯干是"斜撑加固的四边形"，不是链。
- **MirosBird**：见 C（全对全 + 触手脖子）。
- **BigMoth**（Watcher）：5 chunk / 6 connections + `LegGraphics`。
- **Centiwing / CicadaA / CicadaB / BigNeedleWorm**：见 A/C，靠模板 `canFly` 开飞行分支。

模板 `canFly = true` 的全集：Fly、CicadaA、Centiwing、Vulture、KingVulture、MirosVulture、MirosBird、TempleGuard、BigNeedleWorm、BigMoth、FireSprite、SkyWhale、RippleSpider。

### F. 固定 / 半固定（`abstractImmobile`）

- **TentaclePlant**：2 body chunk、**0 connection** + 独立 `Tentacle`。触手理想长度
  `300px`、8 个 `tChunks`，半径从根部 `8px` 递减到末端 `1px`；根 chunk 不碰地形并在
  `Update` 中钉回洞口 `rootPos`。
- **PoleMimic**：同为 2 chunk、0 connection + `Tentacle`，但实现类并不继承
  `TentaclePlant`；只有模板 ancestor 是 TentaclePlant。长度取地图杆路径
  `tilePositions.Length × 40px`，段数等于路径点数，平时会把段钉回伪装杆路径。
- **GarbageWorm**：2 chunk、0 connection + `Tentacle`；长度 `400px × bodySize`，
  段数为 `int(15 × Lerp(bodySize, 1, 0.5))`。它以抬头观察、抓矛和缩洞为主，不复用
  TentaclePlant 的伏击蓄力逻辑。

三者的共性是“宿主本体锚定 + 独立触手段链”，不是共同 locomotion 基类；捕猎、伪装和
交互行为分别留在三个 `Creature` 实现中。

### G. 纯水生

- `waterRelationship == WaterOnly`：BigEel、JetFish、Leech、AquaCenti、BigJelly、Angler。
- `Amphibious`：Slugcat、Snail、Spider、Hazer、DaddyLongLegs、TentaclePlant、Frog、Tardigrade、RippleSpider、JungleLeech、Barnacle。

> 按 [`../CLAUDE.md`](../CLAUDE.md) §4，**游泳/水中运动是本项目明确的非目标**。此节只作分类完整性记录。

---

## 4. 模板参数抽样（值直接取自 `StaticWorld` 的字段赋值）

`–` 表示该模板**未显式赋值、继承自 `ancestor`**（例：`SeaLeech` 只覆盖 `bodySize = 0.15`，其余全走 `Leech`）。

| 物种 | bodySize | canFly | grasps | waterRelationship | meatPoints |
|---|---|---|---|---|---|
| LizardTemplate（17 品种的祖先） | 2.0 | – | 1 | AirAndSurface | – |
| Scavenger | 1.2 | – | **4** | AirAndSurface | 4 |
| Deer | **10** | – | 0 | AirOnly | – |
| DaddyLongLegs | 5.5 | false | 1 | Amphibious | 10 |
| Vulture / KingVulture | 6 / 8.5 | true | 1 | AirAndSurface | 14 / 16 |
| MirosBird | 7 | true | 1 | AirAndSurface | 12 |
| TempleGuard | 7 | true | 1 | AirAndSurface | – |
| BigEel | **100** | – | 1 | WaterOnly | – |
| SkyWhale / Loach | 11 / 11 | true / false | 0 | AirOnly | – |
| DrillCrab | 5 | false | – | AirOnly | 6 |
| Centipede / RedCentipede | 1 / 8.5 | – | 2 | AirAndSurface | 1 |
| Spider / Fly / Leech | 0.1 | – / true / – | 1 | Amph / Air / Water | – |

### 第四条分类轴：可达地形

寻路侧还有一层与身体架构无关的分类：

```
AItile.Accessibility { OffScreen, Floor, CurvedFloor, Corridor, Climb, Wall, Ceiling, Air, Solid, Sand }
```

每个模板带一张 `TileTypeResistance` 表（每种地块的通行代价 + `PathCost.Legality`），`maxAccessibleTerrain` 是从这张表里**推导**出来的——取所有 `Legality.Allowed` 且非 `Sand` 的 accessibility 枚举值的最大者。"哪种生物能去哪"由这张表定义，与它长几条腿无关。

---

## 5. 对本项目的三条结论

1. **腿数 ≠ 身体节数**。Spider 用 1 chunk 挂 8 条腿、Deer 用 6 chunk 挂 4 条多节腿。本项目 `BreedParams` 里 `SpineSegments` / `LegPairs` 相互独立与原作一致；`hexapod`（3 脊柱 × 3 腿对）在原作有 Caramel/SpitLizard 的三锚六腿先例（见 CLAUDE.md「修复轮三」），Deer 的 6:4 则是长脊柱少腿的另一端。

2. **多节身体抗折叠，原作给了两种方案**：节数少走全对全（Centipede / DaddyLongLegs / TempleGuard / NeedleWorm），节数多走链式 + 跨节斜撑（SkyWhale 的 `2n-3`）。我们当前的 `BodyStiffness` PushOnly 支柱是后者的稀疏版；升到 5 节以上脊柱时，"每节都撑"有直接依据。

3. **多节触手应作为独立段链原语**：当前 DLL 的 `Tentacle` 不属于 `BodyPart` / `Limb`
   继承树，而是自带 `tChunks[]`、路径回溯和 `TentacleProps` 的系统；
   `Deer` / `DaddyLongLegs` / `Vulture` 等在其上派生专用触手。对本项目而言，正确边界是
   复用 chunk、地形查询和固定 tick 底座，新增并列 `TentacleChain`，而不是扩充蜥蜴
   plant-and-trail `Limb`。

---

## 6. 复现本文统计

反编译到仓库外目录后：

```bash
grep -rhoE "class [A-Za-z0-9_]+ : [A-Za-z0-9_.]+" --include='*.cs' <反编译目录> | sort -u
```

得到全量继承对，再从 `Creature` / `BodyPart` 建树即为 §2。身体规格来自各生物类构造函数里的 `bodyChunks = new BodyChunk[...]` 与 `bodyChunkConnections = new BodyChunkConnection[...]`（注意：数组长度常是表达式，如 `Red ? 18 : …`、`base.bodyChunks.Length * 2 - 3`，按字面量匹配会漏；`GrappleSnake` 更是先建 `GrappleSnakeBodyChunk[]` 再赋给 `bodyChunks`）。物种枚举来自 `CreatureTemplate.Type` 与三个 DLC 的 `*Enums` 类；模板字段来自 `StaticWorld` 的初始化流程。
