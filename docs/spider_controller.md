# Spider 3D 控制器

`SpiderLocomotionController` 是与 `LizardLocomotionController` 并列的物种后端，**不继承也不
扩充蜥蜴控制器**，只共享 `Body` / `ChunkConnection` / `SphereTerrain` / `ITerrainQuery`。

实现依据为本机 Rain World `BigSpider` / `Spider` 及其 Graphics 类的反编译研究（拓扑与「足端
粒子 + 渲染期两骨 IK」的分层），只移植行为结构与单位关系，不包含原游戏源码。真实蜘蛛步态
文献只用于补充迈步时序，不模拟肌肉/液压。

> **装配 / 输入 / 输出契约的真相源是**
> [`porting_contract.md`](porting_contract.md) §2.3。本文档记录的是**为什么是现在这样**——
> 各轮修复的症状、根因取证、被推翻的初判与验证边界。两处如有冲突，以契约与脚本当前输出为准。

## 1. 身体与姿态边界

- `SpiderBreedParams.BodySegments` 至少两项，按列表顺序装成唯一的 Rigid 线性链；**不表达
  分支或环**。每个 `LegPairSpec.AnchorSegmentIndex` 显式指定锚点节，同一节可挂任意多对腿。
- 正式 `spider-small` / `spider-large` 都对齐 RW BigSpider 的「两节身体、四对腿全挂第 0 节、
  第 1 节无腿」。`SyntheticMultiAnchor()` 三节多锚点配置只用于回归，证明拓扑没有被控制器写死。
- `SpiderLeg.Pos`（足端）是**唯一**参与运动与抓地的物理点。`RootPos → KneePos → Pos` 是渲染
  姿态：两段长度由余弦定理解，持久 `BendPole` 在共线与换面时保持手性，**膝不碰撞、不承力**。
- 地面 / 斜坡 / 墙 / 内外角 / 天花板**没有模式枚举**：支撑法线由真实抓地法线低通汇总，
  抓稳关重力、失抓恢复重力。

## 2. 修复轮记录

### 2.1 窄墙抱边（2026-07）

**症状**：身体贴在接近自身宽度的墙端面时，端面外的腿没有候选而悬空。

**修复没有增加窄墙模式**：`SpiderLeg.FindGrip` 把名义落点沿旧支撑法线压进凸棱轮廓，再沿
`_frameRight * Side` 用完整腿长横向反投影；左/右腿各自发现相邻侧面。该候选**单独保存**，
只有命中本侧横向面或前方正交面、且处于有限 AEP 距离余量内时才可替换旧支撑候选；命中仍走
可达环和 `TargetSurfaceContact` 背书。

**验证**：`--route=narrow-wall` 使用 0.36m 端面并按身体半径留出接近距离；小/大型最终分别有
5/6 条腿连续抱侧面，双侧接触窗口 961/879 tick，支撑有限稳定，IK/pole/穿透不回归。

### 2.2 完整迈步（2026-07）

**症状**（用户白盒实测）：大蜘蛛最后一对腿高频向前挪一点、像被身体拖行。

**被推翻的初判**：「只是腿慢」。逐步测量证实真实链路是三条叠加——① 旧路径在抓点刚越出可达
环时会**同 tick 直接 `FindGrip`**；② 摆动中的腿被可达性检查**逐 tick 重新 `BeginSwing`**；
③ 落脚目标随腿根每 tick 前移。后腿因此记录到约 69~73 次直接重定向，单次前向变化仅
-1.5~+0.9cm，**根本没有完成 PEP→AEP 的摆动**。

**参考的生物学边界**：真实蜘蛛慢速步态常由 `R1/R3/L2/L4 ↔ L1/L3/R2/R4` 两组四腿交替，足端
具有明确接触期和前摆期。本项目取其确定性稳定子集。

**现行修复**：

1. `OpposeSidePhase` 让同对左右反相、相邻腿对交替。
2. 全部腿先更新本 tick 根部，**再**按最低保留抓地腿数、相位和硬超距统一发放松脚许可。
3. 正式预设启用 `UseExplicitTouchdownLead`：抬脚瞬间保留横向工作区并**冻结世界 AEP**，
   摆动期间不追身体。
