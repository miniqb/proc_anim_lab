# Deer 并列控制器

`DeerLocomotionController` 是与 `LizardLocomotionController`、`SpiderLocomotionController`、
`CentipedeLocomotionController` 等并列的物种后端。鹿的身体是头、粗重叠躯干链和大而轻的
鹿角块；四条腿各自是一条多节物理链。它不是蜥蜴品种，不读取 `BreedParams`，也不复用
单粒子 `Limb`。

本轮只实现内核运动、独立出生配置、宿主接口、白盒沙盒与专项回归。正式材质、毛发、鹿角
网格和外形美化不在范围内；调试渲染只负责把身体球、每个腿段、落点、支撑量、局部坐标系
和离地高度画清楚。

白盒里米黄色的大球是原作 chunk 5 对应的**鹿角物理代理**，不是路径块或额外躯干节；本轮必须
保留它的真实半径与质量来验证头角姿态，但不把球体美化成正式鹿角网格。它的中心应在头上方
略向前，只有与头球约定的表面重叠，不得埋进任一躯干节。

## 1. DLL 直接取证边界

真相源是本机用户自有 Rain World 程序集：

- 路径：`/Users/miniz/workspace/others/Managed_extracted/Assembly-CSharp.dll`；
- SHA-256：`b6be1d4e18ce219d21091b51564cb6a11c1e4106b41de903eb8e58849cb16fdb`；
- 反编译器：`ilspycmd 10.1.1.8388 --disable-updatecheck`；
- 直接读取类型：`Deer`、`DeerTentacle`、`Tentacle`、`DeerGraphics`；
- 辅助核实类型：`PhysicalObject`、`PhysicalObject.BodyChunkConnection`、`BodyChunk`、
  `RWCustom.Custom`、`DeerAI`、`DeerPather`、`DeerAbstractAI`。

主要证据点来自 `Deer` 构造函数、`NewRoom`、`Update`、`Act`，`DeerTentacle` 构造函数、
`NewRoom`、`Update`、`UpdateDesiredGrabPos`、`FindGrabPos`、`Support`、`ReleaseScore`、
`ReleaseGrip`，以及 `Tentacle.TentacleChunk.Update`。后腿配对表达式另以 IL 复核，排除了
C# 反编译器重建错误的可能。

反编译源码只留在仓库外用于互操作研究；本仓库记录结构、公式、3D 取舍和自有实现，不提交、
复制或分发反编译文件。下文凡称“原作”，均指上述 SHA 对应的当前 DLL，而不是文档索引或印象。

## 2. 原作结构与关键数值

### 2.1 头、躯干和鹿角

原作 `bodyChunks` 固定为 6 个：

1. chunk 0 是头，半径 `22.5 px`、质量 `3`。
2. chunk 1..4 是从头侧到尾侧的四节躯干。令 `t=i/4`：

   ```text
   shape0 = (1 - t) * 0.5 + sin(sqrt(t) * PI) * 0.5
   shape  = max(0, lerp(shape0, 1, 0.2)) ^ 0.7
   radius = lerp(10, 35, shape)
   mass   = lerp(1, 8, shape)
   ```

   当前 DLL 的 float 结果近似为：

   | chunk | 半径 px | 质量 |
   |---:|---:|---:|
   | 1 | 33.2225 | 7.5023 |
   | 2 | 29.8310 | 6.5527 |
   | 3 | 24.5925 | 5.0859 |
   | 4 | 18.1033 | 3.2689 |

   这意味着当前 DLL 实际是第一躯干节最粗最重、再向尾侧递减，并非严格的几何中点峰值。
3. chunk 5 是鹿角物理块，质量恒为 `0.5`，且 `collideWithObjects=false`。legacy 半径为
   `lerp(30,70,dominance)`；启用 MMF DeerBehavior 时为
   `lerp(30, 60 + 20*InverseLerp(0.8,1,dominance), dominance)`，最高可到 `80 px`。

距离连接固定为 5 条：

- 头 0 到躯干 1：`38 px`；
- 躯干 `j` 到 `j+1`：`0.8 * max(radius[j], radius[j+1])`，约
  `26.578 / 23.865 / 19.674 px`；
- 头 0 到鹿角 5：`headRadius + antlerRadius - 10 = antlerRadius + 12.5 px`。

前三条躯干节距只有相邻半径和的约 42%–46%，所以它是一条粗而软、球体大幅重叠的躯干，
不是细长脊柱。所有连接都是 `Normal`、elasticity `1`。身体连接以质量自动分配误差；鹿角连接
明确使用 `weightSymmetry=0`，距离误差全部修鹿角块，不直接挪头。

`BodyChunkConnection` 构造时会互设 `RotationChunk`，后建连接覆盖先建连接；Deer 构造末尾又
显式重申头与鹿角互指。最终朝向拓扑是：

- `head(0) <-> antler(5)`；
- `trunk1 -> trunk2 -> trunk3 -> trunk4`，`trunk4 -> trunk3`。

`BodyChunk.Rotation` 只是 `(Pos - RotationChunk.Pos).Normalized()`；只有引用为空才回落世界
Up。项目工厂也必须在全部连接建完后显式钉定头角互指，不能依赖建链顺序巧合。

### 2.2 四条独立六段腿

原作腿类是 `DeerTentacle : Tentacle`，不继承 `Limb`：

- 腿 0/1 锚在躯干 chunk 1，腿 2/3 锚在 chunk 2；
- MMF DeerBehavior 构造时的初始 `idealLength` 为 `300 px`，legacy 为 `220 px`；这不是
  运行时最大可达长度；
- 每腿有 6 个 `TentacleChunk`，`tPos=(i+1)/6`；构造半径 `4 px`，每 tick 改为 `5 px`；
- `stretchAndSqueeze=0.1`；
- 相邻段目标长为 `idealLength/6`，`stiff=false`，所以只抗拉、不抗压；误差按
  `massDeteriorationPerChunk=0.5` 对半分；
- 根连接 `pullAtConnectionChunk=0`，普通腿链约束不会把反力传回躯干。躯干升力是 Deer
  根据腿的支撑标量另行注入，而不是足链被动撑起 body chunk。

原作 `TentacleProps` 的完整运动值为：

```text
stiff=false, rope=false, shorten=true,
massDeteriorationPerChunk=0.5, pullAtConnectionChunk=0,
goalAttractionSpeedTip=0.2, goalAttractionSpeed=0.1,
alignToSegmentSpeed=1.04, backtrackSpeed=1.2,
chunkVelocityCap=10, tileTentacleUpdateSpeed=1,
tileTentacleSnapWithPathDistance=5,
segmentPhaseThroughTerrainFrames=15,
tileTentacleRecordFrames=60, maxPullTilesPerTick=12,
terrainHitsBeforePhase=0
```

每条腿的固定二维偏好为 `DegToVec(45 + 90*k)`；原作 `DegToVec(a)=(sin(a),cos(a))`，四个
方向依次是右上、右下、左下、左上。`side=k%2` 在 `DeerTentacle` 物理中没有被读取；
`pair=(k>=2?1:0)` 只改变前后腿的移动落点权重。因此这些二维方向同时混合了前后位置和
画面正背侧，不可以机械地把 X/Y 分量抄成 3D 左右腿。

### 2.3 躯干姿态与防折力

原作 `WeightedPush(A,B,dir,force)` 按质量分配速度：

```text
w = massB / (massA + massB)
velA += dir * force * w
velB -= dir * force * (1 - w)
```

在身体积分前，Deer 每 tick 对躯干 1↔3、1↔4 沿身体方向各施加 `0.35`；清醒且非跪姿时，
头↔躯干 1 分别沿 `HeadDir` 与 `bodDir` 施加 `0.85`、`0.6`，鹿角对 chunk 0..3 的姿态力
依次为 `1.1 / 0.8 / 0.5 / 0.2`，尾侧 chunk 4 与鹿角另有 `0.6` 水平展开力。鹿角速度每 tick
乘 `0.92`。

