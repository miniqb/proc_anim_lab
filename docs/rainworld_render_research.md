# 雨世界怪物渲染研究与 3D 正式渲染方案

> **反编译实证**（2026-08-05）：逐类拆解本机雨世界反编译源码（`~/workspace/others/rw_decomp/`）中
> **全部十个对应物种的 Graphics 类** + 渲染基建（`GraphicsModule` / `TriangleMesh` /
> `RopeGraphic` / `BodyPart` 族），提炼 RW"去球感"的通用手法词汇表，并给出本项目的
> 3D 渲染架构与技术验证选型。所有行号引用指向 `rw_decomp` 内文件（仓库外，仅参考）。
>
> 定位：本文档是**正式渲染层**（替代沙盒 debug 球+线）的设计真相源。运动内核不在本文范围
> （见 [rainworld_procedural_animation_research.md](rainworld_procedural_animation_research.md)）。

---

## 0. 一句话结论

RW 的"流畅有机感"**不是样条魔法，而是五件小事叠加**：
① 渲染永远不直接画物理 chunk（有独立的 render spine / 曲线控制点 / 剖面函数）；
② chunk 链被密化成更密的采样（插值中点 / 贝塞尔 / 弧长 pin）；
③ 连接几何是**宽度插值的梯形条带**（相邻半径取平均 + 关节重叠内缩）；
④ 半径是**解析剖面函数**（sin 包络、shaped hump），与物理半径完全解耦；
⑤ **整只生物一个平色**（palette black），所有内部接缝因同色而不可见，点缀色只出现在
梯度端部（尾尖/触手梢/装饰）。
3D 化后，②③④合并为一件事：**沿样条扫掠截面的管状网格**（swept tube），①⑤原样保留。

## 1. RW 渲染通用词汇表（反编译取证）

### 1.1 GraphicsModule 生命周期与 render spine 双缓冲

- 生命周期：`ctor → InitiateSprites`（一次性分配）→ 每逻辑 tick `Update()`（40Hz，跑在生物物理**之后**）
  → 每渲染帧 `DrawSprites(timeStacker)`（一切位置 `Lerp(last, cur, timeStacker)`）→
  `ApplyPalette`（仅初始化/调色板变更时，非每帧）。（GraphicsModule.cs:71-212）
- **render spine 双缓冲**：图形层把 chunk 位置抄进自己的 `drawPositions[i, 0|1]`，然后在**副本**上叠加
  化妆偏移——走路 bob（抓地腿数低通驱动竖直位移，LizardGraphics.cs:1055-1065）、呼吸、恐惧抖动、
  蓄力颤抖。**物理永远看不到这些偏移**。这是本项目渲染层与确定性内核之间应有的同款隔离。
- 图形层自有粒子（`GenericBodyPart` 垂摆点 / `Limb` 追点 / verlet 绳）也归 `Update()` 驱动，
  会撞地形（`PushOutOfTerrain`）但**不回传力**。通用约束原语 `BodyPart.ConnectToPoint(pnt, rad,
  push, elastic, hostVel, adaptVel, exaggerateVel)`（BodyPart.cs:41-58）是全部"垂坠感"的来源，
  纯数学、可逐行移植。

### 1.2 万能管带（MakeLongMesh 梯形条带）

`TriangleMesh.MakeLongMesh(n, pointyTip, customColor)`：n 段 × 每段 4 顶点的连续条带，
三角形拓扑**建一次不再变**，每帧只改顶点位置（TriangleMesh.cs:197-230）。全 RW 的尾巴、
触手、脖子、翅臂、鹿角、腿带全是它。每段的标准写法（以 Vulture 脖子为例，VultureGraphics.cs:1384-1410）：

```
dir  = normalize(cur - prev);  perp = Perpendicular(dir);  inset = dist(cur, prev) / 5
v[4i+0/1] = prev ∓ perp * ((prevRad + curRad) * 0.5) + dir * inset     // 近端：邻段半径取平均
v[4i+2/3] = cur  ∓ perp * curRad                     - dir * inset     // 远端：本段半径
```

两个关键：**近端宽度取相邻段平均**（C0 半径连续，杀掉台阶感）；**±dist/5 内缩**让相邻梯形
重叠 ~40%，弯折处渲染成圆角而不是折线肘。蜥蜴身体条带还在每个采样点叠一个
`Circle20` 圆片（半径=当日条带半宽）盖住转弯楔缝——条带+圆片合起来就是 2D 胶囊链
（LizardGraphics.cs:1725-1743）。

### 1.3 密化与曲线化：三种策略

