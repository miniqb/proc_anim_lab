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

### 2.5 跳跃攻击、飞行态与昏迷（2026-09-17）

**需求**：蜘蛛类怪物的两大特征——跳跃攻击与任意表面附着。攻击距离必须与跳跃物理**解耦**：
设计侧给定「在墙上能打到 N 米内」这种攻距，跳速按目标现算（1m 与 9m 都要命中）；
天花板上的蜘蛛必须知道正下方的人可以直接掉下去咬，而不是「攻距够不到地面」的愚蠢。

**机制来源**：RW `BigSpider.Attack/Jump`（`charging` 预兆 → `mainBodyChunk.vel += jumpDir×16`、
后节 ×11、图形腿 `vel += jumpDir·30·(j<2 ? 1 : −1)`、`footingCounter = 0`）与 `Collide` 里撞上
猎物即朝「猎物→自己 + (0,10px)」再 `Jump` 的反弹。原作是**固定跳速只调方向**（攻距由跳速隐式
决定，AI 另有 `LerpMap(dot, −1, 1, 0, 500px)` 门）；3D 版反过来：攻距是设计量，跳速精确反解。

**内核（全部 opt-in，既有基线哈希逐位不变——新状态刻意不进 `FoldSpiderControllerState`）**：

- `SpiderLeapPlanner.TryPlan`：内核积分是 `Vel += g; Pos += Vel; Vel *= f`（`Body.Tick` 固定序），
  N 固定时位移 = v0·A(N) + g·C(N) 对 v0 线性，A/C 用同序累加得到（f=1 退化为 N 与 N(N+1)/2）。
  N 在 `距离 ÷ PreferredSpeed` 附近交替搜索（center, +1, −1, +2, …），取第一个满足
  「离面 v0·n ≥ MinOutward / 限速 / 抬升 ≤ MaxRise / 可选地形扫掠（逐段射线 + 球体重叠）」的解；
  猎物提前量 `TargetVelocity × N` 逐候选代入。纯静态数学，无状态无随机。
- `SpiderLocomotionController.BeginLeap(v, N)`：全部身体节与足端**置**同速（置而非叠加——
  规划弹道要逐 tick 成立），腿 `ForceRelease` 进飞行姿态；`Leaping` 期间重力常开、无推进无
  拖尾（两节同速出发 → 实飞与规划逐 tick 一致，smoke 实测到点误差 0.000m），姿态按截止式
  对准摆正（见下「受击与姿态第二轮」）。`LeapMinContactTicks` 之后
  任一身体节触地、或超过 `N + LeapGraceTicks` 即结束（`LeapEndedByContact` 可读），腿走
  `ResumeGripSearch` 当 tick 找抓点（不再等最短摆动期）。`Launch`（被击中）打断飞行。
- 飞行腿姿 `SpiderLeg.TickAirborne`：前对腿沿起跳方向张开、后对腿反向拖尾（≙ RW 腿速度
  ±jumpDir），走常规管线（追逐 → 根约束 → 近根钳制 → 出地形 → 可达性 → 膝解算），
  `GripCounter` 恒零因此从不计入支撑。
- `Conscious = false`：昏迷/死亡——腿蜷向体下（`LimpLegCurl`）、重力常开、无推进；
  贴地摩擦取 footed 档，尸体不滑。内核允许复活（置回 true 即重新找抓点）。

**被推翻的初判**：

- 「用既有 `Launch` 叠加冲量就够」——`Launch` 只叠加速度且不抑制抓点：从天花板起跳后
  腿在 2~3 tick 内重新抓回顶面并关重力，蜘蛛挂在原地不动。飞行态必须显式抑制找抓点。
- 「按最小能量选飞行时长」——对同高目标是 45° 大抛物线，8m 墙面扑击抬升 2m 直接撞顶
  （房高 3.2m）；改成「名义速度居中搜索 + 抬升上限 + 扫掠」后长距自动变平变快
  （7.4m 墙面扑击 20.5m/s、抬升 0.02m），近距自动变慢（1.3m 扑击 10.7m/s）。

