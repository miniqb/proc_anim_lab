# proc_anim_lab —— 3D 程序化生物动画实验室

> Godot 4.x / C# 的独立沙盒项目。**目标：从零实现一套 3D 版"雨世界式"程序化生物动画/运动系统；等它在这里成熟后，整体移植回 [`random-room-runtime`](../random_room/random-room-runtime/) 的怪物系统。**
>
> 当前状态：**M6 Cicada 独立后端 + 并列蜈蚣控制器 + 秃鹫飞行控制器 + 人形运动后端完成（2026-07-30）**——
> `ProcAnim.Core` 现在包含 Lizard / Humanoid / Spider / Centipede / Cicada / Vulture 六个平行的物种控制器。
> 蜈蚣的任意节装配、双端表面轨迹、真实抓足和四个稳定预设，蝉的 3D 飞行、显式三面停驻、Charge、
> 四翼四触须和 light/dark 预设，以及秃鹫的重力常开升力脉冲飞行、拍翅行波、栖息/起飞涌现和
> 四个品种预设均已落地并有独立回归；M5 内核抽离、回迁契约及 Lizard 外部评审修复保持完成。
> 「默认集成姿态」的闭环在主仓接线后验证（契约 §4.1/§8.3）。
>
> 2026-07-21 墙角残留深挖轮已完成：多节脊柱持久拉直、确定性掉头、局部卡角/terrainSqueeze、接触可行锥结构恢复与四条事件相对回归均已落地；历史红灯说明保留在下文，最终状态以下一段「修复轮三」与当前矩阵为准。
>
> 2026-07-29 新增**人形运动后端**（与蜥蜴并列的双足控制器，≙ 反编译 Scavenger）：`Arm`/`HumanoidLocomotionController`/`HumanoidParams` + 三预设（scavenger/brute/waif），共享层零改动、蜥蜴基线逐位不变；合并入主线后矩阵 37→45 配置、smoke 增八断言。详见 §5「人形运动后端」段。

---

## 1. 为什么单独建这个项目

- `random_room` 主项目体量大，直接在里面做程序化动画试验**容易污染环境、拖慢迭代**。
- 这里**只关注一件事**：怪物的身体怎么被程序化地驱动运动。白盒测试场景，快速试手感。
- 语言选 **C#**，与 `random-room-runtime` 一致 → 系统成熟后**移植近乎零翻译**。
- 成功标准：产出一个**能被干净抽出、塞进 `random-room-runtime`** 的运动/动画模块。

## 2. 核心技术路线（一句话版；完整依据见 `docs/`）

来自对《雨世界》(Rain World) 的反编译实证研究（见 §3 文档）：

1. **确定性时基**：固定 **40 tick/秒** 逻辑步长 + 渲染插值（`lastPos→pos` 用 timeStacker 做 Lerp/Slerp），逻辑与画面解耦。
2. **身体 = 珠子 + 橡皮筋**：几个 body chunk（带质量的点/球）+ 弹簧/距离约束（Verlet 式积分 + 约束松弛）。
3. **运动与姿态分层**：蜥蜴腿的运动态是**一个追目标点的足端粒子**（`vel = Lerp(vel, 朝目标*huntSpeed, quickness)` + 吸附），不是多关节物理链；蜘蛛在同类足端之上派生两段 IK 膝点作为正式渲染姿态，膝不碰撞、不承力。
4. **走路 = plant-and-trail**：脚踩住不动 → 身体前移 → 脚相对落后超阈值 → 找新落点 → 再踩住；腿长用距离约束维持。
5. **脚落点 = 射线打真实 3D collider**（**不拆网格、不重造格子碰撞**，见研究文档 §12）；Godot 用 `PhysicsRayQueryParameters3D`。
6. **locomotion 模式靠涌现**：抓着可达地形 → **重力关成 0**（不会掉墙）；走/爬在腿与身体两层**都无分支**，只有"抓住/没抓住"这一个开关。玩家式**显式状态机**仅在需要精细操控的角色上才用。（雨世界的"入水→腿垂下"游泳态本项目不做，见 §4。）

## 3. 相关文档（本项目内引用）

- **[`docs/rainworld_procedural_animation_research.md`](docs/rainworld_procedural_animation_research.md)** —— **核心参考**。雨世界程序化动画系统深度研究 + **反编译实证（§11 代码级：BodyPart/Limb/LizardLimb/TailSegment/TerrainCurve 等）** + **Godot 移植策略（§12：为什么用射线而不是细网格）**。
- **[`docs/porting_contract.md`](docs/porting_contract.md)** —— **M5 产物**。`ProcAnim.Core` → `random-room-runtime` 回迁契约：模块清单与依赖面、装配/驱动/输入/输出四契约、`ITerrainQuery` 接缝语义、确定性守则与三层回归、两条迁移路线与两种集成姿态（含主项目对接面调研快照）。
- **[`docs/centipede_controller.md`](docs/centipede_controller.md)** —— 并列蜈蚣后端：任意节/逐节覆写装配、双端表面轨迹、真实抓足、生命周期、四个稳定预设与当前验证边界。
- **[`docs/rainworld_creature_taxonomy.md`](docs/rainworld_creature_taxonomy.md)** —— **反编译实证**：雨世界生物分类地图（92 物种 / 54 个 `Creature` 实现类）。三条正交分类轴、`Creature`+`BodyPart` 继承树、七大身体架构（含每类的 chunk/connection/肢体统计）、模板参数抽样。扩多节脊柱或多节腿前先查这里的先例。
- **[`docs/cicada_controller.md`](docs/cicada_controller.md)** —— **M6 产物**。Cicada 双 chunk 飞行、稳定 3D 姿态、显式停驻、Charge、附肢表现、宿主接口与专项回归。
- [`docs/README.md`](docs/README.md) —— 文档索引。
- 源文档（主项目，真相源）：`../random_room/random-room-runtime/docs/rainworld_procedural_animation_research.md`（本项目内为**工作副本**，两边如有更新需手动同步；副本中指向主项目其它文档的相对链接会失效，属正常）。
- 主项目怪物美术/规格（回迁时对接）：`../random_room/random-room-runtime/docs/monster_visual_research.md`、`procedural_monster_visual_spec.md`、`tyrant_enemy_requirements.md`。

## 3.5 参考：反编译的雨世界真实源码（本机私用，仓库外）

除了研究文档，本机还留有一份**雨世界的反编译源码**，可在实现时**逐行对照真实实现**——研究文档 §11 的所有代码级结论都出自它。

- **DLL（真相源）**：`~/workspace/others/Managed_extracted/Assembly-CSharp.dll`（+ 同目录 147 个依赖 DLL）。来自用户自有的 Rain World 桌面副本（Mono/.NET IL，可反编译到接近源码）。
- **已反编译的关键类**：`~/workspace/others/rw_decomp/`（`BodyPart`/`Limb`/`LizardLimb`/`TailSegment`/`Lizard`/`LizardGraphics`/`TerrainManager`/`TerrainCurve`/`Player` 等 13 个 + 一份 `README.md` 索引）。
- **再反编译任意类**（RW 游戏类都在**全局命名空间**）：
  ```bash
  export PATH="$PATH:$HOME/.dotnet/tools"   # ilspycmd（dotnet global tool）
  ilspycmd ~/workspace/others/Managed_extracted/Assembly-CSharp.dll -t <ClassName> > ~/workspace/others/rw_decomp/<ClassName>.cs
  ```
  DLC 的类**不在**全局命名空间，`-t` 要带全名：`MoreSlugcats.*`、`Watcher.*`、`DLCSharedEnums`。做**跨类统计**（继承树、全局 grep）时整程序集展开更省事，约 10 秒：
  ```bash
  ilspycmd ~/workspace/others/Managed_extracted/Assembly-CSharp.dll -p -o <仓库外目录>   # ~22MB
  ```
- ⚠️ **边界**：反编译源码**仅供本机学习/互操作参考，不得提交进本仓库、不得再分发**。故意放在**所有 git 仓库之外**（`~/workspace/others/`）。写代码时可以参考其算法/结构，但**落到本项目的是自己的实现**，不是拷贴游戏代码。

## 4. 范围 / 非目标

- **做**：生物运动/动画系统内核 + 必要的白盒测试场景与调参工具。
- **不做**：AI 寻路、战斗、**游泳 / 水中运动（本项目明确不涉及）**、关卡生成、玩法、
  锁钥、UI、正式美术——那些留在主项目；`MoveTarget` 只接受宿主给出的邻近可达点。
- 一切设计以"**能干净地回迁到 `random-room-runtime`**"为约束。

## 5. 路线图（里程碑）

| 里程碑 | 内容 | 状态 |
|--------|------|------|
| **M0** | 项目章程：目标 + 文档就位，清晰起点 | ✅ 完成 |
| **M1** | 物理地基：固定步长循环 + 点/弹簧 Verlet 身体沙盒（可拖动、能滚，验证软体手感与确定性） | ✅ 完成 |
| **M2** | 会走路：plant-and-trail 腿 + 射线落脚，平地行走；可拖动身体看腿自适应 | ✅ 完成 |
| **M3** | 地形涌现：斜坡 / 墙 → 走、爬两态自然涌现（重力开关 + 射线方向切换） | ✅ 完成 |
| **M4** | 多样化与调参：多足 / 尾巴 / 多种体型，参数化手感（对标 `LizardBreedParams`） | ✅ 完成 |
| **M5** | 移植接口：抽出与引擎解耦的模块，定义回迁 `random-room-runtime` 的边界 | ✅ 完成 |
| **M6** | 独立 Cicada 后端：3D 飞行/停驻/Charge、四翼四触须、双预设与专用沙盒 | ✅ 完成 |

