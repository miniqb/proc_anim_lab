# proc_anim_lab —— 3D 程序化生物动画实验室

> Godot 4.x / C# 的独立沙盒项目。**目标：从零实现一套 3D 版「雨世界式」程序化生物动画/运动
> 系统；等它在这里成熟后，整体移植回 [`random-room-runtime`](../random_room/random-room-runtime/)
> 的怪物系统。**
>
> **当前状态（2026-08-08）**：`ProcAnim.Core` 含 **10 个平行物种控制器**
> （Lizard / Humanoid / Spider / Centipede / Cicada / Vulture / TentaclePlant / Deer /
> DaddyLongLegs / DropBug），各有独立回归；七套 Godot 矩阵合计 **170 项**。M5 内核抽离与
> 回迁契约完成；正式渲染层已有 **6 个渲染件**（Lizard / Centipede / Vulture / DaddyLongLegs /
> Spider / Humanoid）。最近一轮：DaddyLongLegs 抓取竞技场 —— 外部目标通道首次接玩家，
> 闭环「追逐 → 触手提前伸抓 → 束缚连打挣脱 → 拖入吞食 → 重开」，内核零改动
> （探索场景，不进矩阵，见 [daddy_long_legs](docs/daddy_long_legs_controller.md) §7.2）。
> 「默认集成姿态」的闭环待主仓接线后验证（契约 §4.1 / §8.3）。

---

## 1. 为什么单独建这个项目

- `random_room` 主项目体量大，直接在里面做程序化动画试验**容易污染环境、拖慢迭代**。
- 这里**只关注一件事**：怪物的身体怎么被程序化地驱动运动。白盒测试场景，快速试手感。
- 语言选 **C#**，与 `random-room-runtime` 一致 → 系统成熟后**移植近乎零翻译**。
- 成功标准：产出一个**能被干净抽出、塞进 `random-room-runtime`** 的运动/动画模块。

## 2. 核心技术路线（一句话版；完整依据见 `docs/`）

来自对《雨世界》(Rain World) 的反编译实证研究：

1. **确定性时基**：固定 **40 tick/秒** 逻辑步长 + 渲染插值（`lastPos→pos` 用 timeStacker 做
   Lerp/Slerp），逻辑与画面解耦。
2. **身体 = 珠子 + 橡皮筋**：几个 body chunk（带质量的点/球）+ 弹簧/距离约束
   （Verlet 式积分 + 约束松弛）。
3. **运动与姿态分层**：蜥蜴腿的运动态是**一个追目标点的足端粒子**
   （`vel = Lerp(vel, 朝目标 × huntSpeed, quickness)` + 吸附），不是多关节物理链；蜘蛛/人形
   在同类足端之上派生两段 IK 膝/肘作为**渲染姿态**，不碰撞、不承力。
4. **走路 = plant-and-trail**：脚踩住不动 → 身体前移 → 脚相对落后超阈值 → 找新落点 → 再踩住；
   腿长用距离约束维持。
5. **脚落点 = 射线打真实 3D collider**（**不拆网格、不重造格子碰撞**，见研究文档 §12）；
   Godot 用 `PhysicsRayQueryParameters3D`。
6. **locomotion 模式靠涌现**：Lizard/Spider 抓住地形时关闭重力；Deer/Vulture 用常开重力下的
   连续升力；DaddyLongLegs 按整链贴面支撑连续抵消重力；DropBug 站稳时前段全额、尾段部分抵消
   （前后不对称）。走/爬/攀附**不按 floor/wall/ceiling 枚举模式**；玩家式显式状态机仅在确需
   精细操控的角色上使用。（水中运动不做，见 §4。）

## 3. 文档地图

**完整索引见 [`docs/README.md`](docs/README.md)**。最常用的三篇：