| 策略 | 用法 | 例 |
|---|---|---|
| **插值中点** | chunk 间按 Slerp 混合方向插入中点，采样数翻倍 | 蜥蜴 3 chunk→4 样点（`BodyPosition`，Slerp 0.35，LizardGraphics.cs:2432-2453）；Scavenger 3 chunk→5 draw 点（合成脖根/收腰点，ScavengerGraphics.cs:1545-1592） |
| **chunk 当贝塞尔控制点** | chunk **不在曲线上**，只是磁铁；曲线两端是头前伸点与图形层 tailEnd 粒子 | BigSpider 整身一条 `Bezier(head+dir*3, abd, tail, abd)`（BigSpiderGraphics.cs:581）；DropBug `Bezier(head+axis*3, mid, tailEnd, rear)`（DropBugGraphics.cs:535） |
| **弧长 pin + 自动柄贝塞尔** | 稀疏控制点按弧长比例 pin 到 N 个稠密渲染段上，未 pin 段落在三次贝塞尔上，柄=（入向+弦向).normalized × 弦/3 ≈ 向心 Catmull-Rom | TentaclePlant 8 chunk→40 段（RopeGraphic.cs:56-131）；Daddy 腿管 4× 密化 + 地形拐角点注入（DaddyGraphics.cs:752-790） |

三者在 3D 里统一为：**Catmull-Rom（或 RW 同款混合切线 Hermite）样条过控制点序列**，
按需过采样。头尾各外插一个虚拟点（蜈蚣 ±10px，CentipedeGraphics.cs:560-586）让端部收锥而非停在球心。

### 1.4 半径剖面与物理半径解耦

渲染半径**从不是** `chunk.rad`，而是弧长参数 t 的解析函数，且常带行为调制：

- 蜘蛛：`rad(t) = Lerp(2.5, 10 + sin(breath/10), sin(t^0.75·π)) · bodyThickness`——sin 峰后移=腹部隆起，峰值随呼吸 ±1px（BigSpiderGraphics.cs:582）。
- DropBug：`Lerp(6,2,t) + sin(pow(t,1.7)·π)^0.75 · Lerp(7,5,|flip|) · (1+ceilingMode)`——背壳隆起，悬挂时充气 ×2（DropBugGraphics.cs:536；`ceilingMode` ≙ 我们的 `HangFactor`，可直连）。
- 秃鹫翅臂：分段 SCurve/cos 剖面 `TentacleContour(t)`，**全部断点随 flyingMode 插值**——爬行粗肢↔飞行细梁的形态渐变（VultureTentacle.cs:116-139）。
- 拉伸变细（体积守恒）：`r *= clamp(pow(restLen/dist, 0.35), 0.5, 1.5)`（Tentacle.cs:112；TailSegment.cs:55 同理）。
- 拟态草甚至**反转**物理锥度：物理根粗梢细，视觉根细梢粗（抓握端垂满海带球）——证明视觉剖面是纯美术自由度（TentaclePlantGraphics.cs:47-50 vs TentaclePlant.cs:62）。

### 1.5 平色融合与点缀色

- `ApplyPalette` 把**所有** sprite 先刷成 `palette.blackColor`（剪影黑）；物种识别色（effectColor）
  只出现在：头部（蜥蜴头呼吸式明暗脉冲，`1-pow(0.5+0.5·sin(blink·2π), 1.5+excitement·1.5)`，
  LizardGraphics.cs:2064-2081）、装饰覆层、**梯度端部**。
- 逐顶点梯度是唯一的"高级"着色：蜥蜴尾 `Lerp(black, effectColor, pow(invlerp(start,0.95,t),exp))`
  （LizardGraphics.cs:2137-2206）；Daddy 触手**前 30% 严格纯黑**（与球团无缝熔接），到梢也只 40% 上色
  （DaddyGraphics.cs:483-503）——"肢体从体块里长出来"的错觉全靠这个黑根。
- 平色是**承重结构**不是美术偏好：所有重叠图元（条带下的圆片、关节重叠、插进剪影的腿）
  内部接缝因同色不可见——只有外轮廓需要是平滑的。任何内部着色/光照都会立刻暴露拼缝。

### 1.6 肢体：粒子 + 渲染期两骨 IK

- 物理层只有**足端单粒子**（plant-and-trail）。膝/肘是纯渲染派生：
  `Custom.InverseKinematic(root, foot, L1, L2, flipSign)`，余弦被钳制在 `[0.2, 0.98]`——
  **腿永远不完全伸直也不完全对折**（Custom.cs:1201-1206），这是"有机感"的暗门。
- 弯折侧（bend pole）= 低通的侧别投票：足端在身体轴哪一侧（`DistanceToLine`），
  蜘蛛只在**摆动中**允许 pole 随机走（BigSpiderGraphics.cs:326-335）——防止支撑腿膝盖突跳。
- 渲染三选一：整腿一张预绘 atlas 图（蜥蜴 54 图 LUT）；三张 sprite **中间那张横跨膝盖**
  （蜘蛛，+1~2px 重叠，BigSpiderGraphics.cs:605-610）；root→knee→foot 一条管带
  （鹿/Scavenger，关节处 ±7.5px 预倒角控制点，DeerGraphics.cs:750-799）。
  **3D 统一为第三种**：过 root/knee/foot 的低张力样条扫管，天然涵盖前两者的意图。