**宿主（`scenes/spider_arena.tscn` / `scripts/spider_sandbox/SpiderArenaWorld.cs`）**：

- 攻距按支撑法线与世界上方向的夹角在 地面 3.5 / 墙 6 / 天花板 9m 三档间**分段线性**插值
  （Inspector 可调）；蓄势 0.3s 后按此刻猎物位置 + 速度重规划起跳，猎物跑出攻距/视线则放弃。
- 命中 = 任一身体节球与玩家胶囊相交 → 玩家走外部冲量通道击退 + 蜘蛛朝「猎物→自己」略向上
  `BeginLeap` 反弹（新弹道，落地才压冷却）；扑空 = 触地/宽限结束，短冷却。
- 手枪命中 = 扣血 + 冲量：地面上 `Launch(枪向 + 向上分量)` 被推退再抓地；墙/顶上打进表面的
  分量按 `KnockOffBounce` 反射成离面反弹再 `Launch(v, KnockOffHoldTicks)`——腿不抓、离面下落、
  落地才站稳；硬直封攻击门 + 停潜行并抢占蓄势/飞行；3 枪 `Conscious = false`，落地翻身
  （`LimpBellyUp`）后静止冻结。
- 潜行接近走 `SpiderStalkPlanner`（宿主 AI，不进内核）：切平面 12 向投影 / 水平扫墙 8 向给
  「爬上去」候选 / 已在墙顶时向上探顶面候选，打分 = 水平推进 + 表面加成（墙 0.5 &lt; 顶 0.8）
  + 之字项（期望侧逐点交替）− 直线惩罚 + xorshift 抖动；猎物 3m 内改绕行（推进权重 0.15、
  之字 ×2）。驱动只喂 `MoveDir` = 路径点方向在当前支撑切平面的投影——跨面时先水平撞向
  墙脚、抓上墙后投影自然变成沿墙向上，不引入任何模式。

**受击与姿态第二轮（2026-09-17，用户试玩四项反馈）**：

- **墙上打不掉（bug）**：用户反馈墙上的蜘蛛被击中只短暂脱面随即回抓，顶上的才正常掉。
  根因两条：① 枪向冲量是**打进墙**的，`Launch` 后身体被墙顶住原地下滑，腿在 `LastGripNormal`
  候选里一伸就把墙抓回来（smoke 消融：旧路径下滑 0.54m、第 7 tick 回到墙上）；顶面则重力
  把身体拉离顶面才掉得下来。② 无任何「先离面再谈抓握」的保持期。修法：`Launch(v, noGripTicks)`
  opt-in 第二参数——保持期内腿走 `TickAirborne` 不找抓点、自由落体、支撑法线以
  `KnockOffRightingRadPerTick`（0.12 rad/tick）等角速度翻正而**不再瞬切成世界上**（旧 `Launch`
  的 `ResetSupportState` 仍瞬切，默认路径不变）；宿主把打进表面的冲量分量按 `KnockOffBounce`
  （0.35）反射成离面反弹再传 `KnockOffHoldTicks`（12）。smoke：墙/顶都掉到 5m 下的地板再站稳。
- **飞行中歪着/侧着到达**：渲染件的身体朝向取 `Primary − Rear` 链方向、up 取 `SupportNormal`，
  旧飞行态只低通 `_forward`（渲染不读）和以 0.06 权重 nlerp 法线——从墙起跳 20 tick 到点时
  法线才翻 70%，身体侧着撞人。改 `AlignFlightPose`：截止 tick = `N × LeapAlignFraction`（0.6），
  每 tick 转角 = 剩余角 / 剩余 tick（等角速度、到期精确对准，`LeapAlignMaxRadPerTick` 0.45 封顶
  防极短飞行瞬切）；身体轴朝 `LeapFacing`（宿主每 tick 喂猎物水平方向；零向量 = 飞行方向
  投影，竖直俯冲保持现有朝向）做**绕主节的刚体旋转**（链长不变、主节弹道逐 tick 不受扰——
  smoke 到点误差仍 0.002m），法线朝世界上、反向时绕身体轴滚转（顶面俯冲 = 180° 翻滚，7 tick
  完成）。新增 `RotateToward`（Rodrigues）替代 nlerp：nlerp 在小权重 + 近反向时几乎不动、
  权重过半时又跳 90°，不适合「到期必须摆正且不瞬切」。
