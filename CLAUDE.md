# proc_anim_lab —— 3D 程序化生物动画实验室

> Godot 4.x / C# 的独立沙盒项目。**目标：从零实现一套 3D 版"雨世界式"程序化生物动画/运动系统；等它在这里成熟后，整体移植回 [`random-room-runtime`](../random_room/random-room-runtime/) 的怪物系统。**
>
> 当前状态：**M5 移植接口完成 + 外部评审修复轮（2026-07）完成**——内核抽离为独立程序集 `core/ProcAnim.Core`（TypeRef 边界扫描 + 无引擎冒烟双重解耦实证），回迁契约就位（[`docs/porting_contract.md`](docs/porting_contract.md)）。评审修复轮补齐：球体穿透碰撞语义（接缝第二原语）、卡链释放、输入死区/抓握/限速语义统一、宿主 Shift/Teleport/Launch 接线 API、断言化回归矩阵（`tools/run_matrix.sh`）。准确状态：**内核抽离完成 + 集成契约就位**；「默认集成姿态」的闭环在主仓接线后验证（契约 §4.1/§8.3）。

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
> **M2 产物**：`scripts/physics/Limb.cs`（腿粒子：单点追目标 IK + plant-and-trail 状态机 + 竖直投影射线 FindGrip + 单侧腿长钳制，≙ RW Limb/LizardLimb）、`scripts/physics/Walker.cs`（行走驱动：推进力 ∝ 抓地腿数，无 locomotion 状态机，MoveDir/RunSpeed 唯一输入，≙ Lizard 移动块）、`scripts/physics/SphereTerrain.cs`（Body/Limb 共用的球-地形解算）；沙盒 WASD 行走 + 拖拽看腿自适应，脚球按状态换色（绿抓稳/橙迈步/灰蓝摆动）。确定性模式改为脚本化路点巡走（行走本身进哈希）：800 tick 走约 25.7 m、平均 2/4 腿抓地。
>
> **M3 产物**：走/爬两态涌现，零模式分支（≙ 研究文档 §11.6b/§12.3）。`Walker`：重力开关（`FootingCounter`/`NoGripCounter` → `ApplyGravity`：抓稳→重力 0 + 贴地摩擦档 0.8/0.5，坠落→重力回归 + 0.999/0.3，数值直取 RW）、支撑法线 `SupportNormal`（抓地腿抓握面法线的平滑平均：平地=上、墙=墙法线）、移动意图被支撑面挡住的分量沿面内上坡重定向（推墙自动变向上爬，同一公式覆盖斜坡）、推进目标射线钉在支撑面 + `RideHeight`（≙ 瞄路径格中心；身体不飘离墙的真正来源）、引擎极速 `MaxMoveSpeed`（墙面无阻滑升的唯一刹车——平地上碰撞/腿阻先饱和，撞不到它）。`Limb`：整套步进几何跑在支撑系（up=支撑法线：走=朝下打、爬=朝墙打，同一条代码）、FindGrip 三类候选统一选「离期望点最近」（支撑向投影 + 锚点直射【面前有墙先够到墙面】+ 攀爬中世界向下投影【翻越棱线够到顶面；起点必须沿「支撑法线的水平反方向」单侧排开——沿步进方向排会叠成悬在墙面外的竖线，薄墙顶面永远打不到】）、`HasGrip` 区分真落点与摆动期空中目标（修掉 M2 潜伏 bug：脚追上空中胡萝卜也计抓地，M3 里它会骗重力开关悬空飞天）。
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
> - `BodyFactory`（≙ `LizardBreeds`）重写为通用装配器：脊柱 = N chunk 的 Rigid 链（头…中段…髋，`Walker` 构造改显式 head/hips 引用 + `SpineLength`）、腿对沿脊柱均匀分布锚定（相邻对出生错位相反 = 对角步态相位种子）、尾巴 = 渐细 PullOnly 链（WeightA 沿链递减）。四预设：**default**（M2~M3 调教的基线四腿，取值与旧硬编码逐位一致）、**heavy**（绿蜥系：3 节脊柱×1.2 体格、宽站距、硬长尾、`SmoothenLegMovement=false`）、**sprinter**（黄蜥系：0.85 体格快腿短尾）、**hexapod**（3 脊柱 3 腿对，RW 无对照）。沙盒数字键 1~4 现场换品种、`--breed=` 供无头回归。
> - **闲置休息姿态**（≙ RW `Limb.Mode.HuntRelativePosition`，清偿 M3 遗留）：连续 `IdleAfterTicks` 找不到落点且 RunSpeed≈0 → `IdlePose`，追逐目标每 tick 切「锚点沿支撑方向垂下、向本侧微撇」的休息位，脚垂回身侧；有输入立即恢复迈步。`HasGrip` 恒 false 不骗重力开关；有移动意图时整套逻辑休眠（默认路线哈希不变）。回归：`--route=stand --spawn=-6,3.7,0` 空降墙顶站桩，悬空侧双脚应 `idle=True` 收拢。
> - **防折叠支柱**（≙ RW `Lizard.bodyChunkConnections[2]`，用户实测逼出来的）：距离约束链没有弯曲刚度——头-中、中-髋两条距离都满足时链条照样可 180° 对折，heavy 刚上墙时头会折到中段下面钻着爬。RW 蜥蜴的对策是第三条「头↔第三节」Push-only 连接：`RestLength = 节长×(1+bodyStiffnes)`，伸直（2 节长）永不触发、折叠低于下限才软推撑开 = 参数化的最大折角钳制。移植为 `BreedParams.BodyStiffness`（粉蜥 0.2/绿蜥 0.5/蓝蜥 0 直取 RW 品种表）+ 工厂对每对隔一节 chunk 加 `SoftOnly` PushOnly 连接（弹性走 RW 原公式 `1-Lerp(0.9,0.5,stiffness)`，Pos/Vel 同步修正 ≙ RW BodyChunkConnection.Update 原语义——只写 Vel 的弱推压不住抓地腿锚定的折叠）。`SoftOnly` 连接不进硬求解器、不计 `LastRelaxDeviation`、渲染不画线。指标 `maxFoldIntrusion`/`foldTicks` 进 [METRIC]：深折叠只剩落地瞬态（heavy wall 全程 56 tick 且分散在 3 次翻越里）。2 节脊柱无隔节对，default/sprinter 哈希不受影响。
> - **调参教训（heavy 第一版近瘫的根因）**：腿慢（LimbSpeed 0.10）+ 步幅大（StepLength 0.85）+ 外撇远（0.7）三者叠加会让脚永远追不上身体，平均抓地 0.6/4 → 重力开关长期打开。腿参数必须留在可行域（速度 ≥0.12、步幅 ≤0.75）附近，「笨重感」交给脊柱节数/体格缩放/站距/尾巴刚度表达——它们不碰抓地循环。
>
> **M5 产物**：内核抽离为独立程序集——回迁 = 拷走 `core/` 一个文件夹。
> - `core/ProcAnim.Core.csproj`：内核 classlib（BodyChunk/ChunkConnection/Body/SphereTerrain/ITerrainQuery/TickContext/Limb/Walker/BreedParams/BodyFactory + DeterminismHasher/PlaneTerrainQuery），**只使用 GodotSharp NuGet 的纯托管数学结构**——不引 Godot.NET.Sdk（挡住场景树源生成器；注意 GodotSharp 包里 GD/Node 仍编译期可达，真正的强制是 smoke 的 TypeRef 边界扫描：允许清单 Vector3/Mathf 之外的 Godot.* 引用即回归 FAIL）。命名空间去 Lab 化：`ProcAnimLab.{Physics,Sandbox,Terrain}` → `ProcAnim.Core`。`core/godot/RaycastTerrainQuery.cs` 引擎适配器随文件夹走、归游戏程序集编译（游戏 csproj `Compile Remove core/**` 后单独 Include 它）。BodyFactory 进内核并去掉唯一 GD 调用（未知品种名静默回落 default）。
> - `core/smoke/`：**无引擎冒烟回归**（纯 .NET console，`dotnet run --project core/smoke`，退出码判定）：解析平面地形（PlaneTerrainQuery，含 HitFromInside 零法线语义）+ default 平地巡走 1000 tick（直行+45° 转向进哈希），进程内双跑 bit-exact 且哈希钉死基线（值以 `core/smoke/Program.cs` 的 `ExpectedHash` 为唯一真相源）——「与引擎解耦」的运行时实证，也是回迁后目标仓库的秒级内核回归。评审修复轮追加断言：嵌入恢复、Shift 连续性、Launch 恢复、MoveTarget 到达/取消/传送契约、TypeRef 引擎边界扫描。哈希折叠下沉为内核 `DeterminismHasher`，沙盒探针与冒烟共用同一实现（两边哈希可互证）。
> - **抽离质量门**：9 配置全矩阵（default 双跑/40vs400Hz/perturb、wall、stand、三品种）与抽离前基线**逐字节 diff 为空**——移动文件+改命名空间+程序集拆分+哈希器下沉，零行为漂移。
> - [`docs/porting_contract.md`](docs/porting_contract.md)：回迁契约。模块清单与依赖面、装配（品种可行域表）/驱动（40 tick 固定序、60Hz 宿主 0.025s 累加器）/输入（MoveDir/RunSpeed 两旋钮 + MoveTarget 路径点直喂可选第三旋钮，≙ RW 寻路器喂路径格的原始形态）/输出（渲染与 AI 观测面）四契约、ITerrainQuery 接缝全语义（HitFromInside 零法线、主项目掩码层 20 + RID 排除规范、射线量级与节流阀门、Jolt 验证点）、确定性守则与三层回归、**两条迁移路线**（源码拷入 ≙ 主项目惯例 / 首个 ProjectReference）与**两种集成姿态**（规格 §4.3 可替换后端·身体拴权威根拖行 / 内核位置权威·根跟随，前者默认）。主项目对接面调研快照（单程序集 60Hz Jolt、motion 子系统 Gate-C 已过待接线、MonsterMotionSnapshot 映射表）沉淀于契约 §8。
>
> **单位约定**：1 RW tile (20px) = 0.5 m；`Vel` 语义 =「米/tick 位移」（积分 `Pos += Vel` 不乘 dt，内核零 delta 依赖）；重力默认 36 m/s²（= RW 0.9 px/tick² 直接换算），`GravityPerTick = 36×0.025² = 0.0225`。
>
> **确定性回归**（改物理内核后必跑；全部真断言——探针只打印不判定的旧形态是假绿，评审修复轮的教训）：
> ```bash
> # ① 无引擎冒烟（秒级，最快反馈）。退出码即判定：双跑 bit-exact + 哈希对基线（钉死在
> #    Program.cs ExpectedHash）+ 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 +
> #    MoveTarget 直喂契约 + TypeRef 边界扫描。
> dotnet run --project core/smoke
>
> # ② Godot 全矩阵（分钟级）。12 配置 × 硬断言（哈希基线/路点下限/[RESULT] 判定/位置检查），
> #    pipefail + 退出码聚合，任何一项红 → 非零退出；结尾打 MATRIX GREEN/RED：
> ./tools/run_matrix.sh [输出目录]
> # 配置：default ×2（双跑 diff）、default 40Hz（时基不变性）、perturb（灵敏度：哈希必须变）、
> #   wall（正面推墙翻越 ≥9）、stand（站桩+闲置姿态）、carrot（MoveTarget 路径点直喂通路）、
> #   heavy/sprinter/hexapod（品种路线）、embed（出生嵌入 60 tick 必须脱困）、
> #   wallside（贴墙擦边不得穿墙）。
> # [RESULT] 在进程 teardown 之前打印；已知 Godot 4.7 macOS 偶发退场 mutex 崩溃（exit 134），
> #   判定以 [RESULT] 为准（脚本已处理）。单配置手跑仍是
> #   $GODOT --headless --path . --log-file /private/tmp/godot_codex.log --fixed-fps 40 -- \
> #     --determinism=2000 --tps=400 [--route=…|--breed=…|--spawn=…|--yank=…|--expect-hash=X16]
> # 2000 tick 参考值（评审修复轮后）：default 12 路点、wall 11 翻越、heavy 6 路点/抓地 1.75、
> #   sprinter 17 路点、hexapod 9 路点/抓地 2.41/6、carrot 26 路点。
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