- DropBug 的膝甚至不用 IK：中点松弛粒子（双距离 snap 0.55L + pole 踢力），两段三次贝塞尔在膝处
  共线柄拼 C1（DropBugGraphics.cs:370-385, 570-601）——更软、适合小甲虫腿。

### 1.7 假 3D 系统：3D 化后的去留

RW 是侧视 2D，"深度"全是假的，但每个 hack 都指名了 3D 里该由谁接管：

| RW hack | 机制 | 3D 归宿 |
|---|---|---|
| `depthRotation`/`flip` | 四肢落点侧别投票→低通 roll 标量 | **消失**（真 roll 来自 SupportNormal frame）；但"投票驱动 bend pole"保留 |
| 蜈蚣 `bodyRotations[3]` | 头/中/尾三个控制 roll 沿链 Slerp | **保留为 frame 平滑思想**：链上 up 向量三控制点低通+Slerp，替代逐段生法线（防扭转突跳） |
| 头部 9 帧 turnaround atlas | 角度→帧索引+残差旋转 | 真 3D 头网格；可选保留 4-tick 迟滞步进换向的"钝拙感" |
| 远侧肢体 alpha 0.3 | 画家算法深度暗示 | 深度缓冲天然解决 |
| 装饰 scaleX 翻转/压扁 | 随 roll 滑动的背脊装饰 | 装饰挂在**截面角度站位**上（0°=背脊），随真实 roll 自动滑动 |

### 1.8 装饰系统（个体差异的来源）

- **全部装饰在出生时以 creature seed 冻结**（`Random.InitState(seed)` + 状态恢复）：蜥蜴 15+ 种
  cosmetic 模板、蜘蛛 10-27 根毛、秃鹫每翅 13-25 根羽毛（含 loose/stunted/discolored 个体磨损）、
  Daddy 的瘤/悬索/死腿数量。个体差异 ≈ 免费。
- **挂载走连续 spine 参数化**，不见 chunk：蜥蜴 `SpinePosition(s∈[0,1])` 横跨身+尾
  （LizardGraphics.cs:2366-2430）；Scavenger `(x∈[-1,1], y∈[0,1])` 体表坐标系
  （ScavengerGraphics.cs:2155-2344）。3D 版=样条弧长 s + 截面圆周角 θ。
- **verlet 绳装饰**（蜘蛛毛/鹿苔藓/秃鹫飘带/拟态草海带）：阻尼 0.9 + 重力 + 距离约束 +
  远程拉直（i±2 邻居），根 pin 在体表参数点。纯渲染层，量大管饱。
- 重复装饰（鳞/刺/羽/瘤）在 3D 里= **MultiMeshInstance3D** 每类一个。

### 1.9 生命感与死亡（涌现，零动画资产）

- 呼吸：`(sin(breath·2π)+1)·0.5·pow(1-runSpeed,2)` 调制前胸宽度——**跑起来自动消失**（LizardGraphics.cs:1715-1729）。
- 眨眼/头色脉冲/瞳孔追视/idle 触须抖动：各自独立小计时器。
- 走路 bob：抓地腿数低通→draw 位竖直位移；DropBug 反向版：腿相位反推身体 bob（DropBugGraphics.cs:388-390）。
- **死亡=系统关闭**：limb→Dangle 垂摆、头伺服关、blink 冻结、呼吸停——姿态自然瘫软，没有死亡动画。
  我们的内核死亡/击晕语义（`Conscious`/`Launch`）已经对齐这套思路。

### 1.10 两种截然不同的身体策略

- **扫掠派**（多数）：细长身体→管带。蜥蜴/蜈蚣/蛇形/触手/人形躯干。
- **替换派**（短硬身体）：chunk 只是隐形骨架，整壳一张预绘图/一个刚性网格摆在 chunk 轴上——
  蝉（9 帧 flipbook 壳，CicadaGraphics.cs:454-498）、秃鹫躯干（4 chunk 风筝 → **一张** KrakenBody 图，
  VultureGraphics.cs:1330-1332）。3D 归宿：刚性网格 + 低通姿态四元数。
- **联集派**（球团）：Deer 躯干与 Daddy 球团**没有连接几何**——重叠椭圆片 + 同平色 + JaggedCircle
  噪声边缘蚀刻（+超采 5%~10%）直接读作一团；Daddy 另加瘤点噪声、黑根触手交叉遮挡
  （DaddyGraphics.cs / DeerGraphics.cs:696-709）。3D 归宿：同色 unshaded 椭球联集（平色下交线不可见）
  或 SDF metaball（后期增强）。

## 2. 十物种渲染策略速查