把当前 DLL 中作用于鹿角的这些二维方向力按稳定站姿合并，主轴的前向/向上分量约为
`0.17756`：也就是**明显向上、只略向前**，并非头后或躯干中心。项目不逐项复制二维
WeightedPush，而以 `normalize(Up + Forward*0.18)` 保存这一合力方向。

身体积分后，对 `n=0..2` 恒做：

```text
WeightedPush(n, n+2, -Dir(chunk[n], chunk[n+2]), 0.45)
```

这是隔节、无距离缩放的持续展开力，明确负责维持整体姿态和防止对折，不是相邻距离连接。
`bodDir` 同时由三组“后一节指向中间节”的方向相加，清醒时再加 `0.8*flipDir` 的水平偏置后
归一化。二维原作没有 roll/twist 自由度，因此不存在可直接照搬的 3D 横滚限制。

原作基础物理还设置 `airFriction=0.999`、`gravity=0.9`、`bounce=0.1`、
`surfaceFriction=0.4`。水下参数不属于本项目范围。

## 3. 原作运动公式

### 3.1 单腿支撑

未吸附足端的腿支撑为 0。已吸附时，令：

```text
d = dot(WorldDown, normalize(Tip - Anchor))
```

原作再扫描其余已吸附腿，只接受 tip 与本腿 tip 分处 `bodyChunks[2].x` 两侧者。对每个这样的
腿计算：

```text
qj = sin(PI * InverseLerp(400, 0, abs(body2.x - otherTip.x))) ^ 0.3
q  = max(qj)
lower = lerp(threshold, -1, q)
Support = InverseLerp(lower, 1, d)
```

MMF DeerBehavior 的 `threshold=0.8`，legacy 为 `0.5`。没有另一侧支点时，腿必须接近锚点正下方
才贡献支撑；前后展开的另一支点会显著放宽直立阈值。这里的“另一侧”是二维世界 X 的躯干
前后侧，不是 3D 左右脚侧。

### 3.2 总支撑、常开重力与不均匀升力

令 `S=sum(leg.Support())`：

```text
support0 = min(S / 3, 1) ^ 0.8 * (1 - resting) ^ 0.3
```

原作随后有两种房间级兜底：chunk 0..4 每出现一次侧墙接触或下方接触，就顺次做
`support0=lerp(support0,1,0.6)`；接近房间水平边缘时再向 1 插值。最后：

```text
support = support0 ^ 0.1
forwardPower = lerp((legsGrabbing / 4) ^ 0.8, support, 0.5)
```

重力从不因抓地关闭。Deer 对 chunk 0..3 注入世界向上速度，chunk 4 没有该升力。令
`t=i/5`：

```text
liftWeight = lerp(1.3, 2.5, sin((t ^ 1.7) * PI))
heightGain = LerpMap(smoothedFloorAltitude, 14, 18, 1, 0.5)
verticalIntentGain = LerpMap(moveDirection.y, -1, 1, 0.5, 1)
lift = gravity * liftWeight
     * lerp(support * heightGain * verticalIntentGain, 0.65, CloseToEdge)
```

chunk 0..3 的 `liftWeight` 约为 `1.3000 / 1.5427 / 2.0373 / 2.4619`。所以鹿的路线是“重力
常开 + 腿支撑量驱动非均匀升力”，与蜥蜴抓稳后 `GravityScale=0` 根本不同。

### 3.3 推进分布

chunk 0..4 的推进为：

```text
drive[i] = moveDirection
         * lerp(0.35, 0, i/5)
         * forwardPower
         * lerp(1, 3, GetUnstuckForce)
```

从头到尾权重为 `0.35 / 0.28 / 0.21 / 0.14 / 0.07`。原作同时按 support 把速度向
`stayStill ? 0.7 : 0.92` 阻尼，再乘 `1-resting*0.5`。推进既依赖抓地腿数，也依赖总支撑，
并明确前重后轻。

### 3.4 主动换步与释放评分

每 tick 先算四腿 `ReleaseScore`，取最高者；只有 `!stayStill && legsGrabbing>3` 才主动释放。
鹿只有四腿，所以正常 plant-and-trail 必须等四脚都踩实才主动抬下一脚。

全抓地时每腿评分等价于：

```text
score = 4 * distance(grabPoint, desiredGrabPoint)
score *= 1 + (Base.x - Tip.x) * sign(moveDirection.x) * 1.4
score /= 1 + Support * 10
```

若 `OtherTentacleInPair` 没有抓地，评分还会除以 100。目标偏离理想点越远、越落后于移动方向，
越容易松开；当前支撑越大越不容易松开。乘法因子没有钳位，原作评分可以为负。

`ReleaseGrip` 在自己与配对腿都没有冷却时，把自己设为 `15 tick` 冷却；若配对腿还在冷却，
自己不再加冷却，可以立即优先找地。这是同对保护的意图之一。

当前 DLL 有一处已由 IL 证实的索引缺陷：`OtherTentacleInPair` 实际为
`0->1, 1->0, 2->2, 3->1`，而不是期望的 `0<->1, 2<->3`。因此后腿保护并不完整；本项目不得
复制这个 bug。

### 3.5 犹豫的真实曲线

“前方有脚”的二维判定是任一已吸附 tip 满足：

```text
(tip.x > body2.x) == (moveDirection.x > 0)
```

若前方没有脚，非房间边缘时原作把推进替换为：

```text
forwardPower = LerpMap(hesistCounter, 20, 150, -0.5, 1)
```

counter 随后递增；一旦前方重新有脚就立即清零。也就是说当前 DLL 不是“逐渐减速”，而是首次
失去前脚时立即降到 `-0.5`，前 20 tick 保持反向刹车，再在 20..150 tick 逐渐恢复到 1。
这更接近“先强烈犹豫，久候后放弃犹豫”。

### 3.6 动态腿长、理想落点与候选滞回

原作每腿：

```text
maxLength = 20 * preferredHeight * 1.3333334
idealLength = min(
    lerp(oldIdeal,
         grabDest ? distance(Base,dest) * 1.5 * InverseLerp(15,0,grabDelay)
                  : maxLength,
         0.03),
    maxLength)
```

因此 `resting=0` 时 `preferredHeight=15`，运行时 `maxLength=400px=10m`；`resting=1` 时
`maxLength=133.33px=3.333m`。MMF 的 `300px=7.5m` 只对应出生初始理想链长，把它当成
活动最大腿长会把 Deer 的站姿整体压低四分之一。

只有 `resting==1` 时 `retractFac=1`，此时每节目标长缩到平常的 10%。原作理想落点为：

```text
stayStill:
  Anchor + normalize(Down + tentacleDir * 0.5)
         * min(nextFloorHeight, maxLength * 0.9)

moving:
  Anchor + normalize(Down
                     + moveDirection * (frontPair ? 0.8 : 0.6)
                     + tentacleDir * 0.25)
         * min(nextFloorHeight, maxLength * 0.9)
```

找地时先等待 `grabDelay`。之后从 Base 依固定顺序向 9 个目标发 tile ray：中心、左、左下、下、
右下、右、右上、上、左上；八个外围点相对 desired 各偏 `5 tiles`。它取第一个合格实体格，
不会把九个结果全局排序；若命中格正上方也是 Solid，则拒绝，故只认上方开放的可站立格。

候选吸引力通常是 `100 / tileDistance(desired,candidate)`。没有旧目标时直接采用；有旧目标时，
静止要求新分数大于旧分数，移动要求大于旧分数的 3 倍才重定向。已经踩实的腿根本不搜索新点；
三倍门主要抑制腾空寻点时的目标抖动。

Tip 距目标小于 `40 px`、地形回溯未阻断且尚未完全休息时，原作把 Tip 直接钉到目标并清速度。
腿段仍由 `Tentacle` 的 tile path、碰撞、回退和只抗拉约束更新。

### 3.7 失抓、极限与当前 DLL 的死字段

已抓地腿会在以下任一条件满足时释放：

- Base tile 到 grab tile 的 terrain ray 被阻挡；
- Tip 到锚点距离达到 `maxLength`。