4. 越出可达环只会开始**一轮**完整 `ReachRecovery`，已摆动腿不会反复清零。

大蜘蛛保留 `TrailReleaseRatio=0.38`、`GaitPhaseTicks=12`、慢脚速 / 四 tick 抓握，最低支撑
为 4；四对 AEP lead 由前至后为 `0.55 / 0.48 / 0.40 / 0.35` 倍腿长。

**验证不再只看整体速度**：`StepSerial` / `LandingSerial` 逐腿统计完整步、直接重定向、紧急步、
前向追回、微步、抬脚高度及支撑期根部推进。900 tick smoke 中大蜘蛛每腿完成 27~35 步，最弱腿
平均追回 0.271 倍腿长，最后一对 0.287，后腿微步 / 直接重定向 / 紧急步均为 0；399 tick Godot
`--route=gait` 中最后一对平均追回 0.282、微步 0；小蜘蛛后排 0.265、微步 0。

### 2.3 急转腿槽（2026-07）

**症状**：身体已完成 90°/180° 换向，但部分腿持续落在另一侧，甚至同一腿对倒置。

**根因不是转身速度**，而是 `CaptureSwingTarget` 原样继承旧脚相对新局部轴的横向分量；一旦旧
抓点跨过中线，每轮冻结 AEP 都会继续复制错误侧。

**第一轮修复（镜像）**：只在抬腿捕获新 AEP 的 tick 处理——若横向分量已跨线，且该腿已确认的
上次抓面与当前支撑面 `dot >= 0.85`，就关于本腿根部面镜像一次。旧脚在接触期仍可自然短暂跨身；
不强制松脚、不逐 tick 重映射冻结目标；法线明显不同的窄墙/棱角多面抓握不套用平面镜像。

**第二轮（视觉复查暴露的稳定坏解）**：镜像只修符号，`+0.06` 这类很小的正 lane 会被以后每次
AEP 原样复制 → 「回到本侧但仍贴身」。因此在镜像之后增加**同面站距软回收**：本腿与配对腿的
上次抓面、当前 `_frameUp` 三者法线 dot 均 ≥0.85 时，每次正常抬腿把横向分量向
`MaxReach × Lerp(0.68, 0.82, StepLength) × DesiredReachDirection·outward` 回收 60%。名义宽度
自然包含每条腿的扇角 / 横向权重 / 体型；已植脚不动，内外侧短暂差异随错相换步渐退；窄墙两侧与
棱角多面抓握因法线不同自动跳过。

**验证**：无引擎专项覆盖小/大型左右 90° 与精确 180°——身体 5~15 tick 对齐，全部足端和下一
落脚目标进入连续 20 tick 正确侧的起点为 13~52 tick，滚动站距平衡恢复为小型 36~40 tick、
大型 69~95 tick；预算后最坏腿对差 P95 ≤0.09 腿长、最小内外站距比 P05 ≥0.87、每腿实际/AEP
相对各自名义宽度 P05 ≥0.875，同时钉住 IK/pole、失抓与转后推进。Godot
`--route=turn --turn=left|right|around` 另以真实 RootPos 腿槽覆盖小/大六项；最坏
large-around 在 55 tick 后不再跨身，92 tick 内恢复站距平衡，零 pole 翻面。

### 2.4 spider-lean 场景门（2026-08-06）

`spider-lean`（按原作群居小蜘蛛 size=0.6 换算的长腿轻身预设）从交接时的 5/7 修到 7 条场景
路线 + gait 全绿，既有小/大 16 项矩阵哈希与 `spider_smoke` **逐位不变**。三个根因**全部与
交接单初判不同**：

**① course 膝跳 104%** —— 元凶不是缺余弦钳制（事发 tick 的 cos 全程在 [0.2, 0.98] 带内不
咬合），而是**足端贴近腿根时腿轴单 tick 近乎反转**（落地减速 rootStep 0.25 + 足端反向 0.40，
相对位移 ≥ d）——膝点被两球交线圆强制甩 0.9~1.04L。