| 物种 | 身体 | 肢体/附肢 | 独有机制 | 3D 移植难度 |
|---|---|---|---|---|
| Lizard | 4 样点条带+圆片，尾条带同宽度对接 | 整腿 atlas LUT（54 图）→ 3D 换两段扫管 | 头色脉冲、SpinePosition 装饰总线、白蜥摄像头拾色伪装 | 中（基准件） |
| Centipede | 逐节中心差分切线 + 底管 + **实例化背/腹甲片** + 关节暗环 | N×2 真抓地腿 + 2 sprite IK，行波相位 | 三控制 roll 沿链 Slerp；甲片高光随 roll 游走；掉甲=碎片实体 | 中 |
| BigSpider | 2 chunk 当控制点的贝塞尔泪滴 + tailEnd 粒子 | 8 条 3-sprite IK 腿（膝跨接）| verlet 毛 fringe + 呼吸炸毛；JaggedSquare 噪边 | 中低 |
| Vulture | 躯干**一张图**（替换派）+ 脖/翅臂/飘带管带 | **羽毛扇**：弧长锚点 + 逐羽弹簧粒子 + 折叠三模式 | flyingMode 形变剖面；羽毛色波；9 帧头 turnaround | 高（翅膀系统） |
| Scavenger | 3 chunk→5 点 spine + 收腰带 + 脖管 | 手/脚粒子→整臂/腿单网格（无关节缝）| 体表 (x,y) 参数化装饰总线；耳角生成器 | 中 |
| Deer | 椭圆片联集（JaggedCircle 毛边）| 腿=双 IK 混合 + 7 段带（±7.5px 预倒角）| **鹿角生成器本来就是 3D 的**（Vector3 生长，投影 2D）——移植白捡 | 中 |
| Daddy | 球团联集（无连接几何，五重障眼法）| 触手 4× 密化管 + 黑根梯度 | 逐球假瞳孔（BulgeVertex 球面包裹）；悬索/死腿 | 中（球团难点在 3D 联集观感） |
| DropBug | 贝塞尔控制点 + shaped hump 剖面（悬挂充气 ×2）| 纯图形腿：中点松弛膝 + 双贝塞尔 C1 | 光照侧 shine 条带；palette 拾色天花板伪装 | 低 |
| Cicada | **替换派**：flipbook 壳→3D 刚性壳网格 + 低通 roll | 翅=根枢轴四边形，`alpha=|roll|³` 即拍翅模糊 | 分层视差（头/眼跟看点位移）；懒翅/坏翅个体 | 低（现有 renderer 已半正式） |
| TentaclePlant | —（固定根）| 40 段管+50 条海带 verlet | 弧长 pin + 自动柄贝塞尔的最小参考实现 | 低（现有 renderer 已半正式） |

## 3. 3D 渲染架构（Godot 4 / C#）

### 3.1 分层（≙ RW 结构逐层对位）

```
[内核 ProcAnim.Core]  只读观测面：Chunks[i].LerpPos(t)/Radius/Rotation、肢体 LerpPos/状态、
      │               SupportNormal、物种专有量（HangFactor/RunCycle/WingFlap/…）——零回写
      ▼
[化妆层 CosmeticState]  固定 tick（跟内核 40Hz 同节拍、排在控制器 Tick 之后）：
      │   drawPositions 双缓冲 + bob/呼吸/抖动偏移；图形层粒子（tailEnd、danglers、羽毛、
      │   bend pole 记忆）。纯 C# 可测；用独立渲染 RNG（或 sin 相位），**不进确定性哈希**
      ▼
[几何层 MeshBuilder]   每渲染帧：样条采样（Catmull-Rom+虚拟端点）→ 平行传输 frame
      │   （up 种子=SupportNormal，近共线用上帧 up——CLAUDE.md 3D 朝向边界）→ 解析半径剖面
      │   → 截面 8~12 边环 → 固定拓扑 ArrayMesh 只重填顶点/颜色数组；装饰=MultiMesh
      ▼
[材质层 Palette]       StandardMaterial3D Unshaded + VertexColorUseAsAlbedo；
                       调色板结构体 {blackColor, effectColor, fogColor}；SetPalette ≙ ApplyPalette
```

- **CPU 每帧重建、固定拓扑**是有意为之：与 RW 逐帧写顶点 1:1 对应，单生物 200~600 顶点
  在任何硬件上可忽略；shader 蒙皮/SDF 留作后期优化，不是架构。
- 插值 alpha 用现成的两级合成形式（DaddyLongLegsSandboxWorld.cs:2150-2158 的
  tick 余数 + 引擎物理帧分数），所有位置读 `LerpPos(alpha)`。
- 共享基建（一次建、十物种复用）：`SplineSampler`（含 RW 自动柄语义）、`ParallelTransportFrame`、
  `TubeMeshBuilder`（固定拓扑+剖面函数+逐顶点色）、`TwoBoneIk`（acos 钳制 [0.2,0.98]）、
  `CosmeticParticle`（ConnectToPoint 逐行移植）、`RenderPalette`。
- 位置：游戏程序集 `scripts/render/`（依赖 Godot，不进 `core/`）；每物种一个
  `XFormalRenderer`，与现有 debug renderer 并存、沙盒可切换。debug renderer 是回归工具，不删。

### 3.2 确定性边界（不可协商）