距离达到 `0.9*maxLength` 时，当前 DLL 只写 `deer.heldBackByLeg=true`。全程序集对这个字段只有
这一处写入，没有读取；Deer.Update 也不清它。结合 `pullAtConnectionChunk=0`，当前 DLL 实际
没有“接近极限时腿反拖身体”的有效力学。这个阈值证明了原作意图，但不是已生效的约束。

### 3.8 休息与前方地板高度

`resting` 是连续量，不是 locomotion 模式：位于目的地附近、且目的地左右两个采样的
`floorAltitude<=5` 时，每 tick `+1/60`；否则每 tick `-1/160`。

```text
preferredHeight = 5 + 10 * (1 - resting) ^ 5
```

移动时约为 15，完全休息时为 5；脚下有 worm grass 时强制为 15。`nextFloorHeight` 以 `0.2`
低通：静止读取当前 AI tile 的 `floorAltitude+2`，移动读取前方路径格的
`smoothedFloorAltitude+2`，都钳到 0..17 后乘 `20 px`。这套数据来自 2D AI tile map，
不是物理射线。

原作没有显式的 BodyCenter 离地高度伺服；`preferredHeight` 在这里主要进入腿的动态
`maxLength`，身体高度由二维地板、腿支撑与其它姿态力共同涌现。因此本项目的 BodyCenter
行进/休息目标是 3D 设计量，不能把原作的 `5/15` 机械乘像素比例后冒充直接换算。

`NewRoom` 会把 `preferredHeight` 临时设为 55，重建每腿 tile path，并从腿根朝
`Down*400 + tentacleDir*100` 初始探地；下一次 Act 才把高度改回 5..15。这是房间初始化特例，
不是正常站立高度。

## 4. graphics-only 边界

`DeerGraphics` 不驱动 Deer 的躯干或足端物理：

- 可见腿额外使用前腿 `35/55 px`、后腿 `60/45 px` 的装饰段长；
- 它从六个物理 `TentacleChunk` 采样若干点，再做两次二维 `InverseKinematic`；弯折符号依赖
  faceDir/正背面，并与物理段位置混合；
- 这些膝、踝和 sprite 伸缩都是渲染姿态，不承力、不碰撞，也不参与 Support；
- `DeerGraphics.Antlers` 只生成鹿角网格；物理只认 body chunk 5；
- dangler、眼睛、眨眼、look、颜色和画面翻面均是图形状态。dangler 只单向读取腿段速度，
  不向腿回传。

因此本项目的持久 bend pole 是为真实 3D 多节腿新增的物理/姿态稳定机制，不是把
`DeerGraphics` 的二维 IK 当成原作腿物理。调试渲染必须只读核心状态，不能反向参与求解。

## 5. 本项目的独立装配契约

鹿的所有新代码位于 `core/species/deer/`：

- `DeerParams` 及躯干节/腿槽配置：纯出生数据、深拷贝、完整校验；
- `DeerFactory`：稳定 ID、参数快照和身体装配；未知 ID 快速失败；
- `DeerLeg`：新的多节腿原语、抓点、段链、冷却、支撑、释放评分和 bend pole；
- `DeerLocomotionController`：四腿统一调度、支撑、推进、犹豫、高度与姿态；
- 独立 smoke、Godot 白盒沙盒和矩阵脚本不进入 Core 程序集。

共享层零改动是硬边界：不得为鹿修改 `Body`、`BodyChunk`、`ChunkConnection`、`Limb`、
`LizardLocomotionController` 或任何其他物种。鹿只使用既有 `Body`/chunk/connection、
`TickContext`、`ITerrainQuery` 和 `DeterminismHasher`。核心不持有 Godot 节点，不调用场景树、
PhysicsServer、导航或随机数。

身体拓扑保持“一个头 + 有序重叠躯干 + 一个大轻鹿角”，四条腿固定组成前后两对，每对各一左
一右。参数表允许预设在体型、腿长、段数、支撑、步速、姿态和休息高度上分化，但不得把差异
写成控制器内的稳定 ID 分支。工厂必须冻结独立参数快照；改品种需要重新装配。

正式稳定 ID 固定为 `deer/original`、`deer/compact`、`deer/strider`；
`AllPresets()` 按这个顺序返回互不共享数组或对象的新快照，`ByStableId` 对未知值快速失败。
直接 `new DeerParams()` 得到的是供自定义配置起步的中段峰值模板，不是第四个正式预设；正式
三预设都从 `Original()` 深拷贝后出生分化，控制器内不得按 ID 写行为分支。

| 稳定 ID | 身体与腿的实际形态 | 主要运动差异 |
|---|---|---|
| `deer/original` | 按 `1px=0.025m` 取 DLL 头 `r=0.5625/m=3`；四躯干半径 `0.830563/0.745775/0.614812/0.452582m`、质量 `7.502312/6.552679/5.085896/3.268919`；连接长 `0.95/0.6644504/0.59662/0.4918496m`；以 dominance=0.5 的 MMF 鹿角取 `r=1.125/m=0.5`、头角表面重叠 `0.25m`。四腿各 6 段，初始理想长 `7.5m`、hard 最大长 `10m`，段/足半径 `0.125m`；腿段质量 `0.10` 是 3D 新增值。 | 四腿分别使用前后外撇 `(+0.68,+0.58,-0.55,-0.71)` 与左右外撇 `0.74/0.82/0.77/0.69`；BodyCenter 行进→全休息目标 `6→2.64m`，当前 reach `10→3.333m`，地板探测 `10.5m`，普通无输入 160 tick 后才取得休息资格。头轴为 `normalize(0.85Up+0.60Forward)`，鹿角轴为 `normalize(Up+0.18Forward)`。支撑注速上限 `0.055m/tick`、基础推进 `0.024m/tick`、推进 headroom 阈值 `0.095m/tick`；腿追逐/响应 `0.32/0.56`、冷却/确认 `15/3 tick`。 |
| `deer/compact` | 保持四躯干和 4×6 段拓扑；Original 身体线性尺寸乘 `0.72`、身体质量乘 `0.55`、腿长乘 `0.68`：初始理想长 `5.1m`、hard 最大长 `6.8m`；腿/足半径乘 `0.78`、腿段质量乘 `0.62`。 | BodyCenter `4.32→1.9008m`，当前 reach `6.8→2.2667m`，地板探测 `7.14m`，休息资格延迟 `160 tick`；腿追逐/响应 `0.44/0.72`、冷却/确认 `10/2 tick`、候选改善门 `0.10`；支撑注速上限 `0.060`、支撑低通 `0.46`、基础推进 `0.030`、推进 headroom 阈值 `0.115m/tick`。它是窄地形上的快速完整步态，不对应原作随机个体。 |
| `deer/strider` | 保持四躯干拓扑；Original 身体线性尺寸乘 `1.05`、质量乘 `1.10`，腿长乘 `1.25`：初始理想长 `9.375m`、hard 最大长 `12.5m`；腿/足半径乘 `0.90`、腿段质量乘 `0.85`；每腿由 6 段增为 8 段。 | BodyCenter `8.316→3.659m`，当前 reach `12.5→4.1667m`，地板探测 `13.125m`，休息资格延迟 `200 tick`；腿追逐/响应 `0.27/0.48`、冷却/确认 `18/4 tick`、候选改善门 `0.15`；支撑注速上限 `0.060`、支撑低通 `0.34`、基础推进 `0.021`，前探加长并提高 balance recovery。它用长腿和慢支撑转移表达审慎跨越，不只是全身统一放大。 |

三份参数均由工厂/拓扑 smoke 逐项装配验证；运动成绩与哈希见 §11.2，仍以脚本当前成功输出
和 smoke 内钉死常量为唯一真相源。

三预设的当前寻点/摆动可达上限统一按
`MaxLength * Lerp(1/3, 1, (1-RestAmount)^5)` 连续变化。已踩住但落在新上限外的脚不会四条
同时强制失效，而是在四脚确认支撑时按统一评分逐条执行“松脚—冷却—在缩短工作区落地”；
hard `MaxLength` 仍只作为遮挡/真实超距的安全边界，因此休息收腿不会让同对同 tick 一起腾空。

## 6. 3D 坐标系与四腿外撇

### 6.1 支撑坐标系

