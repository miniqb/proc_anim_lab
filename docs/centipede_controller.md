# 并列蜈蚣控制器

`CentipedeLocomotionController` 是与 `LizardLocomotionController` 并列的物种专属后端。
二者只共享 `Body`、`BodyChunk`、`ChunkConnection`、`ITerrainQuery`、固定 tick 与哈希底座；
核心层没有为了统一两类生物而增加万能控制器接口。Godot 白盒沙盒用宿主侧
`ISandboxCreatureAdapter` 统一输入、渲染、拖拽与调试。

## 1. 出生配置与装配

- `CentipedeParams`：稳定 ID、默认体型曲线，以及全局速度、约束迭代、贴面、轨迹、行波和自避参数。
- `CentipedeSegmentParams`：单节的半径、质量、下一节距离、弯曲刚度、驱动/贴面权重、
  腿对数、腿长、脚半径、抓地速度/响应/延迟、步幅与侧向距离。
- `CentipedeSegmentOverride`：按节索引稀疏覆写上述字段；同一节多次覆写时后者胜出。
- `CentipedeFactory`：解析出生快照并装配身体、腿与控制器。配置只在出生时读取；
  改尺寸或拓扑必须重新装配。

最少 2 节，没有编译期节数上限。相邻节使用质量加权的 `Rigid` 连接，质量差会真实改变
两端位置修正比例；隔一节使用 `SoftOnly + PushOnly` 防折叠支柱。当前预设：

| 稳定 ID | 节数 | 设计特征 |
|---|---:|---|
| `centipede/short` | 5 | 两端细、中段粗，短节距、快速紧凑步态 |
| `centipede/long` | 18 | 慢速长波步态，用于验证长链完整过角与尾端跟随 |
| `centipede/armored` | 10 | 中段高质量、高刚度，中央两节各有第二腿对 |
| `centipede/ribbon` | 12 | 细长低刚度、长腿高速，逐节交替腿长与节距 |

未知稳定 ID 会快速失败，不像旧蜥蜴 `ByName` 那样回落默认值。沙盒使用
`--creature=<稳定ID>`，数字键 5–8 按上表顺序选择；原有 `--breed=` 与数字键 1–4 保持不变。

## 2. 表面轨迹与贴面运动

控制器维护一条可从两端延伸的 `CentipedeSurfaceSample` 路径；每个采样含 `Point`、`Normal`、
`ColliderId` 与累计 `ArcLength`。宿主通过 `RequestedLeadEnd` 显式请求 `Start` 或 `End`
领航，新值在下一次 `Tick` 的确定性边界生效；`LeadEnd` 是控制器已经应用的当前状态。切换后
从另一端继续使用既有路径，不重置身体或行波相位。`MoveDir`/`MoveTarget` 只决定移动意图，
不推断或切换领航端；自动选头尾、评分和去抖策略属于宿主或 AI。

路径两端各自保存一根有向表面切线。输入在当前面上的投影有效时会更新它；输入近乎平行于
表面法线（例如沿平台输入 `+X` 后翻过外角下墙）时，控制器会把先前切线平行运输到新表面，
而不是退回世界 `Up/Right` 猜方向。显式换端会从对应路径端重新播种；`Teleport`、`Launch`
或表面状态失效则清除它。这只是既定领航端的表面续行，不会根据输入改变头尾。

领航探测按固定顺序寻找当前面前方、内角前方碰撞面和绕边扇形外角。探测结果还要通过
碰撞体连续性、法线变化、沿切线正向进度、近期路径回访与球体可行位置检查；没有下一表面时
停止延伸，不制造悬空目标，也不接受折回身体内部的发夹路径。法线变化较大时按身体半径插入
经过球体 MTD 可行化的圆滑过渡采样；`ArcLength` 使用实际相邻中心距离。各节再按自身连接
长度沿弧长取目标，所以异构节距仍会依次走过同一个角。

每节通过平行运输维护 `Forward`、`Side`、`SupportNormal` 局部坐标系，并独立执行重力抵消、
法向贴面伺服和路径切向推进。因此同一时刻可以前半在墙上、后半仍在地面。非相邻节只在
确定性空间桶内做有上限的对称排斥，且跳过索引距离不超过 2 的相邻结构，避免穿身同时保持
长体查询接近线性增长。相邻刚性连接参与碰撞后结构恢复，但只撤销碰撞相对约束松弛末新增的
违反；候选仍须通过接触可行锥与 MTD，因此不会用“拉直”重新压进地形。

## 3. 真实抓足与行波

`CentipedeLeg` 的落点来自真实 `TerrainHit`。摆动开始时预查下一抓点；站立期保持世界系
`GripPoint`，抓点超出可达范围时只允许一次立即重搜。脚不会以刚性冲量反拉身体，真实
`Gripping` 结果改为调制所属节的支撑可信度、抗重力、贴面和推进能力。

候选抓点必须从锚点直视到“表面点 + 脚半径”的足球中心，防止侧向探针在薄墙另一侧种脚；
已种下的脚按 4 tick 错峰复核同一视线，停驶在 stance 时也不会永久保留隔墙抓点。若已释放
足端连续被同一类“锚点与目标都在阻挡面另一侧”的碰撞挡住，中心扫掠与低速球壳 MTD 都会
累计；第 4 tick 把脚端穿墙复位到锚点侧，继承锚点速度并清除旧抓点、预抓点和插值历史。
`TerrainBarrierRecoveries` 提供逐腿累计诊断，但不参与运动决策。