- 渲染层对内核**只读**；化妆层粒子/抖动一律独立 RNG 或 sin 相位，绝不碰内核随机与哈希。
- 全部矩阵/冒烟基线在渲染层落地前后**逐位不变**（金标准同 M5：改动前后矩阵输出 diff 为空）。
- 化妆层若需要地形（羽毛刷墙、danglers 碰地），走 `ITerrainQuery` 被动查询，量入渲染预算，不进内核 query 计数。

### 3.3 主项目回迁约束对齐

- 主项目低保真管线（Bayer 抖动 + 28 阶量化 + 0.5 渲染缩放、无阴影、偏好大块清晰剪影）与
  RW 平色剪影风格**天然同向**——unshaded 平色 + 轮廓自发光正是它要的输入。
- 主项目 M10 正式管线是 Skeleton3D+刚性零件（spec §4.3 把本内核当"可替换视觉后端"）；
  本项目的变形网格渲染是**超集验证**，回迁时既可整体作为视觉后端（姿态 1），也可降采
  为"chunk→bone pose 适配器"。不以主项目当前裁决束缚本项目的技术验证。
- 性能包络参考：24 实例 ≤3.0ms/tick、≤512KB/tick；固定拓扑重填 + 共享材质 + MultiMesh
  装饰在此包络内富余。

## 4. 技术验证选型：Lizard + Centipede + Vulture

三只覆盖全部五个技术族，其余七只全是它们的子集组合：

| 技术族 | Lizard | Centipede | Vulture | 其余物种复用 |
|---|---|---|---|---|
| A. 连续扫管身体（render spine 密化 + 剖面 + 端帽）| ✅ 身+尾同参数化 | ✅ N 节中心差分 + 虚拟端点 | ✅ 脖/翅臂/飘带 | 蜘蛛/DropBug（控制点变体）、人形、拟态草 |
| B. 肢体扫管 + 两骨 IK + bend pole | ✅ 4~6 腿 | ✅ 腿群行波 | —（翅臂即 A）| 鹿、人形、蜘蛛 |
| C. 实例化装饰（MultiMesh）| （可选鳞刺）| ✅ 背/腹甲片 + 关节暗环 | ✅ **羽毛扇**（含逐羽模拟）| Daddy 瘤点、蜥蜴鳞 |
| D. 刚性替换件 + 低通姿态 | 头部小件 | 头 | ✅ 躯干整件 + 头 | 蝉全身 |
| E. 化妆层生命感（bob/呼吸/垂坠/死亡涌现）| ✅ 呼吸+bob+头色脉冲 | ✅ idle 触须 + 甲片高光 | ✅ 折翼三模式 + 飘带 verlet | 全员 |

选型理由：
- **Lizard**：基准件与最难的"去球感"案例（3 chunk + 尾还要读成一条连续锥体）；品种最多
  （default/heavy/sprinter/hexapod），一次验证参数化覆盖度；A+B+E 全含。
- **Centipede**：扫管的多节极端 + 实例化装饰 + 腿群——蜈蚣好看=方案对大节数成立；
  甲片/暗环是 RW 最"识别度"的连接美学，验证"装饰挂截面站位"。
- **Vulture**：唯一的翅膀/羽毛系统（C 的高配）+ 替换派躯干（D 的正版）+ 三种 verlet 附肢；
  飞行生物的正式渲染无从其它两只推导，必须单独验证。
- 未选的理由：蝉/拟态草现有 renderer 已半正式（椭球+锥管），提升空间小；蜘蛛/DropBug 是
  A+B 的低配变体；Deer/Daddy 的联集策略独特但风险低（同色 unshaded 联集在 Godot 里是
  已知成立的），且鹿角生成器移植是独立小项目；人形等主项目造型裁决（Gate D）后再做正式皮。

## 5. 实施状态（2026-08-05 技术验证轮已落地；同日追加 DaddyLongLegs；
## 2026-08-06 追加 Spider + Humanoid）

六个验证件已实现于 `scripts/render/`（游戏程序集，依赖 Godot，不进 `core/`）：

- **共享基建**：`SplineSampler`（Catmull-Rom + smoothstep 剖面过渡）、`TubeMeshBuilder`
  （ImmediateMesh 每帧重发 + 平行传输 frame + CPU 包裹漫反射烘顶点色 + AddFin 鳍片 +
  AddBlade 羽毛刀片）、`TwoBoneIk`（余弦钳制 [0.2,0.98]）、`IFormalRenderer` +
  `FormalRendererFactory`（按物种适配器分派，未覆盖物种回落白盒）。
- **LizardFormalRenderer**：身尾一条连续扫管（头前伸点 + 脊柱/尾链 + 尾尖外推；显示半径
  与物理解耦——脊柱 ×0.72~0.85 收窄修长）、呼吸门控鼓胸、尾梢识别色梯度、腿两骨 IK 扫管
  （踝控制点把识别色压到足端）、头椭球 + 双眼 + 识别色慢脉冲、背脊鳍片（截面背侧站位）。
  四品种调色板：default=粉/heavy=绿/sprinter=黄/hexapod=青（对齐 RW 血统）。
