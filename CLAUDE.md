# proc_anim_lab —— 3D 程序化生物动画实验室

> Godot 4.x / C# 的独立沙盒项目。**目标：从零实现一套 3D 版"雨世界式"程序化生物动画/运动系统；等它在这里成熟后，整体移植回 [`random-room-runtime`](../random_room/random-room-runtime/) 的怪物系统。**
>
> 当前状态：**M5 移植接口完成 + 外部评审修复轮（2026-07）完成**——内核抽离为独立程序集 `core/ProcAnim.Core`（TypeRef 边界扫描 + 无引擎冒烟双重解耦实证），回迁契约就位（[`docs/porting_contract.md`](docs/porting_contract.md)）。评审修复轮补齐：球体穿透碰撞语义（接缝第二原语）、卡链释放、输入死区/抓握/限速语义统一、宿主 Shift/Teleport/Launch 接线 API、断言化回归矩阵（`tools/run_matrix.sh`）。准确状态：**内核抽离完成 + 集成契约就位**；「默认集成姿态」的闭环在主仓接线后验证（契约 §4.1/§8.3）。
>
> 2026-07-21 墙角残留深挖轮已完成：多节脊柱持久拉直、确定性掉头、局部卡角/terrainSqueeze、接触可行锥结构恢复与四条事件相对回归均已落地；历史红灯说明保留在下文，最终状态以下一段「修复轮三」与当前矩阵为准。

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
3. **腿的"IK"极简**：每条腿是**一个追目标点的粒子**（`vel = Lerp(vel, 朝目标*huntSpeed, quickness)` + 吸附），**不是解析式多关节 IK**。
4. **走路 = plant-and-trail**：脚踩住不动 → 身体前移 → 脚相对落后超阈值 → 找新落点 → 再踩住；腿长用距离约束维持。
5. **脚落点 = 射线打真实 3D collider**（**不拆网格、不重造格子碰撞**，见研究文档 §12）；Godot 用 `PhysicsRayQueryParameters3D`。
6. **locomotion 模式靠涌现**：抓着可达地形 → **重力关成 0**（不会掉墙）；走/爬在腿与身体两层**都无分支**，只有"抓住/没抓住"这一个开关。玩家式**显式状态机**仅在需要精细操控的角色上才用。（雨世界的"入水→腿垂下"游泳态本项目不做，见 §4。）

## 3. 相关文档（本项目内引用）

- **[`docs/rainworld_procedural_animation_research.md`](docs/rainworld_procedural_animation_research.md)** —— **核心参考**。雨世界程序化动画系统深度研究 + **反编译实证（§11 代码级：BodyPart/Limb/LizardLimb/TailSegment/TerrainCurve 等）** + **Godot 移植策略（§12：为什么用射线而不是细网格）**。
- **[`docs/porting_contract.md`](docs/porting_contract.md)** —— **M5 产物**。`ProcAnim.Core` → `random-room-runtime` 回迁契约：模块清单与依赖面、装配/驱动/输入/输出四契约、`ITerrainQuery` 接缝语义、确定性守则与三层回归、两条迁移路线与两种集成姿态（含主项目对接面调研快照）。
- **[`docs/rainworld_creature_taxonomy.md`](docs/rainworld_creature_taxonomy.md)** —— **反编译实证**：雨世界生物分类地图（92 物种 / 54 个 `Creature` 实现类）。三条正交分类轴、`Creature`+`BodyPart` 继承树、七大身体架构（含每类的 chunk/connection/肢体统计）、模板参数抽样。扩多节脊柱或多节腿前先查这里的先例。
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
- **不做**：**游泳 / 水中运动（本项目明确不涉及）**、关卡生成、玩法、锁钥、UI、正式美术——那些留在主项目。
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
> **3D 朝向边界**：`BodyChunk.Rotation` 只是一根 forward 方向，不是完整旋转或局部坐标系。渲染/附着物须结合稳定 up（通常取 `SupportNormal`，必要时沿用上一帧 up）构造 Basis/Quaternion；forward 与 up 近共线时显式选备用 up，避免 roll 突跳。RW 的 2D 单方向向量可唯一确定平面旋转，移植到 3D 后必须补上这层宿主语义。
>
> **单位约定**：1 RW tile (20px) = 0.5 m；`Vel` 语义 =「米/tick 位移」（积分 `Pos += Vel` 不乘 dt，内核零 delta 依赖）；重力默认 36 m/s²（= RW 0.9 px/tick² 直接换算），`GravityPerTick = 36×0.025² = 0.0225`。
>
> **确定性回归**（改物理内核后必跑；全部真断言——探针只打印不判定的旧形态是假绿，评审修复轮的教训）：
> ```bash
> # ① 无引擎冒烟（秒级，最快反馈）。退出码即判定：双跑 bit-exact + 哈希对基线（钉死在
> #    Program.cs ExpectedHash）+ 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 +
> #    MoveTarget 直喂契约 + RotationChunk 拓扑 + wall-pose 顶死稳定性 +
> #    heavy 上墙回摆收敛（推墙+停驶双场景）+ 深卡角/非正交接触边界 + TypeRef 边界扫描。
> dotnet run --project core/smoke
>
> # ② Godot 全矩阵（分钟级）。20 配置 × 硬断言（哈希基线/路点下限/[RESULT] 判定/位置检查/
> #    防折叠支柱持续违反与事件相对恢复门），pipefail + 退出码聚合，任何一项红 → 非零退出；
> #    结尾打 MATRIX GREEN/RED：
> ./tools/run_matrix.sh [输出目录]
> # 配置：default ×2（双跑 diff）、default 40Hz（时基不变性）、perturb（灵敏度：哈希必须变）、
> #   wall（正面推墙翻越 ≥9）、wall-heavy/wall-hexapod（多节脊柱贴墙且分别 ≥7/≥9 路点）、
> #   turn-hexapod（平地 180° 掉头）、wall-turn-hexapod（竖墙沿面掉头）、wall-tail（尾链释放后身体恢复）、
> #   carrot-turn-heavy/carrot-turn-hexapod（行进中 External 胡萝卜侧转约 90°，量化中段领先）、
> #   wall-corner（目标墙首次接触换面）、stand（站桩+闲置姿态）、
> #   carrot（MoveTarget 路径点直喂通路）、heavy/sprinter/hexapod（品种默认巡逻路线）、
> #   embed（出生嵌入 60 tick 必须脱困）、wallside（贴墙擦边不得穿墙）。
> # [RESULT] 在进程 teardown 之前打印；已知 Godot 4.7 macOS 偶发退场 mutex 崩溃（exit 134），
> #   判定以 [RESULT] 为准（脚本已处理）。单配置手跑仍是
> #   $GODOT --headless --path . --log-file /private/tmp/godot_codex.log --fixed-fps 40 -- \
> #     --determinism=2000 --tps=400 [--route=…|--breed=…|--spawn=…|--yank=…|--expect-hash=X16]
> # 2000 tick 参考值（FrontMount 轮后）：default 11 路点、wall 14 翻越、heavy 7 路点、
> #   sprinter 15 路点、hexapod 11 路点、carrot 25 路点；wall-heavy 10、wall-hexapod 13。
>
> # ③ 抽离/移植类改动的金标准：改动前后各捕获一次全矩阵输出，逐字节 diff 为空（M5 即以此验收）。
> # 基线哈希只存两处：tools/run_matrix.sh 顶部哈希表 + core/smoke/Program.cs ExpectedHash。
> # 有意改内核 = 同一提交里更新这两处；别处（含本文件）一律引用不复制。
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