步态是确定性行波：相位由 tick 时间、出生时固定的沿身弧长、左右侧和腿对共同决定。
完整地形搜索集中在摆动开始或抓点失效时；已植脚的可见性检查按腿错峰，不让长体的全部脚
每 tick 扫完整探针带。

## 4. 宿主契约

```csharp
CentipedeLocomotionController controller =
    CentipedeFactory.CreateController(origin, CentipedeFactory.Long());

controller.RequestedLeadEnd = CentipedeLeadEnd.Start; // 宿主显式选择领航端
controller.MoveDir = desiredDirection;
controller.RunSpeed = speed01;
controller.MoveTarget = nearbyReachablePoint; // 可选
controller.Tick(new TickContext(gravityPerTick, terrain, tick));
```

移动输入与蜥蜴同构：`MoveDir`、`RunSpeed`、可选 `MoveTarget`；蜈蚣在选择或更换领航端时
另写 `RequestedLeadEnd`，该值保持到下次变更，不必每 tick 重写。`MoveTarget` 仍必须由宿主
提供**邻近且可达**的路径点；控制器不负责 AI 寻路。到点后读取 `AtMoveTarget` 并由宿主换点
或清空。若要根据方向、目标或战术自动换头，宿主先自行评分/去抖，再更新 `RequestedLeadEnd`。

沙盒边界也遵守这个分层：交互模式由用户显式换端，`--lead=start|end` 会锁定对应请求，
二者都不自动切换。只有未传 `--lead` 的无头 default 巡逻脚本提供一个**宿主层示例策略**：
按路线方向为两端评分，连续 3 tick 确认后写 `RequestedLeadEnd`。控制器只收到最终请求，
不知道评分或去抖存在；该示例不是核心契约。

生命周期入口同样是：

- `Shift(delta)`：世界与生物一起平移，完整保留轨迹、抓点和步态连续性。
- `Teleport(delta)`：地形不动的瞬移；清空轨迹、抓握、支撑与旧目标。
- `Launch(velocityPerTick)`：统一给全部身体节加冲量，并作废表面支撑。

宿主可读 `Body`、`Segments`、`Legs`、`SurfaceTrail`、`LeadEnd`、`LeadChunk`、
`SupportedSegmentCount`、`SupportRatio`、`AtMoveTarget`，以及每节的
`SupportPoint`、`SupportNormal`、`Forward`、`Side`、`ColliderId` 和 `SupportConfidence`。
其中 `RequestedLeadEnd` 是宿主请求，`LeadEnd` 是已应用状态；不要用后者替代输入。

## 5. 已验证边界

`dotnet run --project core/smoke` 中的无引擎蜈蚣回归已经覆盖：

- 2/5/18/32 节装配、全部逐节覆写字段、出生快照、质量加权连接与防折叠支柱；
- short/long 进程内双跑 bit-exact，独立基线分别为
  `4DAD09DE3CB81C31`、`4E3DFC052BA4E74D`；
- 地面 → 18° 斜坡 → 内角墙面 → 外角墙顶 → 天花板的解析课程；
- 固定 `Start` 领航端、恒定 `+X` 输入下完整经过 1.6m 平台外角立面并落到低地，检查
  `Wall → LowerFloor` 顺序、尾端预算、下降后续行、路径不回访身体内部及非相邻节不成团；
- 显式 `RequestedLeadEnd` 头尾切换、`MoveTarget` 到达/清除、`Shift`、`Teleport`、`Launch`、
  支撑恢复和确定性自避；
- 薄墙足端恢复：short 中心扫掠、armored 低速大脚球壳 MTD、同侧撞墙不误复位，以及
  停驶 stance 的既有抓点被墙隔断；两条动态恢复与 stance 遮挡均在 4 tick 内完成；
- 全状态有限、穿透不超过 2 mm、终态连接偏差不超过节距 10%、深断链连续不超过
  20 tick、换面不超过 40 tick、尾端通过预算 `40 + 8×节数` tick；
- 16→32 节地形查询量增长不超过 2.25 倍。

既有 Lizard 无引擎基线仍为 `AAA0E4963668E5DC`。

Godot 全矩阵当前共 **32 项 = 旧 20 项 Lizard + 新 12 项 Centipede**，已经全部通过。
新增 12 项包含四预设巡逻、short 双跑/40Hz/微扰、short/long 全向课程、armored 固定头
下阶梯，以及 long 嵌入恢复/擦墙。最终 Godot 哈希为：

| 配置 | 哈希 |
|---|---|
| `centipede/short` | `BE58C639D59E1EA2` |
| `centipede/long` | `0D1D0D51D5E9C26B` |
| `centipede/armored` | `D595C149C1C6B8EC` |
| `centipede/ribbon` | `D834CFF4122082C3` |
| `centipede-course-short` | `D6F99637C6D76EE1` |
| `centipede-course-long` | `30793ACEDD88F34C` |
| `centipede-step-down-armored` | `3D2594F93BC2F009` |
| `centipede-embed-long` | `FE8E2E356129F7A2` |
| `centipede-wallside-long` | `E2837F5747FDFBFF` |

真实 Jolt 课程硬指标：short/long 的 `maxNoneRun=1/9`、`maxBlockedRun=0/0`、
`maxConnectionRun=4/7`，最大尾端滞后/预算为 `15/80`、`89/184` tick，穿透均为 `0m`。
固定头下阶梯中领/尾端在 tick `46/116` 落地，滞后 `70/120`，净前进 `3.387m`，
终态非相邻间距为半径和 `1.917×`，严重成团连续 `0` tick。

## 6. 默认非目标

本控制器不包含 AI 寻路、战斗、水中/游泳运动或正式美术。反编译源码只用于算法研究与
互操作理解，不复制或提交。