- **CentipedeFormalRenderer**：近黑底管（含头尾虚拟外推）+ 每节扁椭球背甲（中心差分切线 +
  逐节**真实** SupportNormal 时域低通定向——RW 假 roll 的真法线替代）+ 甲片间隙露出暗管 =
  节间暗环 + 腿两骨 IK 细管（外侧上拱 pole）+ 两端正弦摆触须（领航端更长）。四预设调色：
  short=橙/long=红/armored=铜/ribbon=绿。
- **VultureFormalRenderer**：躯干替换派椭球（K4 刚架给完整正交基，roll 来自双肩轴）+
  肩盾 + 骨白头/暗眼/脖管 + 翅臂扫管（FlyingMode 收放剖面）+ 羽毛刀片扇（sqrt 链位、
  后掠随链位、轮廓包络下限保互叠、根黑梢亮渐变、**逐羽方向/长度滞后低通** +
  切线与整臂方向空间混合保扇面连贯）。四品种羽色：vulture=暗红/king=冰蓝/swift=橄榄/quad=紫。
- **DaddyLongLegsFormalRenderer**（≙ DaddyGraphics 全套障眼法的 3D 移植；独立沙盒
  `scenes/daddy_long_legs_sandbox.tscn`，不走 FormalRendererFactory）：
  - **球团**：逐球共享同一张纯平黑 `AlbedoColor` 材质（RW「同色无描边→球间棱线不可见」
    在 3D unshaded 下原样成立，**不需要 metaball/SDF**），显示半径 `×1.1+0.05m`
    （≙ rad*1.1+2px）加深互穿。
  - **X 眼**：每球一个朝质心外向的双笔画十字（AddFin 中心宽底双三角——四臂尖根方案会
    读成四角星，实测否决），seed 定相位角 + 亮度慢闪；apex/臂梢抬到球面上方
    （直线刀片的弦会沉入球体），遮埋测试按邻球**渲染**半径（按物理半径测 X 会藏在
    邻球渲染面下）。
  - **触手**：锚球中心起笔的 Catmull-Rom 扫管（root melding：前段纯剪影黑渐染
    ≙ OnTubeEffectColorFac；RW 0.3/0.4 参数在 3D 下整腿发黄，收紧为 45% 起染、梢端
    ≤22%）；`BacktrackFrom` 处断管成两段、颜色保持原链分数，绝不跨阻断边平滑；
    stun 触手渲染侧喂点抖动。
  - **疣珠**：seed 冻结普查（≙ graphicsSeed 段），沿管弧长 + 环向角 + **径向偏移可超管径**
    （≙ OnTubePos.x）散布 AddKnob 细分八面体小瘤，部分带暗亮 glow 复瘤。
  - **垂索/死腿**：纯渲染侧 verlet 垂坠索（≙ DaddyDangleTube/DaddyDeadLeg：半重力 +
    i±2 拉直 + 双程距离约束每程重钉端点；垂索自然段长 = max(下限, 端距/n) → 端点靠近时
    富余长度垂成深环），端点钉在球或触手近根段，死腿单端钉 + 近根径向外推 + deadness 压暗。
  - **sRGB 顶点色教训**（重要）：带 tonemap 环境的场景里 `StandardMaterial3D` 顶点色默认按
    **线性**解读，写入的剪影黑 0.058 被抬亮 ≈4×成灰褐，管与球异色、融根失效；
    `TubeMeshBuilder.Build(srgbVertexColors: true)` 使顶点色与 AlbedoColor 同空间，管根与
    球写同一数值 → 屏幕同色。既有三物种的调色在线性解读下定型，翻转需整体重调（遗留）。
  - 三预设调色：daddy=黄橙 X 眼+橄榄黄梢（按用户参考图），terror=RW 大型正统蓝，
    brother=橄榄+棕橙眼（≙ 小型 plain）。