每 tick 从 `TickContext.GravityPerTick` 得到 `WorldUp`；零重力输入才回落 `Vector3.Up`。控制器
维护持久 `SupportNormal`：只汇总真实踩实、法线有效且坡角可站立的足端法线，再低通更新；
没有合法足端时向 `WorldUp` 恢复。HitFromInside 的零法线不能归一化或计支撑。

身体前向由 `Head - TailTrunk` 投影到 `SupportNormal` 切平面取得。投影退化时，把上一 tick
Forward 平行运输到新支撑平面，而不是从任意世界轴重猜；Right 由 Forward 与 SupportNormal
正交构造。这个持久 frame 同时服务落点、支撑多边形、腿 bend pole、躯干姿态和调试输出。

鹿不会沿墙爬。墙面或过陡坡面的命中只用于遮挡、足端/段链碰撞和 body MTD，不更新
SupportNormal，不产生升力。推进只取 MoveDir 在合法支撑切平面的分量；分量退化时停止推进，
不得像蜥蜴那样把推墙输入重定向成上爬。

### 6.2 四个固定外撇槽

每条腿用出生槽位保存 `PairIndex`、`Side`、锚定躯干节、`ForwardSplay` 与 `OutwardSplay`。
在持久支撑 frame 中，理想射线方向为：

```text
desired = normalize(
    -SupportNormal * DownWeight
    + TangentMove * MoveWeight
    + Forward * ForwardSplay * SplayWeight
    + Right * Side * OutwardSplay * SplayWeight)
```

前后两对的 `ForwardSplay` 符号不同；同对左右以 `Side` 分开；四个槽仍可各自覆写幅度，所以
它们不是两个镜像模板硬复制。身体转向或走上斜坡时，偏好随持久 frame 平行运输；已经踩住的
世界抓点不滑动，只有下一次抬腿才在新 frame 中捕获新落点。

原作“另一侧支点增强”在 3D 中推广为支撑平面上的展开度：把已踩实足端相对身体中心投影到
支撑平面；另一只足端落在相反半平面、且两点有真实间距时才给 bonus。这样前后跨步和左右宽站
都能增加稳定性，四脚挤在身体正下方不会只因数量多而拿到满 bonus。该项仍与单腿的直立度相乘，
不能让一条几乎横躺的腿凭远处另一脚产生满支撑。

## 7. 多节腿、落点与地形接缝

### 7.1 物理段链与持久 bend pole

`DeerLeg` 的每个段都保存 `Pos/LastPos/Vel/Radius`；足端是链末段，不另造单粒子 `Limb`。当前
项目段链以 `_idealLength / SegmentCount` 为节长，对拉伸与压缩都做双向距离修正：每 tick 先固定
次数松弛，地形碰撞后再补一次。腿根来自对应躯干节，腿段可碰撞、可被 `SpherePenetration` 从
静态地形推出；腿链普通约束不替代控制器的支撑升力。

这与 DLL 的 `stiff=false` 只抗拉明确不同。项目改成双向约束，是为了防止有真实半径和碰撞的
3D 段在台阶/粗糙地上压缩成团，并给每节调试几何稳定长度；反力仍不通过这条腿链自动回传身体，
身体升力与接近极限时的反拖仍由控制器显式施加。碰撞后的补松弛可能再次把段推入地形，因此公开
tick 结束前还必须重新验证球体无穿透，不能只检查碰撞前的 MTD。

每腿维护一根持久 `BendPole`：

1. 在当前真实 `Anchor→Tip` 轴的法平面内分别构造“本腿向外”和“本腿对向前/向后”两个
   正交基，再按逐腿 `OutwardSplay/ForwardSplay` 合成；不能先混合世界向量再整体投影，否则
   根足弦同时前倾、外撇时，某一侧的纵向符号会被投影翻掉；
2. 出生 pole 与运行期共用上述构造。每 tick 先把旧 pole 在上一支撑 frame 的
   `Forward/Up/Right` 分量重建到新 frame，再投影到当前真实根足轴；
3. pole 是有正负的曲率方向，不再把反半球当作同一条无向轴。踩实/摆动时分别以
   `0.12/0.22 rad/tick` 的确定性上限转向；近 180° 时使用本腿解剖外侧和前后对符号决定绕行，
   不用随机抖动选择关节翻面方向；
4. 主段链仍沿未来候选落点预先展开，并保留足端的专用候选追逐；这是长腿能在斜坡和台阶上
   及时抵达落点的功能路径。主形态使用投影到候选弦的 pole，但不会把公开 `BendPole` 改成
   “未来足端”的口径；
5. 全部距离约束、碰撞和候选落脚结束后，若本 tick 最终仍为 Swinging，只给内段补下一拍的
   解剖弯曲速度。主观“向前/向后凹”的 longitudinal 通道目标按 `sin(PI*t)` 分布，
   误差增益 `0.95`、速度上限 `0.80*HuntSpeed*sin(PI*t)`；再把完整 `BendPole` 去掉
   longitudinal 分量，用较弱的正交通道恢复 3D 外撇，误差增益 `0.14`、速度上限
   `0.10*HuntSpeed*sin(PI*t)`。它不改当前位置、不碰足端或身体；Attached 腿完全不走这条
   通道，因此踩住—身体越过抓点时仍可自然拖后。

原作是二维物理链：`DeerTentacle.Update` 会在移动时直接给中间 TentacleChunk 加移动方向，
`DeerGraphics` 的可见关节又按腿槽使用固定的相反弯折符号；它没有 3D 关节平面、frame 运输或
上述增益/角速度。项目的 pole 和末尾双通道速度因此是明确的 3D 确定性替代，不是从 DLL 抄出的
数值。它只决定多段链的弯折工作平面和形态响应，不伪造抓地或把膝钉到地形。转向、斜坡和
台阶回归必须直接检查 pole 有限、解剖外侧和前后符号不翻、相邻段不高频折返；视觉上画出的
每一段都来自这条真实链。

### 7.2 固定顺序的连续地形采样

核心地形访问只能使用：

```csharp
ITerrainQuery.Raycast(from, to, out TerrainHit hit)
ITerrainQuery.SpherePenetration(center, radius, out pushDir, out depth)
```

3D 没有原作的 `floorAltitude`、Solid 格、九方向 tile path 或随机 Grow。项目用固定有序采样
替换：名义落点、支撑向下、移动方向前探、每腿局部扇形偏移，候选数量和顺序固定；不得遍历
无序集合或用 RNG 打破平局。

候选必须同时满足：

- `TerrainHit.Normal` 有限且非零；
- `dot(normal,WorldUp)` 达到可站立坡角门；
- 足端球心位于命中点沿法线偏移 `FootRadius + clearance` 的位置；
- 球心不与地形重叠，深嵌入用 `SpherePenetration` MTD 恢复，不能把零法线射线当方向；
- 腿根到候选足端的视线没有被另一地形隔断；命中预期 collider 的端点容差不能误判成遮挡；
- 距离在该腿可达范围内。

已踩实抓点保存世界坐标、法线和 collider ID。出现略优候选不会让脚滑走；只有腿进入摆动，
且新候选超过配置的滞回门并连续确认后才替换目标。抓点超距、根到足端被地形阻断、法线变成
不可站立或球心失去有效接触时必须释放。

斜坡和不平整地面以真实命中法线继续同一支撑公式；台阶通过“前探高度 + 各腿独立向下扇形”
逐脚完成。墙只挡路、挤压 body 或使抓点失效，不能成为脚的支撑面。超出最大可站坡角、前方
没有合法落点、台阶高于腿的可达余量时，正确边界是犹豫并停住，而不是悬空胡萝卜、关重力或
转成爬墙。

### 7.3 离地高度与连续休息

控制器从当前身体下方与移动方向前方各做可站立地面 probe，得到当前/前方地板点与法线；
目标高度低通跟随前方地形，不直接读取 body 碰撞接触。只有静止且 `AtMoveTarget=true`，或普通
无输入持续超过 `RestDelayTicks`，才取得休息资格；original/compact 延迟 160 tick，strider
延迟 200 tick。取得资格后 `RestAmount` 每 tick 增 `1/60`，恢复活动时每 tick 减 `1/160`；
同一个连续量同时把目标高度降到 `RestHeightRatio * PreferredBodyHeight`，并按五次方曲线缩短
当前腿 reach。没有 `Walking/Resting` 模式枚举，也不在阈值处切换另一套腿算法。