> **M1 产物**：`scripts/physics/`（纯 C# 内核：BodyChunk / ChunkConnection / Body / ITerrainQuery，零场景树依赖，M5 回迁边界；**M5 起移至 `core/`**）、`scripts/terrain/RaycastTerrainQuery.cs`（内核与 Godot 物理的唯一接缝；**M5 起移至 `core/godot/`**）、`scripts/sandbox/`（驱动/渲染/拖拽/确定性探针）、`scenes/sandbox.tscn`（白盒：地板+缓坡+台阶+墙）。
>
> **M2 产物**：`scripts/physics/Limb.cs`（腿粒子：单点追目标 IK + plant-and-trail 状态机 + 竖直投影射线 FindGrip + 单侧腿长钳制，≙ RW Limb/LizardLimb）、`scripts/physics/LizardLocomotionController.cs`（行走驱动：推进力 ∝ 抓地腿数，无 locomotion 状态机，MoveDir/RunSpeed 唯一输入，≙ Lizard 移动块）、`scripts/physics/SphereTerrain.cs`（Body/Limb 共用的球-地形解算）；沙盒 WASD 行走 + 拖拽看腿自适应，脚球按状态换色（绿抓稳/橙迈步/灰蓝摆动）。确定性模式改为脚本化路点巡走（行走本身进哈希）：800 tick 走约 25.7 m、平均 2/4 腿抓地。
>
> **M3 产物**：走/爬两态涌现，零模式分支（≙ 研究文档 §11.6b/§12.3）。`LizardLocomotionController`：重力开关（`FootingCounter`/`NoGripCounter` → `ApplyGravity`：抓稳→重力 0 + 贴地摩擦档 0.8/0.5，坠落→重力回归 + 0.999/0.3，数值直取 RW）、支撑法线 `SupportNormal`（抓地腿抓握面法线的平滑平均：平地=上、墙=墙法线）、移动意图被支撑面挡住的分量沿面内上坡重定向（推墙自动变向上爬，同一公式覆盖斜坡）、推进目标射线钉在支撑面 + `RideHeight`（≙ 瞄路径格中心；身体不飘离墙的真正来源）、引擎极速 `MaxMoveSpeed`（墙面无阻滑升的唯一刹车——平地上碰撞/腿阻先饱和，撞不到它）。`Limb`：整套步进几何跑在支撑系（up=支撑法线：走=朝下打、爬=朝墙打，同一条代码）、FindGrip 三类候选统一选「离期望点最近」（支撑向投影 + 锚点直射【面前有墙先够到墙面】+ 攀爬中世界向下投影【翻越棱线够到顶面；起点必须沿「支撑法线的水平反方向」单侧排开——沿步进方向排会叠成悬在墙面外的竖线，薄墙顶面永远打不到】）、`HasGrip` 区分真落点与摆动期空中目标（修掉 M2 潜伏 bug：脚追上空中胡萝卜也计抓地，M3 里它会骗重力开关悬空飞天）。
>
> **M3 翻越三件套**（快速爬墙/正面推墙实测逼出来的，全部 ≙ RW FollowConnection 对应机制）：
> ① **翻越伺服**：头越过棱线后支撑向目标射线打空，补一根加深的世界向下探测（`CrestProbeDepth`）把推进目标钉在顶面 + 比例回中力（`CrestCentering`，≙ RW 攀爬回中 `vel -= (pos-格中心)*k`）——否则会退回「头前+向上」的飞行胡萝卜，在支撑系旋转跟上前把身体弹道式抛过墙顶。
> ② **顶死换步**（≙ timeSpentTryingThisMove）：推着走却几乎不动超过 `StallReleaseTicks`，强制抓得最久的腿松开重迈步——正面顶墙时身体静止，plant-and-trail 的松脚条件永远不满足，RW 靠随机抖动打破僵局，确定性内核必须显式超时。
> ③ **拉直力**（≙ straightenOut）：身体轴线背对目标（撞墙翻倒、头折叠到髋后）时头沿「髋→目标」强拉、髋反向推——翻倒姿态会让 stepDir 被反向身体轴污染、腿全部背着目标迈步，没有它就永久瘫在墙脚。
>
> 确定性路线：上坡→下坡→撞墙→爬墙→翻越 3m 薄墙→落地续走循环（2000 tick：waypointsReached=12、gravityOff≈80%）；另有 `--route=wall`（配 `--spawn=-4,0.5,0`）做正面推墙翻越测试：2000 tick 翻越 10 次，微扰扫描 9~11 次全成功。身体按重力开关换色（红=坠落、青=抓稳/攀爬）。落地冲击的单 tick 约束拉伸通常 ~3%、偶发硬着陆 ~16%（软体压扁回弹，下 tick 即被松弛修正）。
>
> **M4 产物**：手感全部收拢到一张品种参数表。
> - `scripts/physics/BreedParams.cs`（≙ `LizardBreedParams` 运动子集）：字段名镜像 RW（`BodySizeFac`/`LimbSpeed`/`LimbQuickness`/`StepLength`/`LiftFeet`/`FeetDown`/`LegPairDisplacement`/`LimbGripDelay`/`SmoothenLegMovement`/`NoGripSpeed`/`TailSegments`/`TailStiffness`+`TailTipStiffness`（tailStiffnessDecline 的端点式）/`TailLengthFactor`），单位全部本项目制；`SpineSegments`/`LegPairs` 为 3D 扩展（RW 蜥蜴固定 2 锚 4 腿）。纯出生配置——工厂读表装配，内核运行时不回读、零行为分支。
> - `BodyFactory`（≙ `LizardBreeds`）重写为通用装配器：脊柱 = N chunk 的 Rigid 链（头…中段…髋，`LizardLocomotionController` 构造改显式 head/hips/spineFollower 三点引用 + `HeadLinkLength`——2026-07 SpineFollower 修复轮改名，详见下方段落）、腿对沿脊柱均匀分布锚定（相邻对出生错位相反 = 对角步态相位种子）、尾巴 = 渐细 PullOnly 链（WeightA 沿链递减）。四预设：**default**（M2~M3 调教的基线四腿，取值与旧硬编码逐位一致）、**heavy**（绿蜥系：3 节脊柱×1.2 体格、宽站距、硬长尾、`SmoothenLegMovement=false`）、**sprinter**（黄蜥系：0.85 体格快腿短尾）、**hexapod**（3 脊柱 3 腿对；三锚六腿拓扑有 RW Caramel/SpitLizard 先例，3D 参数为本项目调教）。沙盒数字键 1~4 现场换品种、`--breed=` 供无头回归。
> - **闲置休息姿态**（≙ RW `Limb.Mode.HuntRelativePosition`，清偿 M3 遗留）：连续 `IdleAfterTicks` 找不到落点且 RunSpeed≈0 → `IdlePose`，追逐目标每 tick 切「锚点沿支撑方向垂下、向本侧微撇」的休息位，脚垂回身侧；有输入立即恢复迈步。`HasGrip` 恒 false 不骗重力开关；有移动意图时整套逻辑休眠（默认路线哈希不变）。回归：`--route=stand --spawn=-6,3.7,0` 空降墙顶站桩，悬空侧双脚应 `idle=True` 收拢。
> - **防折叠支柱**（≙ RW `Lizard.bodyChunkConnections[2]`，用户实测逼出来的）：距离约束链没有弯曲刚度——头-中、中-髋两条距离都满足时链条照样可 180° 对折，heavy 刚上墙时头会折到中段下面钻着爬。RW 蜥蜴的对策是第三条「头↔第三节」Push-only 连接：`RestLength = 节长×(1+bodyStiffnes)`，伸直（2 节长）永不触发、折叠低于下限才软推撑开 = 参数化的最大折角钳制。移植为 `BreedParams.BodyStiffness`（粉蜥 0.2/绿蜥 0.5/蓝蜥 0 直取 RW 品种表）+ 工厂对每对隔一节 chunk 加 `SoftOnly` PushOnly 连接（弹性走 RW 原公式 `1-Lerp(0.9,0.5,stiffness)`，Pos/Vel 同步修正 ≙ RW BodyChunkConnection.Update 原语义——只写 Vel 的弱推压不住抓地腿锚定的折叠）。`SoftOnly` 连接不进硬求解器、不计 `LastRelaxDeviation`、渲染不画线。指标 `maxFoldIntrusion`/`foldTicks` 进 [METRIC]：深折叠只剩落地瞬态（heavy wall 全程 56 tick 且分散在 3 次翻越里）。2 节脊柱无隔节对，default/sprinter 哈希不受影响。
> - **调参教训（heavy 第一版近瘫的根因）**：腿慢（LimbSpeed 0.10）+ 步幅大（StepLength 0.85）+ 外撇远（0.7）三者叠加会让脚永远追不上身体，平均抓地 0.6/4 → 重力开关长期打开。腿参数必须留在可行域（速度 ≥0.12、步幅 ≤0.75）附近，「笨重感」交给脊柱节数/体格缩放/站距/尾巴刚度表达——它们不碰抓地循环。
>
> **M5 产物**：内核抽离为独立程序集——回迁 = 拷走 `core/` 一个文件夹。
> - `core/ProcAnim.Core.csproj`：内核 classlib（BodyChunk/ChunkConnection/Body/SphereTerrain/ITerrainQuery/TickContext/Limb/LizardLocomotionController/BreedParams/BodyFactory + DeterminismHasher/PlaneTerrainQuery），**只使用 GodotSharp NuGet 的纯托管数学结构**——不引 Godot.NET.Sdk（挡住场景树源生成器；注意 GodotSharp 包里 GD/Node 仍编译期可达，真正的强制是 smoke 的 TypeRef 边界扫描：允许清单 Vector3/Mathf 之外的 Godot.* 引用即回归 FAIL）。命名空间去 Lab 化：`ProcAnimLab.{Physics,Sandbox,Terrain}` → `ProcAnim.Core`。`core/godot/RaycastTerrainQuery.cs` 引擎适配器随文件夹走、归游戏程序集编译（游戏 csproj `Compile Remove core/**` 后单独 Include 它）。BodyFactory 进内核并去掉唯一 GD 调用（未知品种名静默回落 default）。
> - `core/smoke/`：**无引擎冒烟回归**（纯 .NET console，`dotnet run --project core/smoke`，退出码判定）：解析平面地形（PlaneTerrainQuery，含 HitFromInside 零法线语义）+ default 平地巡走 1000 tick（直行+45° 转向进哈希），进程内双跑 bit-exact 且哈希钉死基线（值以 `core/smoke/Program.cs` 的 `ExpectedHash` 为唯一真相源）——「与引擎解耦」的运行时实证，也是回迁后目标仓库的秒级内核回归。评审修复轮追加断言：嵌入恢复、Shift 连续性、Launch 恢复、MoveTarget 到达/取消/传送契约、wall-pose 墙顶顶死+侧扰动稳定性、TypeRef 引擎边界扫描。哈希折叠下沉为内核 `DeterminismHasher`，沙盒探针与冒烟共用同一实现（两边哈希可互证）。
> - **抽离质量门**：9 配置全矩阵（default 双跑/40vs400Hz/perturb、wall、stand、三品种）与抽离前基线**逐字节 diff 为空**——移动文件+改命名空间+程序集拆分+哈希器下沉，零行为漂移。
> - [`docs/porting_contract.md`](docs/porting_contract.md)：回迁契约。模块清单与依赖面、装配（品种可行域表）/驱动（40 tick 固定序、60Hz 宿主 0.025s 累加器）/输入（MoveDir/RunSpeed 两旋钮 + MoveTarget 路径点直喂可选第三旋钮，≙ RW 寻路器喂路径格的原始形态）/输出（渲染与 AI 观测面）四契约、ITerrainQuery 接缝全语义（HitFromInside 零法线、主项目掩码层 20 + RID 排除规范、射线量级与节流阀门、Jolt 验证点）、确定性守则与三层回归、**两条迁移路线**（源码拷入 ≙ 主项目惯例 / 首个 ProjectReference）与**两种集成姿态**（规格 §4.3 可替换后端·身体拴权威根拖行 / 内核位置权威·根跟随，前者默认）。主项目对接面调研快照（单程序集 60Hz Jolt、motion 子系统 Gate-C 已过待接线、MonsterMotionSnapshot 映射表）沉淀于契约 §8。
>
> **并列蜈蚣控制器（2026-07）**：新增独立 `CentipedeLocomotionController`，只复用 `Body`/
> `ChunkConnection`/`ITerrainQuery` 底座，不改蜥蜴算法。`CentipedeParams` +
> `CentipedeSegmentParams` + 稀疏逐节覆写支持任意 ≥2 节出生装配；相邻节质量加权、隔节
> SoftOnly 防折叠。运动由带 `ArcLength` 的双端表面轨迹、逐节局部支撑、真实
> `CentipedeLeg` 抓点、确定性行波和有上限空间桶自避组成；支持 `Shift`/`Teleport`/`Launch`
> 与宿主直喂的邻近可达 `MoveTarget`。宿主通过 `RequestedLeadEnd` 显式请求 Start/End，
> `LeadEnd` 报告已应用状态；`MoveDir`/`MoveTarget` 不自动推断或切换头尾，自动选端与去抖
> 属于宿主/AI。沙盒交互模式与 `--lead=start|end` 都显式锁定领航端；只有未传 `--lead`
> 的无头 default 巡逻脚本演示宿主层方向评分 + 3 tick 去抖，再通过 `RequestedLeadEnd`
> 发命令，核心对此策略不知情。路径两端各保存有向表面切线；输入投影在外角退化时沿既有
> 切线平行运输，不用世界 Up/Right 猜方向。候选另受正向进度、近期路径回访和球体可行性
> 约束，转角样点的弧长取实际中心距离；路径端只允许领先实体领航节一个短窗口，转角样点
> 跨 tick 缓存并逐枚按真实弧长提交，明显改向会丢弃旧缓存，零弧换法线也受固定操作上限；
> 实体与轨迹落到墙体相反面连续 3 tick 时只回卷超前样点，不直接跨段重钉路径。相邻刚性连接
> 只恢复碰撞相对新增的违反。四个稳定 ID 为
> `centipede/short`（5）、
> `centipede/long`（18）、`centipede/armored`（10）、`centipede/ribbon`（12）；
> 沙盒由宿主适配器统一输入/渲染/调试，核心层不增加万能生物接口。无引擎 smoke 覆盖
> 2/5/18/32 节、显式头尾切换、生命周期、自避、查询增长和地面→18°斜坡→内角墙→外角墙顶→
> 天花板课程、固定 Start+恒 +X 下阶梯，以及脚跨薄墙恢复（中心扫掠、低速球壳 MTD、
> 停驶 stance 抓点遮挡与同侧碰墙对照），以及 long 固定 Start/End 的 0.4m 窄墙前向翻越；
> short/long 当前哈希为 `655A21496C00E86A` / `59CBCF993DF8ACD8`，Lizard
> `AAA0E4963668E5DC` 不变。Godot 13 项蜈蚣矩阵已纳入完整矩阵并通过：
> 四预设巡逻 short/long/armored/ribbon 哈希为 `0F040547BFD02043` / `B66DAAB5D006190E` /
> `A6EDF4704829C261` / `EB6011908D0FAA19`；course-short/course-long/step-down-armored 为
> `BB6696619749832D` / `A2BE4857DB102C19` / `ECC5207E14979A28`；narrow-wall-long-end 为
> `413E289A97ABD487`；embed-long/wallside-long 为 `2C8B2D67731F2B7E` /
> `501B7C44E06FA68B`。两条课程的 `maxNoneRun=4/10`、`maxBlockedRun=0/0`、
> `maxConnectionRun=3/8`，尾端通过为 15/80、89/184 tick（实际/预算），穿透 `0m`。
> 固定头下阶梯领/尾端在 tick 51/121 落地，终态非相邻间距 1.917× 半径和，严重成团连续
> 0 tick；固定 End 窄墙前向翻越后停驶 381 tick，终态连接偏差 7%，穿透 `0m`。
> 详见 [`docs/centipede_controller.md`](docs/centipede_controller.md)。
>
> **蜘蛛并列后端（2026-07）**：`SpiderLocomotionController` 不继承或扩充蜥蜴控制器，只共享
> `Body`/`ChunkConnection`/`SphereTerrain`/`ITerrainQuery`。`SpiderBreedParams` 表达至少两节的
> 有序线性身体链，每个 `LegPairSpec` 可挂到任意身体节；不支持分支或环。正式
> `spider-small` / `spider-large` 都对齐 RW BigSpider 的“两节身体、四对腿全挂第 0 节，
> 第 1 节无腿”，另以三节、多锚点合成配置钉住通用性。`SpiderLeg` 的足端独立执行
> plant-and-trail 与真实射线抓握；`KneePos` 由根/足端、上下腿长度和持久 bend pole 解出，
> 仅作为 `Anchor→Knee→Foot` 渲染输出。地面、斜坡、墙、内外角和天花板没有模式枚举：
> 支撑法线由真实抓地法线低通汇总，抓稳关重力、失抓恢复重力。独立回归入口为
> `dotnet run --project core/spider_smoke` 与 `./tools/run_spider_matrix.sh`；原蜥蜴 smoke/矩阵
> 仍必须逐位不变。
>
> **蜘蛛窄墙抱边（2026-07）**：症状 = 身体贴在接近自身宽度的墙端面时，端面外的腿没有
> 候选而悬空。修复没有增加窄墙模式：`SpiderLeg.FindGrip` 把名义落点沿旧支撑法线压进
> 凸棱轮廓，再沿 `_frameRight * Side` 用完整腿长横向反投影；左/右腿各自发现相邻侧面。
> 该候选单独保存，只有命中本侧横向面或前方正交面、且处于有限 AEP 距离余量内时，才可
> 替换旧支撑候选；命中仍走可达环和 `TargetSurfaceContact` 背书。专项
> `--route=narrow-wall` 使用 0.36m 端面并按身体半径留出接近距离；小/大型最终分别有
> 5/6 条腿连续抱侧面，双侧接触窗口分别 961/879 tick，支撑有限稳定、IK/pole/穿透不回归。
>
> **蜘蛛完整迈步修复（2026-07）**：用户指出大蜘蛛最后一对腿高频向前挪一点、像被身体
> 拖行。逐步测量推翻了“只是腿慢”的初判：旧路径在抓点刚越出可达环时会同 tick 直接
> `FindGrip`，摆动中的腿又被可达性检查逐 tick 重新 `BeginSwing`；同时落脚目标随腿根
> 每 tick 前移。后腿因此记录到约 69~73 次直接重定向，单次前向变化仅约 -1.5~+0.9cm，
> 根本没有完成 PEP→AEP 的摆动。真实蜘蛛研究显示慢速步态常由
> `R1/R3/L2/L4 ↔ L1/L3/R2/R4` 两组四腿交替，足端具有明确接触期和前摆期；本项目取其
> 确定性稳定子集，而不模拟肌肉/液压。
>
> 现行修复：① `OpposeSidePhase` 让同对左右反相、相邻腿对交替；② 全部腿先更新本 tick
> 根部，再按最低保留抓地腿数、相位和硬超距统一发放松脚许可；③ 正式预设启用
> `UseExplicitTouchdownLead`，抬脚瞬间保留横向工作区并冻结世界 AEP，摆动期间不追身体；
> ④ 越出可达环只会开始一轮完整 `ReachRecovery`，已摆动腿不会反复清零。大蜘蛛保留
> `TrailReleaseRatio=0.38`、`GaitPhaseTicks=12`、慢脚速/四 tick 抓握，最低支撑为 4；
> 四对 AEP lead 由前至后为 `0.55/0.48/0.40/0.35` 倍腿长。
>
> 回归不再只看整体速度：`StepSerial/LandingSerial` 逐腿统计完整步、直接重定向、紧急步、
> 前向追回、微步、抬脚高度及支撑期根部推进。900 tick smoke 中大蜘蛛每腿完成 27~35 步，
> 最弱腿平均追回 0.271 倍腿长，最后一对平均 0.287，后腿微步/直接重定向/紧急步均为 0；
> 399 tick Godot `--route=gait` 中最后一对平均追回 0.282、微步为 0。小蜘蛛后排仍为
> 0.265、微步为 0。蜘蛛全矩阵与原蜥蜴 smoke/20 项矩阵全部通过；没有增加地形模式分支。
>
> **蜘蛛急转腿槽修复（2026-07）**：症状 = 身体已完成 90°/180° 换向，但部分腿持续落在
> 另一侧，甚至同一腿对倒置。根因不是转身速度，而是 `CaptureSwingTarget` 原样继承旧脚相对
> 新局部轴的横向分量；一旦旧抓点跨过中线，每轮冻结 AEP 都会继续复制错误侧。现行修复只在
> 抬腿捕获新 AEP 的 tick 处理：若横向分量已跨线，且该腿已确认的上次抓面与当前支撑面
> `dot >= 0.85`，就关于本腿根部面镜像一次，保留原站距再回到解剖侧。旧脚在接触期仍可自然
> 短暂跨身；不强制松脚、不逐 tick 重映射冻结目标；法线明显不同的窄墙/棱角多面抓握也不
> 套用这次平面镜像。
> 第二轮视觉复查又暴露“回到本侧但仍贴身”的稳定坏解：现有镜像只修符号，`+0.06` 这类
> 很小的正 lane 会被以后每次 AEP 原样复制。现行实现因此在镜像之后增加同面站距软回收：
> 本腿与配对腿的上次抓面、当前 `_frameUp` 三者法线 dot 均 ≥0.85 时，每次正常抬腿把
> 横向分量向 `MaxReach×Lerp(0.68,0.82,StepLength)×DesiredReachDirection·outward`
> 回收 60%。名义宽度自然包含每条腿的扇角/横向权重/体型；已植脚不动，内外侧短暂差异会
> 随错相换步渐退；窄墙两侧与棱角多面抓握因法线不同自动跳过。
> 无引擎专项覆盖小/大型蜘蛛左右 90° 与精确 180°：身体 5~15 tick 对齐，全部足端和下一
> 落脚目标进入连续 20 tick 正确侧的起点为 13~52 tick，滚动站距平衡恢复为小型
> 36~40 tick、大型 69~95 tick；预算后最坏腿对差 P95 ≤0.09 腿长、最小内外站距比
> P05 ≥0.87、每腿实际/AEP 相对各自名义宽度 P05 ≥0.875，同时钉住 IK/pole、失抓与
> 转后推进。Godot `--route=turn
> --turn=left|right|around` 另以真实 RootPos 腿槽覆盖小/大六项；最坏 large-around 在
> 55 tick 后不再跨身，92 tick 内恢复站距平衡，零 pole 翻面。既有直行步态、
> 小/大窄墙、墙—墙 L 角和墙→天花板路线继续通过。
> **M6 Cicada 产物（2026-07-30）**：新增 `CicadaParams` / `CicadaFactory` /
> `CicadaLocomotionController`，与 Lizard 后端只共享 Body、连接和 `ITerrainQuery`。双 chunk
> 身体按 RW 尺寸换算，固定序采用 `Body.Tick → Cicada Act`；支持完整 3D 输入、弱悬停锚、
> 显式地板/墙/顶停驻与失效复飞、30 tick 意图起飞、锁方向 Charge 和 20 tick Stunned。
> 四翼与四条触须只输出固定 tick 表现状态，不向身体回传力；`light` / `dark` 只靠出生参数分化。
> 独立 `scenes/cicada_sandbox.tscn` 默认装配 light，交互支持 WASD+Q/E、停驻选择与
> 起飞/Charge；`core/cicada_smoke` 和 `tools/run_cicada_matrix.sh` 覆盖固定哈希、40/400Hz、
> 微扰、三面停驻、起飞、撞墙与双预设。详细契约见 `docs/cicada_controller.md`。
>
> **RotationChunk 机制（M5 后追加，2026-07；≙ RW BodyChunk.rotationChunk 全套语义，反编译穷尽核实：全程序集 30 处 rotationChunk 引用 + 38 行 Rotation 读取）**：`BodyChunk.RotationChunk` 朝向参照 + 派生 `Rotation = (Pos−参照.Pos).normalized`（退化照抄 RW：null → Up ≙ 显式回落 (0,1)，两点近重合（模长 ≤1e-5 = Unity kEpsilon）→ 零向量 ≙ Unity normalized 原语义，消费端自行回退）；建 `ChunkConnection` 时两端自动互绑（≙ RW 构造副作用，后建覆盖、不分连接类型）；工厂装配完**显式钉定**脊柱（≙ RW Deer 构造后重申指向的先例）：头 → 髋（Rotation = 头髋长基线 = 全身轴前向）、中段 → 后一节（本段轴；3 节脊柱时即髋 ≙ RW 中→髋，四节以上不退化成跨关节长弦）、髋 → 头（指向后方，消费侧翻转）——不学 RW Lizard 靠「防折叠连接恰好最后建」的顺序巧合（我们的尾链建在最后，巧合会让髋参照尾根，软尾摆动污染步向）。消费端 = `LizardLocomotionController.TickLimbs` 每锚点步进方向（≙ LizardLimb `a = DirVec(rotationChunk→connection)` 后与目标 Lerp 0.4；髋锚翻转 ≙ `connection.index==2` 的 `a *= -1`，按锚点判定不写死索引）：头/髋锚 = 脊柱长基线轴，**与旧全局 stepDir 按 IEEE 逐位相等**（负号与除法可交换）——default/sprinter/heavy/wall/stand/carrot 六条矩阵哈希 + smoke 基线改动后逐位未动，自带对照组；唯 hexapod（中段锚腿对改跟本段朝向）按设计漂移换新基线。拓扑不进 `DeterminismHasher`（纯装配期引用）；smoke `[CORE-ROTATION]` 结构断言钉住互绑/覆盖/钉定不变量。出生摆位的世界 Z 侧向仅是一次性相位种子（出生脊柱竖叠、朝向退化竖直），运行时脚位全由每锚点 stepDir 接管。
>
> **SpineFollower 修复（RotationChunk 轮后追加，2026-07；多节脊柱爬墙 V 形折叠 bug）**：`LizardLocomotionController.ApplyLocomotionForce` 原先让链尾 `Hips` 直接追「目标点身后一节」，偏移量取 `SpineLength`（= 脊柱**全长**，头到髋各连接 RestLength 之和）——两节脊柱（Head/Hips 相邻）时这恰好退化成正确语义，三节以上（heavy/hexapod）时中间节完全没有驱动力，两条独立刚性连接在「头到髋直线距离 < 脊柱全长」的欠约束自由度上被动折成 V 形，且抓稳后重力关闭，错误姿态可稳定维持（反编译 `Lizard.cs:2277-2280` 核实根因：RW 原版只用 `bodyChunkConnections[0].distance`——**单节**长度——驱动 `bodyChunks[1]`，链尾 `bodyChunks[2]` 从不被直接追踪，只靠连接约束被动拖行）。修复：新增 `LizardLocomotionController.SpineFollower`（≙ `bodyChunks[1]`，工厂钉定为 `chunks[1]`）与 `HeadLinkLength`（单节长度）承接这个追踪力，`Hips` 恢复纯被动拖行。两节脊柱下 `SpineFollower` 与 `Hips` 是同一 chunk 且 `HeadLinkLength` 数值与原 `SpineLength` 相同——`default`/`sprinter`/`wall`/`stand`/`carrot`/`embed`/`wallside` 七条矩阵配置与 smoke 哈希逐位不变（no-op 有数学证明，非仅回归验证）；`heavy`/`hexapod`（三节脊柱）换新基线：路点数 heavy 6→8、hexapod 8→9（同 2000 tick），官方巡逻路线下头-中-髋夹角由折叠态稳定 ~53° 回升到稳态 ~177°（转弯/翻越瞬态低至 116°~151°，但数百 tick 内自行回直，不再像修复前那样滞留）。
>
> **SpineFollower 修复轮二（外部评审追加，2026-07；历史红灯记录）**：该轮完成 `straightenOut` 的 RW 对齐（判轴/施力点切到 `SpineFollower`）、补齐多节脊柱 wall 组合与文档契约，并由真断言暴露 `wall-hexapod` 的 82 tick 持续折叠。轮二当时关于「单纯 Hips 被地面钉住」「hexapod 腿拓扑无 RW 先例」的归因随后被逐 tick 证据反证；以下轮三记录为当前结论与已落地修复。
>
> **SpineFollower 修复轮三（墙角残留深挖与修复，2026-07）**：上一段保留的是修复前红灯记录；逐 tick 重跑后纠正了三点误判：当前最长窗口实际从墙边 180° 掉头开始，不是单一「t1290-1372 髋部钉地」；腿粒子不向身体回传反力，不会直接钉住 Hips；RW Caramel/SpitLizard 本就有 chunk 0/1/2 三锚六腿拓扑。真实链路是「尾链跨墙卡链点火 + 平面 180° 掉头 + `misLocal` 沿目标轴促使头髋互穿 + 拉直力受低抓地衰减 + 约束修正被墙地碰撞覆盖」。修复分层落地：① `misTarget` 只做目标对齐，本地折叠改沿 Head-Hips 弦向撑开；输入反转追加绕 `SupportNormal` 的确定性侧向转身，零输入清旧意图；② 移植 RW `straightenOutNeeded` 跨 tick 记忆，并新增独立于头速的 `SpineCornerStuckTicks`，所有计数强度显式 clamp；③ 接触法线用固定容量 `ContactManifold3D` 收集，非正交法线固定迭代投影，卡角 ≥10 tick 时只恢复碰撞相对松弛末**新增**的 SoftOnly 支柱违反，最终候选再做 MTD 穿透校验，不重跑整套约束、不重复摩擦；④ 同阈值启用 RW 式 `TerrainSqueeze`（只缩 Hips 地形有效半径，1→0.05，下限 0.025m）；⑤ 回归不再用跨品种统一 `<100°` 假门槛，改查支柱实际违反，并拆出 `turn-hexapod`/`wall-turn-hexapod`/`wall-tail`/`wall-corner` 四个事件相对场景；墙面掉头恢复期间必须留在目标墙，尾链逐节释放按首释放起算整个 episode，墙角接触只认目标墙的正确朝向脊柱 chunk，不再误把 Step 侧面算墙。两节脊柱走显式 legacy 分支，default/sprinter/smoke 基线不受新恢复控制器影响。
>
> **拖尾点失稳修复（wall-pose，2026-07）**：症状 = 上墙后髋不贴墙水平悬空、顶死推墙时髋摆到正侧方 ~80° 锁死成横向滑移步态（无引擎复现场景：顶死 + 1cm 侧向种子 40 tick 指数发散；顶死无种子则水平悬空姿态无限期冻结）。根因 = 推进拖尾点 `target + Dir(target→Head)×节长` 在 LookAhead(0.5) > 节长(0.3) 时**恒在头前 ~0.2m**：follower 注入是方向性定幅（不随距离缩放），刚性连接把 follower 锁在头周围一节长球面上，吸引子在球面内前侧 → 稳定平衡位是「髋在头前」，我们要的拖尾位是反平衡点，只靠头部运动的拖行（~v/L，满速时约失稳泵 3 倍）压制——头一顶死失稳泵独大。修复 = 拖尾点挪到头后：`Head + Dir(target→Head)×半节长`（吸引子挪进球面内背离目标侧，拖尾位变成稳定极点；系数不取 1.0 防 Dir 两点重合退化）。顶死时髋自行垂回拖尾位，正常拖尾姿态下 followerDir 几乎不变：平地 smoke 走距仅 -1.4%，wall 翻越 11→14、wall-hexapod 11→13 反升，default/sprinter/hexapod/carrot 路点同量级微降（新参考值见上方矩阵注释）。stand/embed/wallside 无移动意图不触推进力，哈希不变。同轮把 wall-turn 场景补上 carrot-turn 同款 45 tick 恢复预算门（修复后爬墙相位前移，第 11 次掉头挤进窗口尾部形成 pending 假红；墙顶棱线附近的 7 tick 离墙观测为髋搭上墙顶的过渡瞬态，非失稳），并把顶死+1cm 侧扰动与无扰动对照固化为 smoke `[CORE-WALL-POSE]` 真断言。复现中确认的两个次级问题（Fallback 胡萝卜可达性校验、站稳态雕像化）未修，独立成后续工作。
>
> **上墙弓起修复（RearBrace 链尾静息位回摆，2026-07）**：症状 = heavy 刚上墙的一段时间脊柱以中段为铰向墙外弓起、髋部悬在墙外 0.5~0.8m、尾巴悬浮成弧，随爬升缓慢回贴（最坏 1.5s）。无引擎逐 tick 取证 + 消融（最坏种子 spawn -3.0/接近偏角 10°，弓起深浅取决于撞墙时步态相位）定因四层：**形成** = 上墙交接期头被拉上墙而链尾无任何驱动（被地板摩擦 + 尾根回拉钉住），三节脊柱唯一自由铰只能折弯（两条路径：交接全松开动量压曲 / 头爬髋留几何折弯）；**变丑** = 折角落在 97°~150° 恢复死区（misLocal <120° 才介入、防折叠支柱 0.45m 折算 <97° 才触发，且两者都是沿头髋弦「方向盲」撑开，撑出来的是水平旗杆不是贴墙直线）+ 球体重叠视觉放大（半径 0.24/0.27/0.30 对节长 0.3）；**维持** = 尾根机械回拉（纯化消融：只把尾根 WeightA 置 0 与整条摘尾逐项同值——尾链 Footing 记账贡献为零）+ 后腿超出 JointDist 0.63m 可及圈被 FindGrip 逐 tick 全拒（旧 HuntPos 停在墙脚，最长 60 tick 无锚，抓地腿减半又反削唯一贴墙力）+ 抓稳关重力让悬臂力中性（把后半身摆回墙面的钟摆力正是被开关关掉的那个；RW 2D 侧视不存在「垂直于墙伸出」自由度——3D 化新增维度，无反编译对策可抄）；**恢复** = 爬行拖拽被动对齐（τ≈25 tick），髋进可及圈后腿一抓即拉平。修复走了两迭代，第一版被用户实测否决：**迭代一（废弃）**= 沿 -SupportNormal 朝最近支撑面吸的贴面伺服——推墙场景达标（重抓 +59→+30），但**上墙后停驶**时没有推进力抵消，把停在近直姿态（174°）的身体主动折进墙角（135° 锁死、髋吸到 0.30m 贴死）——「朝面吸」在悬臂几何里就是压链方向，缺的是「绕中段回摆」的切向分量。**迭代二（现行）**= `LizardLocomotionController.RearBraceGain`（0.15）：链尾朝「SpineFollower + 头→SpineFollower 轴 × 链尾当前半径」的直脊柱静息位回摆，施力前显式去径向（纯切向、不压缩链条、零地形查询、MaxMoveSpeed 余量钳制）——悬臂态它给出爬行拖拽本来要等 τ≈25 tick 才给的下摆，停驶态前段轴指哪就摆到哪、不会无中生有拉向墙。结构门 `Hips != SpineFollower` + `!ApplyGravity`——spine=2 链尾就是拖尾点驱动的 follower 天然免疫（default/sprinter 与 smoke ExpectedHash 逐位不变），坠落态交还重力；不碰 FindGrip 不造抓地。另试过翻越窗口（Crest）豁免，实测净负（回摆正是把链尾送过棱线的助力：豁免后 wall-heavy 8→6、heavy 巡逻 8→6——巡逻路线含翻越段，平地配置一并受损），不设窗口分支。效果：推墙最坏种子后腿重抓 +59→+28 tick、脊柱角回 160° 仅 +9 tick、此后不塌回（≥167°）；停驶终态 180°（修复前对照 174°、迭代一 135°）；carrot-turn 恢复 12~15→6~7 tick、中段领先 8.4°→0°；hexapod 巡逻 9→11 路点。代价：wall-heavy 翻越 9→8（-1，相位量级）。smoke `[CORE-HEAVY-MOUNT]` 双场景真断言（推墙：重抓 ≤45 / 角回 160° ≤40 / 不塌回 <140°；停驶：终态角 ≥165° / 髋离墙 ≥0.35m——钉死「朝面吸」类回归；增益归零精确复现 +59 红灯，门有效性已验证）。同轮平地 turn 驱动补 wall-turn 同款 45 tick 恢复预算门（掉头节奏前移后末次反转落进结尾窗口 pending 假红）。spine=3 十配置换新基线；遗留：形成期瞬时折角保留（合法瞬态）、后腿超距 FindGrip 过渡目标（还可砍 ~16 tick 脚程，须保持 HasGrip=false 不造假抓地）未做。
>
> **上墙前段转向修复（FrontMount，2026-07）**：RearBrace 解决了链尾弓起，却也暴露新的假绿——三节脊柱内角接近 180°，但头—第二节仍沿墙法向水平伸出，RearBrace 只会把髋排到这根错误轴后面，整条成为水平旗杆。`LizardLocomotionController.FrontMountGain=0.35` 只服务「多节脊柱、无中段腿」的 Heavy 类拓扑：Head 腿拿到本 tick 射线背书的陡墙落点即可用原始 `GripNormal` 预构造局部爬升方向，腿同步换步时仅以当前 `Head.TerrainContact+ContactNormal` 补位；SpineFollower 绕 Head 纯切向回摆，Head 同步沿面走，不沿法线吸墙、不驱动 Hips、不造抓地。预摆内部角到 120° 即停，真实接触后允许形成用户期望的 ≥90° L/斜线；前段进入沿墙 30°、`SupportNormal·localNormal≥0.8`、后段踩同面或 Crest 均熄火，成熟壁面换步不会重燃。强度按 `RunSpeed` 缩放，速度注入只补到目标值的 0.75，不逐 tick 累积冲量。44 组出生距离×接近角扫描：44/44 在真实墙接触后 15 tick 内稳定进入沿墙 30°，后腿重抓最坏 45 tick，最小内部角 97.7°、零深折叠；正撞最坏相位 mount 法向占比从 0.986 降到 0.809，交接最低角 129°。Godot `wall-heavy` 8→10 路点；Hexapod 因有中段腿而结构排除，wall/tail/turn 轨迹与旧基线逐位一致。smoke 同时覆盖 10°、0°、RunSpeed=0.25、停驶仍挂墙/不吸墙和后续展开，不能再只拿全身内角判美观。
>
> **秃鹫飞行控制器（VultureFlightController，2026-07）**：与 `LizardLocomotionController` 并列的飞行生物控制器（与 Spider/Centipede/Cicada 同为平行物种后端），共享 Body/地形原语互不引用。≙ 反编译 Vulture.cs/VultureTentacle.cs 全套语义（升力/拍翅/悬停/降落数值逐行核实换算，1px=0.025m）。与蜥蜴的根本路线差异：**重力常开**（无重力开关）——升力 = 与拍翅相位同步的 sin² 脉冲（谷值恰为 0），只注入**后脊柱单 chunk**，重力摊 4 个躯干 chunk，约束松弛摊分脉冲 ≈÷4 后周期均值与重力平衡（悬停上下颠簸是这套机制的直接后果，≙ RW 手感，不是 bug）；**下降不施向下力**，靠倒拨拍翅相位冻结在低升力半区滑翔（`FlapGlideRate` ≙ wingFlap −= 1/70）。身体 = K4 风筝刚架 3D 化（前脊柱 +0.4m/后脊柱 −0.25m/双肩 ±0.5m，六条 Rigid 全三角化，RestLength 取出生几何 = RW 26/40/22.36/25.61px 逐位同构；共面静息态的无穷小柔性 = 有机机体微弯）+ 头 PullOnly 拴绳（WeightA=0 ≙ weightSymmetry 0，头的重量拖不动身体）+ 头部伺服（RW 物理脖链的收缩：飞行 0.2/着地 0.75 父系阻尼 + 朝「前向×脖长」静息位吸，渲染层画脖曲线）。翅膀 = `VultureWing` 新类（**不复用 Limb**——plant-and-trail 是地面步态状态机，且该类被既有基线钉死）：段链粒子 + 只抗拉绳约束（≙ stiff=false）+ **对身体零回传**（≙ pullAtConnectionChunk=0，唯一例外 = 抓地悬挂拉力 (0.9L−d)×0.2）；`Flap` 模式 = 全局相位行波（快下拍 15 tick + 慢回收 25 tick = 1s 周期，翼尖滞后半周期，扫掠幅 5~15m 故意远超可达 → 伺服饱和截断才是有效机制）+ 翼根两节硬驱动；`Grab` 模式 = 射线找抓点（锚点直射 + 投影采样，允许天花板底面——秃鹫倒挂）→ 翅尖硬钉 → 逐节贴附计支撑。**无 locomotion 状态机**：模式在每只翅膀上，AirBorne/栖息从翅膀组合涌现；起飞/降落由 MoveTarget 几何触发（落点贴地探测 + 进入触发半径 → 全翅 Grab + AirBrake(30) + 5s 切换锁；栖息 + 远/悬空目标 → TakeOff + 30 tick 助推——RW 喷气推进器 jetFuel/Utility 系统的收缩）。输入契约与蜥蜴同名同义（MoveDir 为 **3D** 意图/RunSpeed/MoveTarget 直喂 + AtMoveTarget **带 1×/2× 迟滞**防悬停颤振；Shift/Teleport/Launch 同名移植）；注速一律 headroom 钳制（MaxFlySpeed/MaxRiseSpeed——RW 瞄路径格天然限速，连续胡萝卜必须显式封顶，同蜥蜴 MaxMoveSpeed 论证）。确定性：零随机（RW StuckBehavior 随机抖动不移植），Flap→Grab 需「刚架贴地形」或「持续 >10 tick 无意图」证据——翅尖刷墙/悬垂头擦地/换路点单 tick 空窗都不算（fly 路线三轮实测逼出来的门控）。品种四预设：`vulture`（基准双翅 8 节 5.5m）/`king`（×1.4 质量 10 节 6.75m 长翅）/`swift`（0.8 体格快拍短翅，原创）/`quad`（四翼 ≙ Miros 拓扑，LiftShare 1.4/翅数）；`VultureBreedParams` 与蜥蜴表平行不混表，沙盒数字行 9,0,-,= 续接（5~8 归蜈蚣）、`--breed=vulture|king|swift|quad` 自动分派生物类别（与 `--creature=centipede/...` 互斥）。回归：smoke 四断言（`[CORE-VULTURE-FLIGHT/LAUNCH/SHIFT/ASSEMBLY]`：2000 tick 起飞→巡航→悬停→降落栖息双跑 bit-exact + 哈希基线、击飞恢复、rebase 逐字段、四预设装配不变量）+ 矩阵四配置（`vulture`/`vulture-king`/`vulture-swift` fly 环线反复越 3m 薄墙 ≥21/24/28 路点 + `vulture-perch` 真降落断言）。直喂契约的飞行版教训（实测）：巡航路点须离地形 ≥ 降落贴地探测深度（1.2m，否则如实降落）、下降腿要给滑翔垂度留 ~1m 余量（否则擦墙顶）。落地即修的对抗性评审轮（四缺陷确认零误报）：① `AtMoveTarget` 迟滞态绑定具体目标（换点即复位——RW 原生 0.5m 格距下旧版会连环假到达）；② 悬停锚只在真到点时取喂点（零油门 + 远处残留目标曾以 ~89% 巡航速度绕过油门自动驾驶）；③ 升力注入不设 AirBorne 外层门（逐翅注入 ≙ RW，混合翅态下仍在拍的翅膀继续托身体——单翅失能侧倾涌现的前提）；④ 坠落自救补 `landingBrake<1` 门 + 收紧全翅 Grab（俯冲降落曾被自救掰回 Flap 再吃 5s 切换锁）。四条全部有 smoke 钉子（`[CORE-VULTURE-CONTRACT]`：密集喂点 10/10 无假到达、停车水平漂移 <0.5m、混合态波峰注入、俯冲 engage→吸附 ≤100 tick）。
>
> **人形运动后端（Humanoid，M5 后新增，2026-07；≙ 反编译 Scavenger 拾荒者，与蜥蜴并列的双足控制器类别）**：`core/Arm.cs` + `core/HumanoidLocomotionController.cs` + `core/HumanoidParams.cs`，共享层（Body/BodyChunk/ChunkConnection/Limb/LizardLocomotionController）**零改动**——20 条蜥蜴矩阵基线与 smoke ExpectedHash 逐位不变（改动前后矩阵输出除日志横幅逐字节 diff 为空，金标准③实证）。核心机制全部出自 Scavenger 反编译逐行核实：① **清醒近地 = 失重伺服木偶**（Scavenger.Act L2207 对三 chunk `vel.y += gravity` 是**抵消** chunk 级重力，不是施加——探索期曾读反方向，实测站立力偶在带重力语义下平衡倾角只有 79°、瘫平不起，改为「Conscious && Grounded → GravityScale 0」后全部行为涌现归位）；② 站立 = 姿态误差非线性力偶 `WeightedPush(胸,髋,up, LerpMap(dot,-1,1,5.5px,0.3px))` + 头部双伺服 + 髋高度伺服（拉向地面上方 RideHeight，≙ 拉向格中心）——摔倒/爬起/眩晕瘫倒全由 `Conscious` 开关与力偶存在性涌现，**零 knockdown 状态机**（击飞后 ~31 tick 自动爬起；注意确定性内核里完全对称的直立杆是不稳定平衡点，「击晕倒地」需配一次轻推）；③ **手臂两条独立通道**（RW 分层原样移植）：物理层 `KnucklePos` 撑点（胸前探地射线，plant-and-trail 的手版）驱动腾空俯仰泵 = 指关节行走的真物理推进辅助；肢体层 `Arm` 粒子（三模式 Dangle/HuntAbsolute/HuntRelative + ConnectToShoulder(adaptVel 0.4/exaggerate 0.1 甩动感) + 腋窝排斥 + 帧末速度参数复位原语）纯可视化不回传力；④ **手臂优先级链**（≙ ScavengerHand.Update 扁平 if-else）：昏迷→投掷→蓄力→指向→持物→撑地锁点→闲置垂手，链尾统一臂长约束；扇扫（10 根 0..±25°）每 tick 只允许一只手（奇偶交替，预算+确定性双保，人形合计 17.4 射线/tick 比蜥蜴还省）；⑤ 非移动 API：`PointTarget`（指向 sin 伸缩）、`Carrying`（主手携带位，被持物由宿主硬钉 `MainHandPos` ≙ grasp 0）、`StartThrowCharge/ReleaseThrow`（蓄力强制停驶+身后蓄力位抖动→出手返回初速+头甩，物体归宿主）——RW 的随机抖动全换 `sin(TickIndex)` 相位。三预设 `AllHumanoids()`（独立路由表不进 AllBreeds()）：**scavenger**（忠实换算基线，2000 tick 巡逻 26 路点）、**brute**（魁梧重型，深弓背猛冲，23 路点）、**waif**（瘦小敏捷近直立小快步，33 路点）。沙盒：`--species=humanoid`（+`--route=hwalk|hact`/`--stun=T,D`，与 `--creature=`/秃鹫 `--breed=` 互斥）、下拉框换人形品种（数字行 12 键已被蜥蜴 1~4/蜈蚣 5~8/秃鹫 9,0,-,= 占满）、交互键 P=指向/C=持物/T=按住蓄力松开投掷；`HumanoidSandboxDriver`/`HumanoidRenderer` 独立于蜥蜴路径。矩阵 +8 配置（含 humanoid-40 时基不变性；哈希基线在 run_matrix.sh 顶部 HASH_HUMANOID_*），smoke +6 断言（[CORE-HUMANOID-DET/STAND/STUN/ACT/SHIFT/GRAVITY]，基线 `HumanoidExpectedHash`）。
> **人形评审修复轮（同月，多 agent 对抗评审 12 条核实命中）**：两条 HIGH 均在重力开关——① 撑地探针法线门槛曾取 0.3，与滑墙 0.5 判据间留 60°~72.5° 争议带：陡面被当地面 → 失重+髋伺服钉面+前置探地节节抬高 = **65° 陡壁全速爬升 49m 永久悬挂**（与「不爬墙」声明直接矛盾）；② `contact` 项曾不看接触法线：贴 3m 竖墙的胸接触攒 GroundedCounter，击飞撞墙半空反复失重成粘滞滑降。修复 = 「可站立地面」统一判据 `IsGroundNormal`（≥0.5，与墙分类同一条线不留争议带）套住探针与 chunk 接触两处。其余：前置探针 miss/被拒补原位回退射线 + 蓄力停驶时不前置（崖边瞄准曾 200 tick 翻转 30 次、顶墙探针陷墙永不收敛）；`Arm.ConnectToShoulder` 的 Dangle 分支断开 Exaggerate/AdaptVel 肩速两项（≙ RW Dangle 传零——垂摆不是僵直随行，此项为唯一有意行为变更，humanoid 基线随之换新）；`ReleaseThrow` 补 `!Conscious` 守卫（同帧置昏不能再拿出手向量）；`Shift` 补平移 `MainHandPos`（宿主钉持物的观测量）；指向 sin 相位 `TickIndex % 62832` 防长驻宿主两天后 float 量化抽搐；沙盒 CLI 品种名硬校验（打错静默回落=假绿）+ `--stun/--yank` 越界硬拒（事件没发生断言静默蒸发=假绿）。全部修复由 smoke [CORE-HUMANOID-GRAVITY]（65° 坡不可爬 + 贴墙击飞零半空失重，HalfSpaceTerrain 解析地形）与 STUN/SHIFT 扩展断言钉死。
> 遗留：斜坡/台阶通过靠悬浮伺服前置探地（0.3m lead），更陡地形未覆盖（60° 以上现在正确地不可站立）；手臂在爬杆/荡杆（RW Climb/Swing）不在范围（本仓无杆地形）；崖边探针回退在 smoke 无有限地板地形，靠评审复现验证未固化。
> **行走弓背与后仰摆修复（WalkLean 轮，用户白盒实测驱动，2026-07）**：起点是用户两条观感反馈——「行走像坐着」与「躯干向移动**反**方向倾斜 + 周期性抖动」。带符号探针（此前只看无符号 `meanUpright`，看不出前后倾方向——教训）证实后者：平地直行胸恒在髋后 0.18~0.37m（≈40° 后仰）、21 tick 周期摆。根因 = **knuckle 俯仰泵的腾空门槛移植错了**：RW 原门「胸髋双双无地面接触」= 真弹道腾空（走路不点火），曾错用「双脚都没抓稳」当等价物——双足步态每步都有双摆相，泵在正常行走 60% 时间点火，且撑点一落到胸后（无符号距离 ≈ 近端 → t≈1）恒输出「髋前甩/胸后压」的后仰力矩。修复 = 门改 `!Grounded`（近地探针即本内核「有地面支撑」的语义载体）。修后弓背方向/深浅的**消融实证**（scratchpad humanoid_probe，ablate/tune 场景）：巡航前倾的真实来源是 **Chest/Hips 阻尼差**（0.9/0.8 → 胸恒比髋留速多，刚性杆把速度差转成前倾；拉平即直立）；同轮补的 `LeanPush`（≙ L1956）满油门时被推进余量钳制**完全吸收**（净效果零，保留为 RW 保真 + 低油门段有效，注释已如实标注）；`HeadLean`（≙ L1957 行进中头探在胸前方）真实有效（头前探 +0.43m，其中 +0.24 是它的贡献）。预设按真杠杆（阻尼差）调深浅：brute 0.88/0.72（最深 up≈0.82）、scavenger 0.9/0.8（≈0.85）、waif 0.92/0.9（近直立 ≈0.95；HeadDamping 0.82→0.88 同轮修——头伺服最大牵引必须 ≥ (1-HeadDamping)×巡航速度，否则头追不上巡航退化成被脖子拖行垂在胸后）。修后平地直行 lean 稳定 +0.24m 前倾、零抖动（min/max 差 0.001）。行为面：路点 27/27(yank)/23/34 全面持平或 +1，smoke 六断言与 GRAVITY 门原样绿；`humanoid-stun`/`humanoid-act` 哈希逐位不变（站桩类零介入对照组）。基线换新：`HumanoidExpectedHash` + 矩阵 4 条人形哈希；蜥蜴 20 条逐位不变。停驶收脚（坐姿的另一半根源：脚恒在髋前 ≥0.2m 的 plant-and-trail 换步阈值 + 平地永不触发 IdlePose）与 FeetDown 调参未做，见上方遗留。
>
> **双手撑地重合修复（HandSpread 轮，用户白盒实测驱动，2026-07）**：症状 = 行走中两只手撑地时锁到同一个世界点完全重合。根因 = 扇扫分离的 3D 化几何错误：RW 原版两手分离靠**扫描角 ±4° 偏置**（`CheckForGrabPos` 的 `limbNumber==0?-4°:+4°`——2D 侧视唯一的分离维度）；移植时换成「起点沿侧轴错开」，但射线仍瞄同一个 `KnucklePos`——kp 本身落在地面上，朝单点**汇聚**的射线让起点侧向错开在命中处被精确抵消，两手 `GrabPos` 逐位相同；`Arm` 只有手-胸腋窝排斥、没有手-手分离项，锁点吸附把重合稳定维持（双手**同时**撑地本身是 RW 保真的，错的只是同点）。修复 = 分离做在**目标点与扫描角**上：扇扫中轴改瞄 `kp + right·Side·KnucklePlantSpread`（0.2m——落点左右分开 ~0.4m，斜面最坏收缩仍 >0.24m）+ 补回 RW 原版每手 ±4° 俯仰偏置（`KnuckleScanBiasDeg`，落点前后交错）。改动只落 `TryFindGrabPos` 一处（人形独享路径）。smoke `[CORE-HUMANOID-HANDS]` 真断言钉死：平地直行双手同锁相位 ≥100 tick + 锁点最小间距 ≥0.15m（两常量归零精确复现 minPlantSep=0.000 红灯，门有效性已验证；修后实测 0.458m）。行为面：路点 27/27/23/34 与修前持平，`humanoid-stun`/`humanoid-act` 哈希逐位不变（站桩对照组），蜥蜴 20 条逐位不变；基线换新：`HumanoidExpectedHash` + 矩阵 4 条人形哈希。
>
> **头部掉头响应修复（HeadTurn 轮，用户白盒实测驱动，2026-07）**：症状 = 行进中掉头，胸部很快转向而头要「好一会儿才慢慢靠过来」。无引擎 reverse 场景（scratchpad humanoid_probe，巡航 300 tick → 瞬间 180° 反转，量化胸翻转 vs 头横越/就位时差）证实：胸 2 tick 完成翻转，头 80 tick 才横越、**110 tick（2.75s）才就位**。力学根因 = **头伺服在巡航中恒饱和**：满速巡航仅维持头前探所需的每 tick 修正 ≈0.125m 恰好等于 `HeadServoRange` 钳制（RW 5px 直译），掉头时零牵引余量——头只能靠 PullOnly 脖子拖行横越。修复两旋钮（8 组合参数扫描选定，全组合零振铃、零站姿抖动）：① `HeadServoRange` 0.125→0.25（**主杠杆**，有意偏离 RW 原值：只翻倍远场牵引，近场刚度「力 ∝ 距离×增益」与站姿稳定性不变，单它就 110→22 tick）；② `HeadDamping` 三预设统一 0.88（scavenger/brute 从 RW 原值 0.8 提高，waif 已是——削掉巡航拖拽预算，叠加后就位 **14 tick（~0.35s）**）；`HeadServoGain` 保持 RW 原值 0.16 不动。三预设就位 14/14/16 tick；巡航头前探 scav 0.433→0.487、brute 0.428→0.473 略深，waif 不变。行为面：路点 27/27/23/34 持平，smoke 全断言绿（击飞爬起 31→30 tick 同量级）；本轮阻尼在站桩姿态也生效，`humanoid-stun`/`humanoid-act` 哈希一并换新（对照组豁免不适用），六条人形基线 + `HumanoidExpectedHash` 全部更新；蜥蜴 20 条逐位不变。
>
> **双足高频蹭步修复（LegGait 轮，用户白盒实测驱动，2026-07）**：症状 = 行走（brute 最显眼）双腿高频低距离往前蹭而非正常迈步。gait 探针量化：步频锁死 5~7 tick/步（brute 每秒 8 步）、步幅仅腿长 0.4~0.7 倍、**释放时脚还在髋前**（brute +0.33）、brute 触地 3 tick 永远达不到 `LegGripDelay=4` 的 Gripping 判定（抓地占比恒 0）。根因 = 蜥蜴 `Limb` 的 oldestGrip 步态错开（「其余腿全抓稳→本腿松开」）在两腿拓扑下退化成「对脚一落地本脚即松」：触地被锁死在对腿落地延迟、与身体速度无关；释放时超前量立即高于重新迈步阈值 → 下 tick 直接再找落点——高频小碎步正反馈。RW `ScavengerLeg` 反编译核实**根本不用成对协调**：独立前瞻点循环（`IdealPos = 髋 + clamp(髋速×10, 腿长)`，锁点离 IdealPos 超腿长才松、松开即重找无摆动期门槛、FindGrip 搜索半径以 IdealPos 为心——锁点允许暂超腿长靠 ConnectToPoint 拖住）。修复 = `Limb.LookaheadTicks` opt-in（默认 0 = 蜥蜴路径逐位不变；人形工厂设 10 ≙ RW 字面量）：flag 路径换前瞻点释放 + 可及判定改以 IdealPos 为心全腿长半径（以锚点为心会拒掉全部前伸落点，waif 曾被压成 4 tick 碎步），跳过 trail/oldestGrip/extraLongStep 三段。**支撑保序 guard**（确定性内核对 RW 环境噪声的显式等价物，先例 = 随机抖动→sin 相位）：对脚踩稳才允许本脚松开 + 腿表 tick 顺序打破同 tick 双到期（先 tick 腿松开清零计数、后 tick 腿持稳半拍）——两脚 FindGrip 目标前后对称，站定时落到同一 x、之后同起同落成跳步（逐 tick 日志证实），guard 反相自锁、无周期漂移；1.75×腿长失效阀防对脚长期找不到点时本脚被拖行钉死。修后：brute **14 tick/0.82m（1.16×腿长）**触地 10 tick 抓地 50%、scavenger 9t/0.62m、waif 7t/0.64m（回到它本来正常的值）；双悬空 0%、单脚支撑期 44~58%、释放点回髋附近（−0.00~+0.06）。行为面：路点 27/27/23/34 持平，smoke 全断言绿。smoke `[CORE-HUMANOID-GAIT]` 真断言钉死步幅/周期/触地/释放位置/抓地占比/零双悬空 + brute Gripping 专项（LookaheadTicks 归零精确复现全套修前数字红灯，门有效性已验证）。六条人形基线 + `HumanoidExpectedHash` 换新；蜥蜴 20 条 + smoke ExpectedHash 逐位不变（flag 默认 0 数学保证 + 矩阵实证）。
>
> **3D 朝向边界**：`BodyChunk.Rotation` 只是一根 forward 方向，不是完整旋转或局部坐标系。渲染/附着物须结合稳定 up（通常取 `SupportNormal`，必要时沿用上一帧 up）构造 Basis/Quaternion；forward 与 up 近共线时显式选备用 up，避免 roll 突跳。RW 的 2D 单方向向量可唯一确定平面旋转，移植到 3D 后必须补上这层宿主语义。
>
> **单位约定**：1 RW tile (20px) = 0.5 m；`Vel` 语义 =「米/tick 位移」（积分 `Pos += Vel` 不乘 dt，内核零 delta 依赖）；重力默认 36 m/s²（= RW 0.9 px/tick² 直接换算），`GravityPerTick = 36×0.025² = 0.0225`。
>
> **确定性回归**（改物理内核后必跑；全部真断言——探针只打印不判定的旧形态是假绿，评审修复轮的教训）：
> ```bash
> # ① 无引擎冒烟（秒级，最快反馈）。退出码即判定：双跑 bit-exact + 哈希对基线（钉死在
> #    Program.cs ExpectedHash/ExpectedVultureHash/HumanoidExpectedHash）+ 里程/约束收敛/
> #    无 NaN + 嵌入恢复 + Shift 连续性 + MoveTarget 直喂契约 + RotationChunk 拓扑 +
> #    wall-pose 顶死稳定性 + heavy 上墙回摆收敛（推墙+停驶双场景）+ 深卡角/非正交接触边界 +
> #    蜈蚣装配/显式头尾切换/表面课程/固定头下阶梯/脚跨墙恢复/生命周期/自避/查询增长 +
> #    秃鹫（飞行全流程/击飞恢复/rebase/装配不变量/评审修复契约）+
> #    人形八断言（DET 双跑/STAND 失重悬停与击飞爬起/STUN 瘫倒苏醒+昏迷弃掷/
> #    ACT 指向持物蓄力出手/SHIFT 三 API 完备性/GRAVITY 陡壁不可爬+贴墙不计撑地/
> #    HANDS 双手撑点分离不重合/GAIT 双足步幅周期触地与零双悬空+brute 抓地专项）+
> #    TypeRef 边界扫描。
> dotnet run --project core/smoke
>
> # 蜈蚣无引擎基线：short=655A21496C00E86A，long=59CBCF993DF8ACD8；
> # 既有 Lizard=AAA0E4963668E5DC 不变。
>
> # ② Godot 全矩阵（分钟级）。45 配置 × 硬断言（哈希基线/路点下限/[RESULT] 判定/位置检查/
> #    防折叠支柱持续违反与事件相对恢复门），pipefail + 退出码聚合，任何一项红 → 非零退出；
> #    结尾打 MATRIX GREEN/RED：
> ./tools/run_matrix.sh [输出目录]
> # 蜥蜴配置：default ×2（双跑 diff）、default 40Hz（时基不变性）、perturb（灵敏度：哈希必须变）、
> #   wall（正面推墙翻越 ≥9）、wall-heavy/wall-hexapod（多节脊柱贴墙且分别 ≥7/≥9 路点）、
> #   turn-hexapod（平地 180° 掉头）、wall-turn-hexapod（竖墙沿面掉头）、wall-tail（尾链释放后身体恢复）、
> #   carrot-turn-heavy/carrot-turn-hexapod（行进中 External 胡萝卜侧转约 90°，量化中段领先）、
> #   wall-corner（目标墙首次接触换面）、stand（站桩+闲置姿态）、
> #   carrot（MoveTarget 路径点直喂通路）、heavy/sprinter/hexapod（品种默认巡逻路线）、
> #   embed（出生嵌入 60 tick 必须脱困）、wallside（贴墙擦边不得穿墙）、
> #   vulture/vulture-king/vulture-swift（秃鹫 fly 环线反复越墙，飞行占比 ≥80% + 越墙高度）、
> #   vulture-perch（空中路点后地面目标：真降落吸附 + 终态栖息断言）。
> # 蜈蚣 13 项：四预设巡逻 + short 双跑/40Hz/微扰 + short/long 全向课程 +
> #   armored 固定头下阶梯 + long 固定 End 窄墙前向翻越 + long 嵌入恢复/擦墙；
> #   课程须完整通过地面、斜坡、内角墙、墙顶、外墙与天花板，下阶梯须固定 Start 且
> #   始终只给 +X；窄墙完成后停驶至少 80 tick 再检查终态。
> # 人形 8 项（--species=humanoid）：humanoid ×2（hwalk 坡→平地→跨台阶巡逻 + 双跑 diff）、
> #   humanoid-40（40Hz 时基不变性）、humanoid-yank（行进中击飞限时回正续走）、
> #   humanoid-stun（昏迷瘫倒+苏醒爬起）、humanoid-act（指向→持物→蓄力停驶→出手动作脚本）、
> #   humanoid-brute/humanoid-waif（变体巡逻）。
> # [RESULT] 在进程 teardown 之前打印；已知 Godot 4.7 macOS 偶发退场 mutex 崩溃（exit 134），
> #   判定以 [RESULT] 为准（脚本已处理）。单配置手跑仍是
> #   $GODOT --headless --path . --log-file /private/tmp/godot_codex.log --fixed-fps 40 -- \
> #     --determinism=2000 --tps=400 [--route=…|--breed=…|--spawn=…|--yank=…|--expect-hash=X16]
> # 2000 tick 参考值（FrontMount 轮后）：default 11 路点、wall 14 翻越、heavy 7 路点、
> #   sprinter 15 路点、hexapod 11 路点、carrot 25 路点；wall-heavy 10、wall-hexapod 13；
> #   秃鹫（fly 环线）：vulture 29、king 32、swift 37 路点（800 tick perch：降落 ~t255）；
> #   人形（hwalk）：humanoid 27 路点、humanoid-brute 23、humanoid-waif 34。
> # 当前 45 项 = 20 项 Lizard + 13 项 Centipede + 4 项 Vulture + 8 项 Humanoid，完整矩阵已 GREEN。
>
> # ③ 抽离/移植类改动的金标准：改动前后各捕获一次全矩阵输出，逐字节 diff 为空（M5 即以此验收）。
> # 可执行基线真相源：tools/run_matrix.sh + core/smoke/Program.cs
> # （ExpectedHash 蜥蜴 / ExpectedVultureHash 秃鹫 / HumanoidExpectedHash 人形）+
> # core/smoke/CentipedeSmoke.cs。有意改内核时更新对应真相源，别处一律引用不复制。
> ```

## 6. 环境

- **Godot（mono/C#）**：`/Applications/Godot_mono.app/Contents/MacOS/Godot`
- **.NET**：dotnet 10 SDK
- **Godot CLI 日志**：执行任何 Godot CLI（尤其 `--headless`）必须显式追加 `--log-file /private/tmp/godot_codex.log`（沿用主项目约定）。
- 本项目为**独立 git 仓库**（`master` 分支，首次提交已完成）。

## 7. 约定

- 逻辑（物理/腿）跑在**固定步长**内，渲染在 `_Process` 里按累加器余数**插值**——从 M1 起就照这个结构搭，别让画面帧率污染物理。
- **物理/腿逻辑与渲染解耦**；脚落点走连续射线，寻路（若引入）走粗网格，两者分开。
- 尽量**镜像 `random-room-runtime` 的概念与命名**，降低回迁翻译成本。
- 编码准则沿用主项目：**想清楚再写、最简实现、外科手术式改动、匹配既有风格**。