- **SpiderFormalRenderer**（≙ BigSpiderGraphics；蜘蛛专用沙盒，不走 FormalRendererFactory；
  逐行取证 = scratchpad spider_scav_render/bigspider_graphics.md）：
  - **身体**：头前伸点→腹（双控制点）→渲染侧 tailEnd verlet 粒子的三点 Bezier 变径扫管
    （≙ MakeLongMesh(7)；半径剖面**有意偏离**原作 `Sin(Pow(f,0.75)π)` 单峰——那个剖面在
    3D 读作扁圆栗子，2026-08 按用户示意图改为**沿弧长的椭圆叶**：中心 f=0.62、半宽 0.36，
    修长椭腹的细腰谷与尾收锥由椭圆两端自然给出），头叶瓣以第二峰并进同一条剖面（RW 独立
    头椭圆 sprite 的单管等价）。tailEnd 挂腹后 1.6×腹半径 = 椭腹轴向长度来源（圆栗子时代
    取 0.75×，拉远会拖成尖喙——椭圆剖面下该教训不再适用）；tailEnd 追踪与呼吸幅度全部
    收硬（高阻尼 0.55 + 强回中 + 呼吸 ±2mm/毛鼓张 ±8%——软弹簧参数让后腹 Q 弹地晃，
    用户实测不适合蜘蛛，微漂只保留「活物」底噪）。
  - **腿**：内核两段 IK 姿态（Root/Knee/Foot **直接消费**——蜘蛛后端本就为此输出膝点）
    画成股节/膝结小瘤/胫节/爪尖四件（≙ 屏幕三段贴图 1.5×/1.2×/1.2× 粗细梯度；直线管 +
    膝瘤保锐利折角——Catmull-Rom 会把节肢感的硬关节磨圆）；股节起点沉向体轴 30% 点融根
    （≙ 全腿肩点聚拢画法）；逐腿个体粗细 seed 冻结（≙ legsThickness 0.7~1.1）。
  - **腹毛**：verlet 密细链阵（≙ scales 系统；密度按 2D 半圆弧→3D 球面换算 48/64/14——
    稀疏粗毛的单根远侧遮挡弧读作悬空逗号，密细毛读作绒毯），线性黑根亮尖渐变（≙ RW
    ApplyPalette 原式；曾用 pow(t,1.4) 压暗中段，横穿轮廓段在灰背景上隐形 = 假悬空）。
    **贴体四件套**（悬空毛四轮实测逐层逼出）：① 根锚距 = 对本帧剖面**锥台链**二分求交再
    微沉融根——球面与站球并集都会在细腰/尾锥的斜向鼓包上悬空 5~7cm（锥台链才是 loft 的
    正确近似）；锚剖面只收**腹叶主导**且半径 ≥30% 峰值的「肉身」站——细尾管排除（锚上
    近隐形细管 = 悬在肥腹旁），头叶瓣排除（前倾毛向的射线打中头叶 = 毛垂在脸前，RW 鳞毛
    长腹背不长头脸），毛向普查另避开正后极（back ≥ −0.86）；② 自由节对锥台链穿透排斥（贴体耷拉读成花纹）；
    ③ 整链贴体薄壳钳制（离面高度 ≤ 0.02+0.06t²，根紧梢松）——远侧毛弓离体面太高时弧根
    被身体遮挡、只露弧梢 = 悬空逗号，壳内可见段永远连着轮廓线；④ 外梳方向 40% 径向 +
    60% 表面切向后方（≙ RW 鳞毛整体向后掠）：后掠毛在远侧沿轮廓线方向伸出，且天然给出
    参考图的「屁股毛边」。毛链同时承担 JaggedSquare 毛边职责。
  - **螯肢**：头前一对短管 sin 蠕动（≙ mandibles RNV 抖），根部微染 accent。
  - 有意偏离：不移植 deadLeg（RW 腿纯图形可装瘫，本项目腿真实承力会与迈步矛盾）、
    不移植 flip/膝压平（2D 滚转伪装）。三预设：small=黄毛基准 / large=Spitter 红毛 /
    lean=群居小蜘蛛气质（近乎无毛全黑细腿）。
- **HumanoidFormalRenderer**（≙ ScavengerGraphics；主沙盒 FormalRendererFactory 分派；
  逐行取证 = scratchpad spider_scav_render/scavenger_graphics.md）：
  - **躯干**：五点脊柱扫管（头/脖/胸/**背凸腰点**/髋 ≙ drawPositions[5,2]）——驼背「?」
    剪影的几何本体 = 腰点沿背侧法线外推 `Lerp(5,15,narrowWaist)` 的 3D 直译；宽度剖面
    吃 fatness/narrowWaist 基因（沙漏楔）。背饰 = 脊背 UV 鳞片刀片阵（≙ LizardScale 复用，
    dominance 定大小，sin 微摆）。尾 = 渲染侧 verlet 短锥链（0~4 节）。
  - **头脸（怪异感核心）**：近黑竖长椭球（≙ light 0.05~0.2 生而近黑 + 头永不亮过身体
    硬规则）+ **头色牙刀片**从脸下缘辐射刺出头轮廓（≙ TeethSprite——颅骨下颚锯齿而非
    白牙，20%/颗缺牙、aggression→长牙）+ 微小满饱和对比色眼（斜吊向共同汇聚点
    ≙ eyesAngle、眨眼抹零、昏迷 0.5 半睁死鱼眼、sympathy 个体带追视瞳孔）+
    **eartlers 鹿角须**（≙ 2~4 对手工分支模板：主支上弯/分叉/鬓角/后枕，头局部系镜像展开、
    dominance 定尺寸 `Lerp(15,35)px`、角尖染饰色）。头朝向 ≙ HeadDir 但**必须压掉竖直
    分量**——本项目头挂胸上方 0.55m（RW 2D 头在前），照抄权重会仰面、牙横长（实测）。
  - **脖管**：根粗头细中段掐一口 + **顶点色体→头渐变**（≙ 脖 customColor mesh——近黑头
    从彩色身体上长出来不突兀的全部秘密）。
  - **四肢**：Limb/Arm 无膝肘输出——渲染侧 TwoBoneIk + 私有 pole（臂=外后上肘、
    腿=前向蹲膝）；臂细竿（≙ 0.75~2.75px）从肘起向头色渐变、手深色瘤（≙ handsHeadColor
    手套 + HandB 抓握二态双瘤）；腿粗随体格（重型庞躯配牙签腿脱节，实测）；摆动腿脚板
    方向混入本腿姿态（硬指行进向会悬空外飘成小旗，实测）。
  - **表情标量**：blink/eyesOpen 状态机（nervous→勤眨、energy→睁速）+ 神经质待机微颤，
    渲染侧私有 PRNG/sin，不进哈希。持物/投掷道具画**长矛**钉 MainHandPos/Dir
    （≙ grasp 0 硬钉；飞行矛朝向 = 渲染侧前帧差分）。
  - 三预设档案（性格 6 元组 + 手工定档色，形状基因仍 seed 冻结）：scavenger=土黄身黑脸
    黄绿眼；brute=暗棕重型（dominance/aggression 高→巨黑角+长牙+缝眼红瞳）；
    waif=灰绿瘦小（sympathy/nervous 高→大眼冰瞳追视+勤眨+微颤）。
  - 两个新渲染器都走 `srgbVertexColors:true`（Daddy 轮教训：新件直接在所见空间调色）。