原作休息资格来自 DeerAI 目的地和 `floorAltitude`，完全休息时 support 因子归零、Tip 不再钉住，
段目标另缩至 10%。本项目没有 AI/tile map，因此用到达信号或确定性无输入延迟替代资格；完全
休息仍保留真实脚支撑，并把当前 reach 收到 hard 上限的 1/3，使独立 3D locomotion 沙盒可以
稳定停驶而不是高重心身体必然塌落。这是明确的 3D/宿主边界偏离，不声称与原作数值相同。

probe 暂时打空时保持有限历史并让犹豫增长，但历史必须有固定失效预算；不能永久沿旧地板高度
跨越悬崖。前方坡面法线必须先通过可站立门，墙的“高度”不进入目标体高。

## 8. 支撑、推进、换步和姿态的 3D 契约

### 8.1 重力始终开启

鹿的 `Body.GravityScale` 在正常、休息、失抓和恢复期间恒为 1。单腿支撑由根到足端
相对 `-SupportNormal` 的直立度、已确认抓点和支撑平面展开 bonus 共同决定。原作在没有对侧
展开时用 `0.8` 直立度作二维硬门；3D 的同一条腿同时含前后/左右分量，低姿态恢复若照搬会出现
“四脚真实落地但支撑恰为零”的死锁，因此项目把无展开起点改为 `0.20`，仍保持直立度单调和
对侧展开增强。四腿总量低通后再乘 `SupportLiftGain=1.75` 映射到 `[0,1]` 重力补偿；零支撑
严格为零，标准三到四脚站姿则足以抵消恒开重力。最后按
`DeerBodySegmentParams.SupportWeight` 归一化分配有上限的向上速度注入；尾侧权重可以低于
中段，但增加躯干节数不能线性增加总升力。

原作把 body 的侧墙/地板 `ContactPoint` 人为混入 support；本项目不沿用。连续碰撞已经由
`Body.Tick`/MTD 负责，墙撞体不能伪装成腿支撑，否则鹿会靠墙浮起。只有通过腿抓点可站立性
验证的贡献进入 `TotalSupport`。

推进先投影到支撑切平面，再由抓地腿比例与总支撑共同缩放，最后按躯干 `DriveWeight` 归一化
分配。每个 chunk 的推进 headroom 为
`MaxMoveSpeed - dot(reject(Vel, worldUp), effectiveMove)`：已有 world-up 速度不参与度量，
但 clamp 会同时缩放 drive 的水平与沿坡竖直分量；支撑/高度伺服、重力和碰撞则独立施加。
因此它不是最终三维速度硬上限。前段权重大于尾段，保留原作前重后轻；失去支撑时仍可有
很小的惯性，但不能凭 MoveDir 在空中持续加速。

### 8.2 统一换步调度与同对守卫

四腿先在同一 tick 更新根部、验证旧抓点和计算释放评分，再由控制器统一发放最多一个主动
松脚许可，避免数组顺序让早更新的腿抢占支撑。评分至少包含：

- 当前抓点到理想落点的距离；
- 相对移动方向落后程度；
- 已踩实时长；
- 当前支撑贡献的惩罚项。

主动松脚必须同时满足踩实腿数门、最低总支撑、同对另一腿已踩实和冷却门。同一对的两腿不能
被主动调度同时腾空；当前 DLL 的后腿错误映射明确修正为 `frontLeft<->frontRight`、
`rearLeft<->rearRight`。若地形遮挡或超距证明旧抓点物理无效，安全释放优先于伪造抓地；在存在
合法可达落点的标准课程上，专项断言要求同对双悬空计数为 0。

正常评分仍只在四脚踩实时松一脚。3D 台阶的根—足视线可能在两条同对腿接近边缘时一起失效，
因此项目新增确定性的两落脚窗口前视：用当前根速度和移动意图预测至多 40 tick 的可达/遮挡，
把有风险的腿提前列为高分候选；三脚踩实时只允许这类 reach-guard 预换步，仍无条件受
`MinimumRawSupportForRelease`、`MinimumPlantedLegs` 与同对真实支点预测约束。若离散台阶边缘
仍让同对旧支点同 tick 物理失效，紧急通道还必须看到该对至少一条腿的
`ConfirmedGripInvalidatedThisTick=true`：出生、Launch 后或普通找地时的“本来就没抓点”不具备紧急资格。
此时也只允许其中一条把“冷却已结束、已存在、足端已接近、再次通过可达/遮挡/穿透复验，
且 `CandidateConfirmCounter >= GripConfirmTicks - 1`”的候选省去**最后一个**确认 tick；刚出现的
单帧候选不能被直接升级。该通道不搜索新点、不跳过冷却、不造假抓点。
这两条是项目相对原作四脚门的 3D 偏离，目的是同时满足物理失效必须松脚和标准课程同对不双悬空。

原作 `0.9*maxLength` 的死写入在本项目补成有效但有上限的反拖：腿长超过
`BodyDragStartRatio` 后，沿锚点到抓点方向对锚定躯干施加连续拉力；达到
`ReachReleaseRatio` 或被遮挡时释放。基础参数可在 0.9 之前开始柔和介入，给连续 3D 碰撞与
固定迭代约束留出 headroom；这必须在预设表中明示，不能声称原作的 0.9 标记本来就有效。

### 8.3 犹豫不施反向推进

3D 的“前方有脚”定义为：已踩实足端相对身体中心在支撑平面上的偏移，与有效移动切向的 dot
为正。没有前方脚时，`Hesitation` 连续上升，推进在原方向上平滑降到一个非负最小比例；前方脚
恢复后连续下降。项目不复制原作 `-0.5` 的反向推力，因为任意 3D 转向和台阶边缘上，突然倒车
会制造与宿主路径相反的冲量和左右摆振。保留的可观察语义是“重心不会继续全力压向尚未探到
落点的一侧”，不是原作恰好使用的二维反向数值。

### 8.4 防折、横滚与整体倾覆

相邻大重叠距离连接之外，鹿按 `PostureWeight` 对隔节跨度施加对称防折力；它只在折叠侵入超过
参数门时连续增强，不把躯干硬拉成直杆。头相对第一躯干节以
`normalize(0.85*Up + 0.60*Forward)` 为目标，因此位于躯干前上方；鹿角中心目标为
`Head + normalize(Up + 0.18*Forward) * antlerLink`，独立伺服增益为 `0.55`。`antlerLink` 已含
头角表面重叠量；鹿角可以与头重叠，但不应侵入任何躯干节。大轻鹿角不能成为支撑力来源。

`BodyChunk` 都是球，躯干又只有一条前后中心线，因此内核没有可观测的“绕 Forward 自转角”；
若从持久 frame 自己的 Up 低通误差虚构 roll，只会得到与身体物理无关的指标。本项目实际限制
的是**整体侧向倾覆**：以包含鹿角质量在内的质量加权 COM、真实足端凸包和左右支点边界计算
`BalanceOffset`、有符号 `SupportMargin`、左右半宽与 `LeanDegrees`。只有 COM 横向越出左右
支点边界且 `LeanDegrees > MaxLeanDegrees` 时，才沿支撑面的 Right 方向给头/躯干连续回中速度，
并按 COM 横向速度阻尼；鹿角不直接吃该力，零支撑、单侧支撑或退化支撑线也不伪造恢复。
plant-and-trail 本来要求身体沿 Forward 越过旧脚后再换步，所以凸包的前后越界只作为公开诊断，
不能被 balance recovery 当成反向刹车；前后 pitch 由姿态力、腿长拖拽和预换步约束。这个机制
改变下一次 `Body.Tick` 的真实位置轨迹，不是隐藏朝向或渲染姿态；不硬改位置、不瞬时翻正、
不关闭重力。击飞先释放腿，balance recovery 因缺少有效支撑面积自然停止；落地后仍走同一循环。

