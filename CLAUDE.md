# proc_anim_lab —— 3D 程序化生物动画实验室

> Godot 4.x / C# 的独立沙盒项目。**目标：从零实现一套 3D 版"雨世界式"程序化生物动画/运动系统；等它在这里成熟后，整体移植回 [`random-room-runtime`](../random_room/random-room-runtime/) 的怪物系统。**
>
> 当前状态：**M2 会走路完成**（plant-and-trail 四腿 + 射线落脚 + 抓地推进，平地巡走确定性回归就位），进入 M3。

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
| **M3** | 地形涌现：斜坡 / 墙 → 走、爬两态自然涌现（重力开关 + 射线方向切换） | ← **当前** |
| **M4** | 多样化与调参：多足 / 尾巴 / 多种体型，参数化手感（对标 `LizardBreedParams`） | 待办 |
| **M5** | 移植接口：抽出与引擎解耦的模块，定义回迁 `random-room-runtime` 的边界 | 待办 |

> **M1 产物**：`scripts/physics/`（纯 C# 内核：BodyChunk / ChunkConnection / Body / ITerrainQuery，零场景树依赖，M5 回迁边界）、`scripts/terrain/RaycastTerrainQuery.cs`（内核与 Godot 物理的唯一接缝）、`scripts/sandbox/`（驱动/渲染/拖拽/确定性探针）、`scenes/sandbox.tscn`（白盒：地板+缓坡+台阶+墙）。
>
> **M2 产物**：`scripts/physics/Limb.cs`（腿粒子：单点追目标 IK + plant-and-trail 状态机 + 竖直投影射线 FindGrip + 单侧腿长钳制，≙ RW Limb/LizardLimb）、`scripts/physics/Walker.cs`（行走驱动：推进力 ∝ 抓地腿数，无 locomotion 状态机，MoveDir/RunSpeed 唯一输入，≙ Lizard 移动块）、`scripts/physics/SphereTerrain.cs`（Body/Limb 共用的球-地形解算）；沙盒 WASD 行走 + 拖拽看腿自适应，脚球按状态换色（绿抓稳/橙迈步/灰蓝摆动）。确定性模式改为脚本化路点巡走（行走本身进哈希）：800 tick 走约 25.7 m、平均 2/4 腿抓地。
>
> **单位约定**：1 RW tile (20px) = 0.5 m；`Vel` 语义 =「米/tick 位移」（积分 `Pos += Vel` 不乘 dt，内核零 delta 依赖）；重力默认 36 m/s²（= RW 0.9 px/tick² 直接换算），`GravityPerTick = 36×0.025² = 0.0225`。
>
> **确定性回归**（改物理内核后必跑）：
> ```bash
> GODOT=/Applications/Godot_mono.app/Contents/MacOS/Godot
> $GODOT --headless --path . --log-file /private/tmp/godot_codex.log --fixed-fps 40 -- --determinism=400 --tps=400 | grep '\[DET\]'
> # 双跑 diff 必须为空；40Hz（去掉 --tps=400）与 400Hz 哈希必须一致；--perturb=0.001 哈希必须变。
> # 另有 --spawn=x,y,z 覆盖出生点（坡上/陷地板压力测试）；
> # --yank=T 在 T tick 给头部脚本化上抛冲量（「拎起再摔」回归：落地后步态必须恢复，
> #   曾有成对腿 extraLongStep 互相死等导致四腿永久冻结的 bug，靠确定性超时打破）。
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