沙盒集成：V 键切换正式/白盒；`--formal=off` 起动白盒；视觉验证回路 =
`--screenshot=path[@tick]` + `--camfollow=ox,oy,oz`（跟踪相机）/`--cam=…`（定点）+
`--autowalk=dx,dz`（恒定行走）。Daddy 专用沙盒同构复刻这套旗标（前缀制：
`--daddy-screenshot/--daddy-cam/--daddy-camfollow/--daddy-formal=off`，V 键同义；正式视图下
地形查询调试线随白盒一起隐藏——`RayDebugDraw.Draw` 每帧必须照常调用清面，只临时压
Enabled）。**蜘蛛专用沙盒**同构接线但沿主沙盒**无前缀**旗标名（其参数空间独立），
`--camfollow` 注视 Primary/Rear 中点、正式视图同样压地形调试线；**人形**在主沙盒经
`FormalRendererFactory` 分派（`HumanoidSandboxCreatureAdapter` case），`_Process` 人形
早退分支内做与主路径同构的正式/白盒仲裁，`HumanoidRenderer`/`SpiderBodyRenderer` 补
`SetVisible` 纳入 `ApplyRenderView`；`--autowalk` 补人形分支支持；持矛截图可用
`--route=hact --determinism=900 --screenshot=…@560`（窗口化 determinism 照常渲染，
截图在持物窗口内）。验收：45 配置主矩阵（含人形 8 项）GREEN、哈希逐位命中基线
（渲染只读实证）；蜘蛛专项 16 配置 GREEN、13 条钉死哈希全中；Daddy 40 配置不受本轮影响。
截图对照 RW 参考图（蜥蜴 heavy vs 绿蜥、蜈蚣 short/long vs 橙/红蜈蚣、秃鹫 fly vs
展翅参考、daddy vs 黄色长腿爸爸、spider-lean vs 黑蜘蛛剪影、scavenger vs 沙褐/暗色拾荒者）
均达到剪影级相似。

**遗留打磨项**：羽毛升级为完整弹簧粒子（折翼过渡相扇面分组散开）、蜥蜴走路 bob/头件形状
（楔形头+眼位）、蜈蚣甲片高光游走、肩盾/飘带 verlet、死亡 Dangle 表现、低保真后处理
（抖动+量化+降采样）联调、既有三物种切换 sRGB 顶点色空间后整体重调色、Daddy 假瞳孔
（BulgeVertex 球面包裹瞳点）与眼睛看向目标、蜘蛛蓄力抖动通道（本项目暂无咬击意图量）、
人形 ShockReaction 复合表情宏与 bristle 炸毛 verlet（待恐惧/激动观测量）、人形腹面浅色
胸口补丁（bellyColor chest patch）、waif 长脖比例复核。后续物种按 §2 速查表以既有基建拼装。

## 6. 附录：本轮取证的完整行号索引

十份逐类分析（sprite 清单/身体几何/肢体/配色/打磨/去球感手法/3D 映射/行号）与基建分析
归档于会话 scratchpad`rw_render_research/`；Spider/Scavenger 轮的两份逐行取证
（BigSpiderGraphics 719 行 / ScavengerGraphics 2492 行 + 5 个 ScavengerCosmetic 类）
归档于会话 scratchpad`spider_scav_render/`；关键行号已内联于上文各节。反编译源
`~/workspace/others/rw_decomp/`：本轮新增 CentipedeGraphics / BigSpiderGraphics /
CicadaGraphics / DeerGraphics / TentaclePlantGraphics / GraphicsModule / TriangleMesh /
Centipede / BigSpider / Cicada（连同既有 LizardGraphics / ScavengerGraphics /
VultureGraphics+VultureFeather / DaddyGraphics / DropBugGraphics）。
**边界不变：反编译源仅本机参考，不入仓、不再分发；落进本项目的是自己的实现。**