## 9. 宿主输入、输出与生命周期

鹿与既有移动型并列后端保持同名同义：

```csharp
Vector3 MoveDir
float RunSpeed
Vector3? MoveTarget
float MoveTargetArriveRadius
bool AtMoveTarget

void Tick(in TickContext ctx)
void Shift(Vector3 delta)
void Teleport(Vector3 delta)
void Launch(Vector3 velocityPerTick)
```

- `MoveDir` 是 3D 移动意图方向，`RunSpeed` 是强度；控制器只使用它在合法支撑平面的投影。
- `MoveTarget` 是宿主射线/导航投影后直喂的**邻近可达地形表面点**，不是内核寻路请求，
  也不是已抬到鹿身中心高度的点。存在目标时，控制器沿重力反方向 `WorldUp` 把它抬高
  `CurrentRideHeight`，再从 `BodyCenter` 导出临时 MoveDir 并做 3D 到达判定；进入
  `MoveTargetArriveRadius` 后报告 `AtMoveTarget=true` 并停止推进。换点、取消或 Teleport
  必须重算/清除到达态，不能保留上一目标导出的方向。
- AI、路径评分、下一路点、房间切换和目标可达性由宿主负责。控制器不得越过当前点继续扫描。

建议的只读观察面包括 `Body/Head/Trunk/Antler/Legs`、`Forward/Up/Right`、`SupportNormal`、
`RawSupport/TotalSupport`、抓地腿数、`Hesitation`、当前/目标离地高度、`BalanceOffset` /
`SupportMargin` / `SupportHalfWidth` / `LeanDegrees`、每腿抓点/候选/支撑/冷却/bend pole，
以及最后一次移动目标种类与位置。它们供 AI、沙盒和哈希读取，不暴露可绕过固定序的分步写入口。

两个诊断量不能当成更强的物理契约：`DeerLeg.MaxConstraintError` 是最终 MTD 后相对本 tick
目标段长的最大链边偏差，固定端点、缓变形态长度和无穿透优先都可能形成瞬态值，不宜直接设置
很紧的 solver-only 门；`MaxPairAirborneRun` 是实例自出生以来的历史高水位，Teleport/Launch
只清当前 `PairAirborneTicks` 而保留它，且 Launch 弹道会自然计入高水位。判断当前同对状态应读
`PairAirborneTicks` 或各腿 `AttachedAtTip`。

生命周期语义：

- `Shift(delta)`：世界和地形一起平移。同步平移所有 body 位置/插值历史、腿段、抓点、候选点、
  地板采样和 MoveTarget；保留速度、抓地、冷却、支撑低通、步态年龄、bend pole 方向与到达态。
- `Teleport(delta)`：地形不动的瞬移。先 Shift，再释放四腿，清抓点/候选/地板历史/支撑/
  犹豫/姿态恢复位置记忆，并把 `IdleTicks/RestAmount` 原子清零、当前 reach 恢复为 1、
  `DesiredBodyHeight/CurrentRideHeight` 重置为活动站高；同时清 MoveTarget 与 AtMoveTarget，宿主从
  新位置重新喂点。不得保留旧地形 collider 的抓点、旧房间高度或深休息的短腿工作区。
- `Launch(velocityPerTick)`：所有 body chunk 加同一速度增量，四腿强制释放，支撑立即归零且
  重力仍为 1；同样清 `IdleTicks/RestAmount/Hesitation`、恢复完整 reach 与活动目标站高，避免
  深休息后以 1/3 腿长完成整段弹道恢复。不篡改宿主冲量，并保留发射瞬间连续的
  `CurrentRideHeight`；MoveTarget 仍保留，AtMoveTarget 立即作废并在下一 tick 重算。落地后只靠
  正常找地、换步和高度循环恢复。

全部可演化状态必须按固定顺序进入 Deer 自己的 `FoldDeterministicState`；Body 的位置/速度仍由
宿主公共哈希器先折叠，Deer 再补折 Body 的可变摩擦、约束、Skin、卡链阈值与碰撞后恢复开关。
腿段历史、抓点、候选、cooldown、支撑、frame、pole、休息高度、犹豫、MoveTarget、到达态和
累计同对悬空观测都不能漏；专项 hash-fork 目前逐项验证 18 个未来状态分叉。

## 10. 原作到本项目的明确差异表