- **死相翻身**：`LimpBellyUp`（宿主 opt-in）——昏迷且任一身体节触地后，支撑法线以
  `LimpFlipRadPerTick`（0.14）绕身体轴滚到世界下方向（背朝地、腹朝天），既有蜷腿目标
  `fan×0.5 − FrameUp×0.9` 随帧翻成指向天（膝 pole 侧向，腿蜷在体上方）；之后只剩约束与碰撞，
  被碰翻允许。竞技场在翻身完成（法线·下 &gt; 0.98）后才冻结尸体。
- **后退幅度**：`HitImpulseMps` 4 → 6.5、`HitImpulseUpMps` 1.5 → 2.2（小蜘蛛地面命中 60 tick
  水平后退 bot 实测均值 1.3m，旧值约 0.6m）；命中硬直期潜行也停（原只封攻击门）。
- 渲染件 `_bodyUp` 低通在飞行/击落/昏迷期从 6/s 提到 16/s——内核已在等角速度转法线，
  低通只需跟得上不拖尾。

**验证边界**：`[SPIDER-LEAP]` smoke 八项——地面扑击到点 ≤5cm（小/大）+ 触地结束 + 再抓稳、
天花板俯冲 v0·n ≥ 0.02 到点 + 宽限超时结束 + 180° 滚转到点前完成且单 tick ≤ 封顶角、
身后侧方目标（141°）截止 tick 对准 + 等角速度 + 主节弹道不受扰、墙/顶击落掉到地板再站稳
（旧路径消融钉住「墙上打不掉」）、路径被墙挡规划失败（同请求去掉扫掠可解）、飞行中
`Launch` 打断 + 再抓稳、昏迷不抓地/落地静止/复活、昏迷翻身腹朝天 + 8 脚高于腿根；全部双跑
bit-exact；16 项矩阵哈希不变。竞技场无头自检：`--bot=still --ticks=4000`（小蜘蛛扑击含
墙面/天花板起跳与零抬升俯冲）；`--bot=strafe`（突进-停顿玩家）；`--shoot-every=300`
（3 枪死 + 翻身 + 尸体冻结）；`--shoot-every=20 --shoot-on=wall|ceiling --hp=40`（每次
墙/顶命中 60 tick 内下落 ≥0.8m 才 PASS，`[SPIDER-ARENA-HIT]` 逐次打下落/后退/回抓）。
**未验证**：多蜘蛛、猎物在斜坡/楼梯上、房高 &gt; 9m 的顶面攻距外推。

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
- `scenes/spider_arena.tscn`（`SpiderArenaWorld` / `SpiderArenaHud` / `SpiderStalkPlanner`）：
  跳跃攻击竞技场（探索场景，不进矩阵）——第一人称玩家、手枪、三预设切换（1/2/3）、
  Inspector 全参数；无头自检 `--bot=still|strafe --ticks=N [--tps=400] [--preset=…]
  [--shoot-every=N] [--shoot-on=ground|wall|ceiling] [--hp=N] [--screenshot-dir=…]`，
  每次命中后打 `[SPIDER-ARENA-HIT]`，结尾 `[SPIDER-ARENA-RESULT]`。见 §2.5。
- `core/spider_smoke/`：确定性、拓扑、两段 IK、步态、生命周期与跳跃攻击/飞行态/昏迷
  （`LeapSmoke.cs`）无引擎回归。
- `tools/run_spider_matrix.sh`：16 项 Godot 配置，覆盖两预设、完整步态 / 急转 / 窄墙 / 换面。

```bash
dotnet run --no-restore --project core/spider_smoke
./tools/run_spider_matrix.sh
# 原蜥蜴 smoke / 主矩阵必须逐位不变：
dotnet run --no-restore --project core/smoke
./tools/run_matrix.sh
```