| 文档 | 什么时候看 |
|------|-----------|
| [`docs/porting_contract.md`](docs/porting_contract.md) | **回迁真相源**。改内核 API、加物种、准备回迁时看：十物种各自的装配/驱动/输入/输出四契约、`ITerrainQuery` 全语义、确定性守则与三层回归、迁移路线与集成姿态。 |
| [`docs/rainworld_procedural_animation_research.md`](docs/rainworld_procedural_animation_research.md) | **核心参考**。深度研究 + 反编译实证（§11 代码级）+ Godot 移植策略（§12：为什么用射线而不是细网格）。本项目内为**工作副本**（2026-08-06 已反向同步回主项目，两边一致）。 |
| [`docs/rainworld_creature_taxonomy.md`](docs/rainworld_creature_taxonomy.md) | **扩多节脊柱、肢体或固定触手前先查这里的先例**：92 物种 / 54 个 `Creature` 实现类、七大身体架构统计。 |

十个物种后端**各有独立文档** `docs/<物种>_controller.md`，记录该后端的机制来源、各轮修复的
症状/根因/**被推翻的初判**与验证边界 —— 见 §5 表格。改某个物种前先读它那篇。

渲染层：[`docs/rainworld_render_research.md`](docs/rainworld_render_research.md)（取证与契约，
§5 是实施状态）+ [`docs/rendering_principles.html`](docs/rendering_principles.html)（叙述版导览）。

主项目怪物美术/规格（回迁时对接）：`../random_room/random-room-runtime/docs/` 下的
`monster_visual_research.md`、`procedural_monster_visual_spec.md`、`tyrant_enemy_requirements.md`。

## 3.5 参考：反编译的雨世界真实源码（本机私用，仓库外）

研究文档 §11 的所有代码级结论都出自它，实现时可**逐行对照真实实现**。

- **DLL（真相源）**：`~/workspace/others/Managed_extracted/Assembly-CSharp.dll`（+ 同目录
  147 个依赖 DLL）。来自用户自有的 Rain World 桌面副本。
- **已反编译的关键类**：`~/workspace/others/rw_decomp/`（含 `README.md` 索引）。
- **再反编译任意类**（RW 游戏类都在**全局命名空间**）：
  ```bash
  export PATH="$PATH:$HOME/.dotnet/tools"   # ilspycmd（dotnet global tool）
  ilspycmd ~/workspace/others/Managed_extracted/Assembly-CSharp.dll -t <ClassName> > ~/workspace/others/rw_decomp/<ClassName>.cs
  ```
  DLC 的类**不在**全局命名空间，`-t` 要带全名：`MoreSlugcats.*`、`Watcher.*`、`DLCSharedEnums`。
  做**跨类统计**（继承树、全局 grep）时整程序集展开更省事，约 10 秒：
  ```bash
  ilspycmd ~/workspace/others/Managed_extracted/Assembly-CSharp.dll -p -o <仓库外目录>   # ~22MB
  ```
- ⚠️ **边界**：反编译源码**仅供本机学习/互操作参考，不得提交进本仓库、不得再分发**。故意放在
  **所有 git 仓库之外**（`~/workspace/others/`）。写代码时可以参考其算法/结构，但**落到本项目
  的是自己的实现**，不是拷贴游戏代码。

## 4. 范围 / 非目标

- **做**：生物运动/动画系统内核 + 必要的白盒测试场景与调参工具。
- **不做**：AI 寻路、战斗、**游泳 / 水中运动（明确不涉及）**、关卡生成、玩法、锁钥、UI、
  正式美术——那些留在主项目；`MoveTarget` 只接受宿主给出的**邻近可达点**。
- 一切设计以「**能干净地回迁到 `random-room-runtime`**」为约束。

## 5. 里程碑与物种后端

| 里程碑 | 内容 | 状态 |
|--------|------|------|
| **M0** | 项目章程：目标 + 文档就位 | ✅ |
| **M1** | 物理地基：固定步长循环 + 点/弹簧 Verlet 身体沙盒 | ✅ |
| **M2** | 会走路：plant-and-trail 腿 + 射线落脚，平地行走 | ✅ |
| **M3** | 地形涌现：斜坡 / 墙 → 走、爬两态自然涌现（重力开关 + 射线方向切换） | ✅ |
| **M4** | 多样化与调参：多足 / 尾巴 / 多种体型，参数化手感（对标 `LizardBreedParams`） | ✅ |
| **M5** | 移植接口：抽出与引擎解耦的模块，定义回迁边界 | ✅ |
| **M6** | 独立 Cicada 后端：3D 飞行/停驻/Charge、四翼四触须、双预设与专用沙盒 | ✅ |

> M1~M5 的**产物细节**（翻越三件套、闲置姿态、防折叠支柱、抽离质量门等）在
> [`docs/lizard_controller.md`](docs/lizard_controller.md) §1。

**十个并列后端**（互不继承，只共享 `physics/` + `terrain/` + `host/` 底座）：

| 后端 | 运动路线 | 预设 | 文档 |
|------|---------|------|------|
| **Lizard** | 抓地关重力；plant-and-trail 四足 + 多节脊柱。**共享物理层的来源**，M1~M5 产物档案 | default / heavy / sprinter / hexapod | [lizard](docs/lizard_controller.md) |
| **Humanoid** | 清醒近地失重伺服木偶；双足 + 手臂两条独立通道，零 knockdown 状态机 | scavenger / brute / waif | [humanoid](docs/humanoid_controller.md) |
| **Spider** | 抓地关重力；足端粒子 + 渲染期两骨 IK 膝，多锚点线性身体链 | small / large / lean | [spider](docs/spider_controller.md) |
| **Centipede** | 双端表面轨迹；任意 ≥2 节装配、真实抓足、确定性行波 | short / long / armored / ribbon | [centipede](docs/centipede_controller.md) |
| **Cicada** | 双 chunk 差分飞行 + 显式三面停驻 + Charge | light / dark | [cicada](docs/cicada_controller.md) |
| **Vulture** | **重力常开** + 拍翅同步 sin² 升力脉冲；起降由翅膀模式涌现 | vulture / king / swift / quad | [vulture](docs/vulture_controller.md) |
| **TentaclePlant** | 固定锚定 + 独立触手链；伏击—突刺—抓取—回收 | original / short / hunter | [tentacle_plant](docs/tentacle_plant_controller.md) |
| **Deer** | 常开重力下的连续支撑；粗重叠躯干 + 四条独立多节腿 | original / compact / strider | [deer](docs/deer_controller.md) |
| **DaddyLongLegs** | **无前向轴**：seed 冻结的完整图球团 + 整链贴面连续抵消重力 | brother / daddy / terror | [daddy_long_legs](docs/daddy_long_legs_controller.md) |
| **DropBug** | **伏击者**：三节短链、前后不对称重力、运行时收放静息长度的悬挂态、弹道俯冲 | original / nimble / bulky | [dropbug](docs/dropbug_controller.md) |

## 6. 跨物种硬约束（改任何内核前必读）

### 6.1 单位约定

- 1 RW tile (20px) = **0.5 m**；`1px = 0.025m`。
- `Vel` 语义 = **「米/tick 位移」**（积分 `Pos += Vel` **不乘 dt**，内核零 delta 依赖）。
- 重力默认 **36 m/s²**（= RW 0.9 px/tick² 直接换算），`GravityPerTick = 36 × 0.025² = 0.0225`。

### 6.2 3D 朝向边界

`BodyChunk.Rotation` **只是一根 forward 方向**，不是完整旋转或局部坐标系。渲染/附着物须结合
稳定 up（通常取 `SupportNormal`，必要时沿用上一帧 up）构造 Basis/Quaternion；forward 与 up
近共线时**显式选备用 up**，避免 roll 突跳。RW 的 2D 单方向向量可唯一确定平面旋转，移植到 3D
后必须补上这层宿主语义。

### 6.3 RotationChunk 拓扑（≙ RW `BodyChunk.rotationChunk` 全套语义）

- 朝向参照 + 派生 `Rotation = (Pos − 参照.Pos).normalized`。**退化照抄 RW**：null → Up
  （≙ 显式回落 (0,1)）；两点近重合（模长 ≤1e-5 = Unity kEpsilon）→ 零向量
  （≙ Unity `normalized` 原语义，**消费端自行回退**）。
- 建 `ChunkConnection` 时两端**自动互绑**（≙ RW 构造副作用，后建覆盖、不分连接类型）。
- 工厂装配完**显式钉定**脊柱指向，**不依赖建连接的顺序巧合**——我们的尾链建在最后，巧合会让
  髋参照尾根，**软尾摆动会污染步向**。
- 拓扑**不进** `DeterminismHasher`（纯装配期引用）；smoke `[CORE-ROTATION]` 结构断言钉住
  互绑/覆盖/钉定不变量。

### 6.4 内核目录分层与模块边界

`core/` 按**依赖方向**分层，命名空间跟随目录：`physics/` / `terrain/` / `host/` /
`diagnostics/` / `species/<十物种>/`。枢纽 `BodyFactory` 坐在 `species/` 根（它装配
lizard + humanoid + vulture 三家，是唯一不对称点；按物种拆开是独立后续工作）。

两处**有意的目录 ≠ 命名空间**：`core/godot/` 适配器实现 `Terrain` 接口但归宿主程序集编译
（留顶层作回迁隔离区）；`AssemblyInfo.cs` 只承载程序集属性。

> **per-file using 现在就是依赖图。** 十个物种目录只依赖四层底座，**唯一跨物种边是
> Humanoid → Lizard**（人形腿复用 `Limb` 的 opt-in `LookaheadTicks` + `MoveIntentDeadzone`
> 常量）。smoke `[CORE-MODULARITY]` 遍历全部物种目录，白名单外的另一物种命名空间即 FAIL。
> 扫描走**源码**而非 IL —— 跨物种耦合最常见的形态就是编译期常量，它在 IL 里被内联得一干二净，
> 元数据扫描看不见。

### 6.5 调参可行域

腿参数必须留在可行域（速度 ≥0.12、步幅 ≤0.75）附近。**「笨重感」交给脊柱节数 / 体格缩放 /
站距 / 尾巴刚度表达**——它们不碰抓地循环。腿慢 + 步幅大 + 外撇远三者叠加会让脚永远追不上身体
（heavy 第一版近瘫的根因，见 [`docs/lizard_controller.md`](docs/lizard_controller.md) §1 M4）。

### 6.6 opt-in 是硬要求

任何改变**已进哈希**状态的新机制，必须以默认关闭的 opt-in 参数落地（先例：
`Limb.LookaheadTicks`、`SpiderBreedParams.KneeStepBudgetRatio`），否则既有品种基线漂移。
共享层改动则**全部重新审计**，不能用批量改哈希代替行为断言。

### 6.7 渲染层边界

渲染件在 `scripts/render/`（游戏程序集，**不进 `core/`**），对内核**只读**；化妆状态
（呼吸 / bend pole / 逐节 up / 逐羽低通 / verlet 垂索 / 蛛毛 / 表情 PRNG）渲染侧私有，
不进物理与哈希。

> **sRGB 顶点色陷阱**：tonemap 环境下 `StandardMaterial3D` 顶点色**默认按线性解读**，剪影黑
> 被抬亮 ≈4×。Daddy / Spider / Humanoid 走 `TubeMeshBuilder.Build(srgbVertexColors: true)`
> 在所见空间调色；**Lizard / Centipede / Vulture 三件在线性解读下调色定型，翻转需重调**（遗留）。

## 7. 确定性回归（改物理内核后必跑）

**全部真断言** —— 探针只打印不判定的旧形态是**假绿**（评审修复轮的教训）。

```bash
# ① 无引擎冒烟（秒级，最快反馈）。退出码即判定：双跑 bit-exact + 哈希对基线 +
#    里程/约束收敛/无 NaN + 生命周期契约 + 引擎边界(TypeRef) 与模块边界(跨物种源码扫描)双扫描。
#    覆盖 Lizard / Centipede / Vulture / Humanoid 四家。
dotnet run --project core/smoke

# ② Godot 主矩阵（分钟级）。45 配置 = Lizard 20 + Centipede 13 + Vulture 4 + Humanoid 8。
#    pipefail + 退出码聚合，结尾打 MATRIX GREEN/RED：
./tools/run_matrix.sh [输出目录]

# ③ 六个独立物种的专项（各自 smoke + 矩阵）：
dotnet run --project core/spider_smoke          && ./tools/run_spider_matrix.sh            # 16 项
dotnet run --project core/cicada_smoke          && ./tools/run_cicada_matrix.sh            #  9 项
dotnet run --project core/tentacle_plant_smoke  && ./tools/run_tentacle_plant_matrix.sh    # 17 项
dotnet run --project core/deer_smoke            && ./tools/run_deer_matrix.sh              # 18 项
dotnet run --project core/daddy_long_legs_smoke && ./tools/run_daddy_long_legs_matrix.sh   # 40 项
dotnet run --project core/dropbug_smoke         && ./tools/run_dropbug_matrix.sh           # 25 项

# ④ 抽离/移植类改动的金标准：改动前后各捕获一次全矩阵输出，逐字节 diff 为空（M5 即以此验收）。
```

七套 Godot 矩阵合计 **170 项**（45 + 16 + 9 + 17 + 18 + 40 + 25）。各矩阵覆盖什么、哪些机制
有消融红灯，见对应物种文档与脚本本身。

**单配置手跑**：

```bash
$GODOT --headless --path . --log-file /private/tmp/godot_codex.log --fixed-fps 40 -- --determinism=2000 --tps=400 [--route=… |--breed=… |--spawn=… |--expect-hash=X16]
```

> `[RESULT]` 在进程 teardown 之前打印；已知 Godot 4.7 macOS 偶发退场 mutex 崩溃（exit 134），
> **判定以 `[RESULT]` 为准**（脚本已处理）。

### 可执行基线真相源（**别处一律引用不复制**）

| 物种 | 真相源 |
|------|--------|
| Lizard / Centipede / Vulture / Humanoid | `tools/run_matrix.sh` + `core/smoke/Program.cs`（`ExpectedHash` / `ExpectedVultureHash` / `HumanoidExpectedHash`）+ `core/smoke/CentipedeSmoke.cs` |
| 其余六物种 | `tools/run_<物种>_matrix.sh` + `core/<物种>_smoke/Program.cs` |

有意改内核时**只更新对应真相源**；文档里的数字只是当前状态快照，**不能替代重跑**。

## 8. 环境

- **Godot（mono/C#）**：`/Applications/Godot_mono.app/Contents/MacOS/Godot`
- **.NET**：dotnet 10 SDK
- **Godot CLI 日志**：执行任何 Godot CLI（尤其 `--headless`）必须显式追加
  `--log-file /private/tmp/godot_codex.log`（沿用主项目约定）。
- 本项目为**独立 git 仓库**（`master` 分支）。

## 9. 约定

- 逻辑（物理/腿）跑在**固定步长**内，渲染在 `_Process` 里按累加器余数**插值**——从 M1 起就照
  这个结构搭，**别让画面帧率污染物理**。
- **物理/腿逻辑与渲染解耦**；脚落点走连续射线，寻路（若引入）走粗网格，两者分开。
- 尽量**镜像 `random-room-runtime` 的概念与命名**，降低回迁翻译成本。
- 编码准则沿用主项目：**想清楚再写、最简实现、外科手术式改动、匹配既有风格**。
- **新增物种后端时**：不改共享层、不继承其它物种、落一份 `docs/<物种>_controller.md`、
  在 `porting_contract.md` 补装配/输入/回归三处，并加独立 smoke + 矩阵。
- **修复轮的记录去处是物种文档，不是本文件**。本文件只保留跨物种适用的约束与入口。