| 主题 | 当前 DLL | 本项目 | 原因 |
|---|---|---|---|
| 后腿配对 | IL 实际为 `2->2, 3->1` | 明确修正为后左↔后右 | 原作索引缺陷会破坏同对保护 |
| 极限拖拽 | `0.9L` 只写无人读取的 `heldBackByLeg` | `BodyDragStartRatio..ReachReleaseRatio` 内施加有上限拉力 | 补全可观察意图，并给超距释放连续前兆 |
| 犹豫 | 立即变 `-0.5`，20..150 tick 恢复到 1 | 只把正向推进平滑降到非负下限 | 避免 3D 任意转向/台阶边缘突然倒车 |
| 外撇 | 四个固定二维角同时编码前后与画面侧 | 每腿在持久 support frame 内有独立前后和解剖外侧偏好 | 3D 必须显式区分左右与支撑面 |
| 腿关节面与摆动弓向 | 二维段链的中间 chunk 接受移动方向速度；graphics 再按腿槽使用固定相反弯折符号 | 每腿在真实 Root→Tip 法平面内用正交 longitudinal/outward 基构造有向 bend pole，旧 frame 分量连续运输；约束后仅给 Swinging 内段补有上限的纵向主通道和较弱正交通道，Attached 不介入 | 2D 没有 3D 关节翻面问题；项目必须同时避免左右投影反号、180° 换向时记住旧世界半球，以及足端前摆而内段长期反凹。项目角速度与 `0.95/0.80` 纵向、`0.14/0.10` 正交增益/上限均为 3D 确定性取舍，不是 DLL 数值 |
| 腿段距离 | `stiff=false`，只抗拉、不抗压 | 对拉伸和压缩都做固定次数的双向距离修正 | 防止有真实半径的 3D 段在粗糙地形中压缩成团；必须以碰撞后无穿透回归约束副作用 |
| 腿最大长度 | 构造初长为 MMF `300px`，运行时 `maxLength=20*preferredHeight*4/3`，随休息量在 `133.33..400px` 变化 | hard 最大值为 `10/6.8/12.5m`，初始理想链长为 `7.5/5.1/9.375m`；当前 reach 按 `MaxLength*Lerp(1/3,1,(1-RestAmount)^5)` 连续变化，旧远脚逐条换到新工作区 | 修正把 300px 误当最大长度的旧移植；保留原作动态可达曲线，同时用 hard 上限和同对互锁防止缩限同 tick 双失效 |
| 地形 | Solid 格、九方向 tile ray、tile path、floorAltitude | 固定顺序 Raycast + SpherePenetration，连续 collider 法线与前探高度 | 内核只有既有地形接缝，不重造格子地图 |
| 随机 | flipDir、Tentacle Grow、limp gravity、unstuck 等使用 RNG | 固定候选顺序、腿索引错相、确定性退化回退 | 双跑与 40/400Hz 必须逐位一致 |
| body 接触补支撑 | 侧墙/下方 ContactPoint 会把 support 拉向 1 | body 碰撞不计腿支撑，墙只碰撞/遮挡 | 防止鹿靠墙获得假升力 |
| 可站立面 | 只接受上方开放实体格；无墙抓附 | 接受法线门内的连续地板/斜坡，拒绝墙和过陡坡 | 推广地面语义，不把鹿变成爬墙物种 |
| 支撑展开 | 以 body2.x 两侧和 0..400px 曲线增强 | 在支撑平面按相反半平面与真实足间距增强 | 去除二维屏幕 X 假设，保留宽站更稳 |
| 无展开直立门 | 单腿直立度低于 `0.8` 时支撑为 0 | 3D 无展开起点 `0.20`，仍由直立度单调增长且展开后显著增强 | 避免大半径躯干低姿态恢复时四条真实落地腿形成零支撑死锁；Godot original 旧值 `0.35` 可稳定复现该红灯 |
| 预换步窗口 | 只有四脚抓地时主动释放，二维 tile path 负责地形前视 | 正常释放仍需四脚；可达/遮挡风险可在三脚时预换步，并保留弱支撑硬门、同对预测与已验证候选紧急确认 | 连续 3D 台阶会让同对旧抓点同时失效；提前一个完整落脚窗口才能同时满足失效释放与同对不双悬空 |
| 翻滚 | 2D 无 roll/twist | 不虚构球链轴向 roll；以质量加权 COM、真实支点横向边界和体高定义 `LeanDegrees`，越界后施连续 balance recovery | 给 3D 高腿增加可观测倾覆边界，同时不把渲染 frame 当物理状态，也不刹停正常前后换步 |
| 休息来源 | AI 目的地、floorAltitude 决定 resting；完全休息时 support 归零、Tip 停止钉住，段目标缩至 10% | `AtMoveTarget` 或无输入延迟取得资格；体高和当前 reach 连续降低，但脚仍逐条重落并提供支撑 | AI/tile map 属宿主；独立 3D locomotion 停驶不能让高重心身体必然塌落，且仍不增加显式模式 |
| 头/鹿角姿态 | 多组二维 WeightedPush，合成鹿角轴约为 Up + 0.17756 Forward | 头轴 `normalize(0.85Up+0.60Forward)`；鹿角轴 `normalize(Up+0.18Forward)`，鹿角独立伺服且躯干侵入比例门为 0.10 | 3D 必须显式决定“头顶”和前向；0.10 只容纳贴墙连续碰撞的轻微压缩，不能回归为躯干内的大球 |
| 身体曲线 | chunk1 最粗重并向后递减 | `deer/original` 逐值换算该曲线；compact/strider 只做统一出生缩放；仅 `new DeerParams()` 自定义模板采用中段峰值 | 稳定 original 作为取证基线，同时保留可编辑模板 |
| 绝对尺度 | 身体按 px；MMF 构造腿长 `300px`，运行时腿长动态变化 | original 身体按 `1px=0.025m` 换算；正式变体另做显式缩放，3D 地板探测和控制增益重新调教 | 保留可核实形态比例，但不把 AI tile 高度与二维运动增益机械换算 |
| 升力分布 | 头与前三节躯干的权重约 `1.3000/1.5427/2.0373/2.4619`，尾节无升力 | original 四躯干 `SupportWeight=1.55/2.15/1.75/0.55`，归一化后分配总支撑注速 | 3D 中把升力集中在四个腿根相关粗躯干节并给尾节少量稳定量；不是 DLL 逐值换算 |
| 推进分布 | 头→尾的绝对权重为 `0.35/0.28/0.21/0.14/0.07` | original 的头+四躯干 `DriveWeight=1.0/0.8/0.6/0.4/0.2`，按正权重平均归一化后再乘 `BaseDrive=0.024m/tick`，并以 `MaxMoveSpeed=0.095m/tick` 作为推进 headroom 阈值 | 保留 DLL 的 `5:4:3:2:1` 前重后轻比例，把绝对推进强度抽成可按预设调教的米制全局增益，不机械换算 px/tick |
| 躯干姿态/防折 | 积分前对躯干1↔3、1↔4 各施 `0.35`；积分后对 `n↔n+2` 恒施 `0.45`，另有头/鹿角姿态常力 | original 躯干 `PostureWeight=1.10/1.35/1.20/0.85`、`PostureStrength=0.12`；隔节距离只在低于两段静息长之和的 `0.58` 时，按侵入量乘 `AntiFoldStrength=0.22` 质量加权撑开 | 3D 粗重叠链只在真折叠时需要防折；距离门+连续增强避免恒定推力把躯干变成硬杆或在稳态注入振荡 |
| 碰撞回弹 | `airFriction=0.999`、`surfaceFriction=0.4`、`bounce=0.1` | 保留 `AirFriction=0.999` 与 `SurfaceFriction=0.4`；共享 `SphereTerrain` 法向内速清零，无 restitution（等价 `bounce=0`） | 遵守本轮共享层零改动，并避免高重心多节腿在连续 MTD 接触中被单物种反弹反复激起；该偏离属宿主共享碰撞语义，不是 Deer 隐藏参数 |
| 腿段数量 | 每腿 6 个 TentacleChunk | original/compact 为每腿 6 段，strider 为每腿 8 段 | 保持取证基线，同时让长腿变体有足够弯折分辨率；不得退化为单粒子 |
| 载人/进食/跪下 | Deer/AI/PlayerInAntlers 含这些 gameplay | 不实现 | 属宿主玩法，不是 locomotion 核心 |

预设之间只能改出生参数，不能开启隐藏模式。每个稳定预设都必须分别验证：身体重叠拓扑、腿段
数与可达性、满支撑稳态、完整步幅、动静高度差、防折与侧倾边界。若某预设相对 DLL 改了
躯干曲线、腿段数、腿长或拖拽阈值，最终参数表和沙盒 HUD 必须能直接看见，不用名称暗示行为。

## 11. 固定序与验证范围

控制器每个 40Hz 核心 tick 的公开入口只有一次 `Tick(TickContext)`。实现固定序必须稳定为：

1. 解析 MoveTarget/MoveDir/RunSpeed，得到 WorldUp 与有效支撑切向；
2. 更新休息资格、连续体高与各腿当前 reach，再更新持久 frame 与当前/前方地板 probe；按腿索引在身体受力前复验上一 tick
   的抓点（不推进冷却/候选/段链），纯测量本 tick 仍合法的支撑快照；
3. 受力用 `min(上一 tick 低通支撑, 当前合法目标)`，因此新支撑按低通建立，消失/遮挡支撑则
   立即从拖拽、升力和推进中剔除；随后注入 COM balance recovery、防折与姿态力；
4. `Body.Tick`，重力倍率固定为 1；
5. 按固定腿索引推进多节链、候选、碰撞、抓地和 bend pole，并以新腿根再次复验超距/遮挡；
   若同对至少一个**上 tick 已确认的支点**在本 tick 被超距/遮挡/接触复验判为物理失效，
   才可对一个已完成全部安全复验且确认计数只差最后一 tick 的现有候选做紧急落脚；
6. 纯测量最终四腿支撑，统一评分并至多主动松一腿；若发生释放只重新纯测量，不重复低通，
   然后恰好一次提交 `RawSupport/TotalSupport/SupportNormal` 并更新犹豫；
7. 更新同对双悬空与顶死指标，清除由 MoveTarget 临时导出的 MoveDir，并折叠确定性状态。

宿主不得拆开或重排这些阶段。渲染只对 `LastPos -> Pos` 插值，不向核心回写。

### 11.1 无引擎 smoke 必须真断言

专项 smoke 以退出码判定，覆盖：

- 所有正式预设的参数深快照、拓扑、头角 RotationChunk、躯干重叠和四腿两对；
- 同参数进程内双跑逐位一致，40Hz 与 400Hz 宿主子步终态一致，微小输入扰动改变哈希；
- `GravityScale` 全程为 1，支撑稳态高度与速度有限，失去腿后不能悬空；
- 行进 BodyCenter 高度为腿 hard 长度的 `0.45..0.90`，头/躯干最低球底净空至少为 `0.25L`；
  全休息净空至少 `0.15L`，当前 reach 收到约 `L/3`，稳态实际根足距不越过该上限；
- 出生与运行中头位于首躯干前上方，鹿角 up-dot 至少 `0.85`，正常/斜坡/贴墙/休息与 Launch
  恢复后鹿角都不显著侵入躯干；
- 单腿直立度、相反半平面展开 bonus 和总支撑沿躯干的不均匀分配；
- 正常可达课程及休息收腿全过程中同对双悬空为 0、每腿完成完整踩住—抬腿—落地周期、
  冷却生效；普通停车在 `RestDelayTicks` 前不得产生休息主动释放，取得资格后必须实际逐腿
  释放并重落到缩短后的工作区；