修复 = 核心两件 **opt-in**（`SpiderBreedParams.KneeStepBudgetRatio`，默认 0 = 既有品种膝解算
逐位不变；`KneePos`/`BendPole` 在 `FoldSpiderLegs` 进哈希，**opt-in 是硬要求**）：

- **膝点连续性预算**：膝自由度只在绕腿轴的圆上，圆上任意角都精确满足两段骨长 → 先取离上一
  tick 膝点最近角，预算弧内转回平滑 pole，`ikError` 恒 0。
- **近根鞭甩钳制**：d < 0.5 腿长时腿向量单 tick 变化弦长 ≤ 0.62×平均长 ≈ 36°/tick，正常摆动
  在大 d 区不触碰。

course 膝跳 1.036 → 0.666，`finalInwardGrip` 自愈归零。

**② turn-right supportUp 0.891** —— 是**场地污染不是姿态问题**：lean 巡航 0.063m/tick，
120 tick 引导段从 x=11 走到 x≈3.4，整只钻进坡道板下（坡底面在 x≈2.3~4.5 只离地 0~0.7m），
脚抓上坡底楔缝顶（法线 (0.309, −0.951, 0) ＝ Ramp 底面）。修复 = 沙盒 `TurnArenaMinX=7.2`
引导段下界（small/large 实测停在 7.47/7.92 从不触及，哈希不变；7.2 时任何品种足端 + 射线
可达半径 ≤0.88m 对坡道与 WallX 全部出清）。

**③ gait 冻结 AEP 衰减** —— 摆动 + 抓握 ~7 tick × 巡航速度 ÷ 腿长归一化 ≈ 0.6~0.9 超前量
损失，后两对 lead 0.40/0.33 被吃光 → 落点恒在腿根后、rear 复位 0.04L、微步 92%（落地即再抬
的极限环，A/B 证实与新钳制无关、预设落地时即有）。修复 = 沙盒 `--gait-throttle`（默认 1 =
gait-large 逐位不变）+ lean 后两对 lead 提到 0.45/0.48。

`--route=gait --spawn=11,0.55,8 --gait-throttle=0.6 --determinism=300` 下 rear 复位 0.288、
微步 0、紧急步 0，全部 gait 门 PASS。

**遗留**：lean 仍不进矩阵（矩阵化留待下一轮），七路线复现命令见 `SpiderFactory.LeanSpider()`
的文档注释。

## 3. 正式渲染

蜘蛛的 `IFormalRenderer` 走**专用沙盒**（不经 `FormalRendererFactory` 分派）：三点 Bezier
变径体管（腹剖面 = 沿弧长椭圆叶 + 1.6×R 尾展 = 修长椭腹，细腰/尾锥由椭圆两端自然给出）、
pedicel 细腰双叶剪影、内核 `Root/Knee/Foot` 两段 IK 直接消费成股/膝瘤/胫/爪尖四件、
verlet 密细腹毛（黑根亮尖线性渐变）+ 贴体四件套（锥台链锚定/排斥、肉身剖面避后极、薄壳钳制、
40/60 后掠外梳）、渲染侧 `tailEnd`/呼吸。`spider-small` 黄毛 / `large` Spitter 红毛 /
`lean` 全黑近无毛。不移植 `deadLeg`（腿真实承力）与 flip。

顶点色走 `TubeMeshBuilder.Build(srgbVertexColors: true)`，在所见空间调色。细节与四轮贴体实测
见 [`rainworld_render_research.md`](rainworld_render_research.md) §5。

## 4. 沙盒与回归

- `scripts/spider_sandbox/`：独立白盒；正式视图下地形调试线随白盒隐藏。旗标沿**无前缀**命名
  （参数空间独立于蜥蜴沙盒）。
- `core/spider_smoke/`：确定性、拓扑、两段 IK、步态与生命周期无引擎回归。
- `tools/run_spider_matrix.sh`：16 项 Godot 配置，覆盖两预设、完整步态 / 急转 / 窄墙 / 换面。

```bash
dotnet run --no-restore --project core/spider_smoke
./tools/run_spider_matrix.sh
# 原蜥蜴 smoke / 主矩阵必须逐位不变：
dotnet run --no-restore --project core/smoke
./tools/run_matrix.sh
```