- Original / Compact / Strider 都先沿一个方向建立稳定步态，再做精确 180° 反转：Attached 只检查
  pole 的解剖 longitudinal/outward 与连续性，不禁止 plant-and-trail 自然拖后；Swinging
  必须逐腿检查真实中段弓向、bow·pole、内段前摆、落脚净进度和最长连续错向，不能只看脚尖；
- 另以整条腿和局部 frame 的精确 180° 刚体旋转直接验证 pole 分量运输；关闭 bend
  时四腿 alignment 从 `+1` 精确变为 `-1`，因此红灯不是只由后续段链形态补偿偶然触发；
- 释放评分能选择落后/偏离腿，同时保护高支撑腿；
- 前方无脚时犹豫增长且推进不反向，前脚恢复后犹豫消退；
- 候选略优不重定向，显著改善并连续确认才换点；
- 同对紧急落脚必须有本 tick 已确认抓点物理失效证据；Original/Compact 的全新候选都不能
  绕过滞回，只有跨 tick 保留且确认计数只差最后一拍的候选可以落脚；
- 超距、根足遮挡、不可站立法线会失抓，接近极限的有效拖拽确实降低躯干继续前冲；
- 行进高度高于静止休息高度，前方地形高度变化平滑而非单 tick 跳变；
- 平地、解析斜坡和解析台阶通过；Godot 矩阵另断言粗糙错高面、墙前停住和 90° 转向；
- Shift 全状态连续、Teleport 作废旧地形状态、Launch 保留注入冲量并能重新落地；
- 深休息到 `RestAmount≈1`、reach≈`1/3` 后，Teleport/Launch 必须即时唤醒并在无移动输入下
  以完整 reach、活动站高、两对真实落地和有限支撑恢复；该组合不得复用只看抓地数的宽松 Recover；
- Launch 前若非空 MoveTarget 已到达，调用后必须保留目标与连续 CurrentRideHeight、立即作废
  AtMoveTarget，并在后续每 tick 按受力前 BodyCenter 几何重新计算真假；
- 预热后的完整 Deer tick 热路径不得产生托管堆分配；当前门直接测 256 tick 的线程分配字节；
- MoveTarget 到达、换点、取消、Shift 和 Teleport 契约完整。

验证门本身也要做消融：命令行分别关闭 support、同对互锁、犹豫、主动换步、balance
recovery、站姿高度、鹿角姿态或有向 bend/摆动弓向时，对应八个专项进程必须以退出码 1 和
`EXPECTED-FAIL` 变红。
展开 bonus、候选滞回、
超距/遮挡失抓、可及极限拖身和生命周期由同一 smoke 的直接机制夹具做正反对照，不能只打印指标。

### 11.2 Godot 矩阵

最终版 `./tools/run_deer_matrix.sh /private/tmp/proc_anim_deer_bend_matrix_final_all_presets` 实跑 **18/18** 个
Godot 进程配置通过；每项均以场景退出码和 `[DEER-RESULT] PASS` 双判定。双跑与 40/400Hz
逐条 `[DEER-DET]` 相同，1mm 微扰改变哈希，三个正式预设哈希互异；三预设另各自通过同一个
180° reverse 场景；随后无引擎 smoke 和
8 个 `--ablate=support|pair|hesitation|release|balance|stance|antler|bend` 失效注入分别以预期
退出码 1 变红。

| 配置 | 最终终态哈希 |
|---|---|
| original-a / original-b / original-40 | `B02B1A5648208F02` |
| perturb | `FABFED3F0BB0F3DF` |
| compact | `C817EC40407B66FB` |
| strider | `B5F40B8AA66CFFA1` |
| slope | `40D599ED04216720` |
| steps | `7C1FFF9B6792FE98` |
| wall | `079C1C8DD02EE82A` |
| turn | `E5883FBAF6C5CC69` |
| reverse-original | `D4FA378502BD7394` |
| reverse-compact | `50035DB70AD14A2F` |
| reverse-strider | `D6494BFCC50770DE` |
| rough | `F82576BDBF79DA85` |
| rest | `6166233D7FBCE179` |
| launch | `2F0C8FA0609676B6` |
| target | `95847339455ED62A` |
| lifecycle | `90C2A98FA2211208` |

无引擎固定哈希为 `80249FD24361B9C8`，40/400Hz 相同；微扰为 `B33E51B04CAF9D99`。
评审后新增的稳态分配门实测 256 tick 为 `0B`；深休息夹具从 `rest=1/reach=0.333` 调用
Teleport 与 Launch 后均即时回到活动参数，并分别在 37 tick 内连续恢复到活动站高和满支撑。
同一 smoke 的平地 900 tick 为 `88.512m`、146 次落脚、每腿至少 36 次、同对双悬空 0；compact
行进/休息 BodyCenter 为 `4.335/1.916m`，球底净空为 `3.501/1.051m`，全休息当前 reach
`2.267m`、实际最大根足距 `2.063m`。解析斜坡/台阶分别前进 `82.85/124.79m`。

Godot original 平地为 `70.132m`、42 次落脚、每腿至少 7 次、平均支撑 `0.963`；活动
BodyCenter `6.020m`，最低球底净空 `4.822m`，鹿角 up-dot 最小 `0.961`、躯干侵入为 0。
rest 从 `6.020m` 连续降至 `2.666m`，当前 reach 为 `3.333m`、稳态实际根足距最大
`2.861m`、球底净空 `1.607m`；休息资格在 idle 161（延迟 160）首次出现，延迟前主动释放 0，
之后四条腿各完成一次 release→replant，同对双悬空 0、终态两对均有真实落地腿。wall 连续
接触 639 tick 后终速为 0、墙面抓点 0，贴墙时鹿角
最大躯干侵入比例 `0.085`；turn 的物理身体轴转过 `90.0°` 且转后每腿至少 4 次落地；rough
前进 `32.638m`、平均高度误差 `0.050m`、抓点高差 `1.200m`、最大残余穿透 `0.00048m`。
三个 reverse 的物理身体轴均完成 180° 换向，同对双悬空均为 0。Original 四腿稳定摆动期
纵向弓度/匹配率依次为 `+0.34m/0.94`、`+0.35m/0.96`、`-0.34m/1.00`、`-0.33m/1.00`，
最长连续错向为 `3/2/0/0 tick`，最慢腿 113 tick 恢复。Compact / Strider 最慢分别为
128 / 72 tick；两者四腿的完整 3D `bow·BendPole` 平均值分别为
`0.42/0.35/0.98/0.78` 和 `0.98/0.91/0.91/0.95`，全部通过符号与匹配率门。
上述数值均来自最终冻结脚本输出，不是预写目标。

最终补丁已重跑全部既有 smoke 与 Godot 矩阵：主矩阵 45 项、Spider 16 项、Cicada 9 项、
TentaclePlant 17 项均 GREEN，原脚本的路点/行为硬门不变。既有无引擎固定哈希仍为 Lizard
`AAA0E4963668E5DC`、Humanoid `900982675F381F26`、Vulture `1B9F0B2CCAA0FC10`、
Centipede short/long `655A21496C00E86A` / `59CBCF993DF8ACD8`、Spider small/large
`F78323EEB0882985` / `F7AB5619F6E6928F`、Cicada `C56791A6588031FC`、TentaclePlant
`025601D1B6ADC65A`；旧断言源码未改。主 smoke 的旧物种数量下限门与源码本轮也未修改；
它的当前目录遍历仍会扫到 Deer 并检查跨物种边，但该旧下限门不证明 Deer 必然存在。
Deer 的三个稳定 ID、独立参数快照、未知 ID 快速失败和工厂装配由 `core/deer_smoke`
的 `[DEER-CORE-FACTORY]` 真断言单独负责。没有更新任何旧物种基线来掩盖漂移。

## 12. 非目标

- AI、寻路、房间迁移和下一路点选择；
- 攻击、伤害、进食、叫声、跪下、载人和鹿角攀附；
- 游泳、水中运动和水下支撑；
- 动态物体扫描、动态平台所有权和 gameplay 碰撞效果；
- 墙面/天花板抓附或蜥蜴式重力开关；
- 正式模型、材质、毛发、鹿角造型和表现动画；
- 为统一物种而新增万能控制器接口，或修改共享/其他物种代码。
