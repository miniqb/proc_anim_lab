# 长腿爸爸（DaddyLongLegs）3D 控制器

`DaddyLongLegsLocomotionController` 是与 `LizardLocomotionController`、
`SpiderLocomotionController`、`CentipedeLocomotionController` 等平行的独立物种后端。
它没有头、髋、脊柱或 forward；身体是出生时生成的完整连接球团，运动附肢是物种自有的
多段贴面触手。它不继承任何既有控制器，也不读取其它物种的参数表。

本轮只实现核心物理、确定性出生形态、宿主接口、白盒沙盒与专项回归。眼睛、材质、表面网格
和正式外形不在范围内。

## 1. 当前 DLL 的直接取证边界

真相源是本机用户自有的 Rain World 程序集：

- DLL：`/Users/miniz/workspace/others/Managed_extracted/Assembly-CSharp.dll`；
- SHA-256：`b6be1d4e18ce219d21091b51564cb6a11c1e4106b41de903eb8e58849cb16fdb`；
- 反编译器：`ilspycmd 10.1.1.8388 --disable-updatecheck`；
- 直接读取类型：`DaddyLongLegs`、`DaddyTentacle`、独立基类 `Tentacle`；
- 辅助核实类型：`DaddyGraphics`、`PhysicalObject.BodyChunkConnection`、`BodyChunk`、
  `RWCustom.Custom`。

反编译源码只留在仓库外用于互操作研究；本仓库只记录结构、数值、3D 取舍和自有实现，
不提交、复制或分发原作源码。下文“原作”均指上述 SHA 对应的当前 DLL，而不是分类文档或印象。

### 1.1 出生身体

构造函数先保存 `UnityEngine.Random.state`，再以 `abstractCreature.ID.RandomSeed` 初始化随机状态，
完成形态生成后恢复旧状态。`SizeClass` 在 Terror、时间线不晚于 Artificer 的所有 Long Legs，
以及其它时间线里的普通 Daddy 上为 true；Brother 因而有时间线分支：不晚于 Artificer 时与
Daddy 一样是 4–7 球、预算 12，较晚时间线才是 4–6 球、预算 8。

对第 `i` 个球，令 `t=i/(count-1)`、`remaining` 为尚未分配的预算：

```text
allocation = Lerp(remaining*0.2,
                  remaining*Lerp(0.3,1,t),
                  Random.value^(1-t))
radius = allocation*3.5 + 3.5 px
mass   = allocation
```

每个球先放在以自身半径为半径的二维圆周上；随后做 5 轮有序两两排斥，使中心距离至少达到半径和
的 `0.85`，每轮末再把全部偏移乘 `0.9`。最终每对球都建立一条 Normal connection，静息距离
冻结为此时两出生点的距离，因此 `B` 个身体球恒有 `B(B-1)/2` 条连接。

### 1.2 出生触手

`SizeClass=true` 的普通 Daddy / 早期 Brother 触手数为 `Random.Range(5,13)`，即 5–12；
较晚时间线的 Brother 为 5–9。总长度同样按 `SizeClass` 分支：

```text
SizeClass:  Lerp(3000 px, tentacleCount*400 px, 0.5)
small Bro:  Lerp(1600 px, tentacleCount*300 px, 0.5)
```

长度先均分，再做 `5*tentacleCount` 次转移：从随机 donor 取自身长度的
`Random.value*0.3`，只在 donor 仍超过 `100 px` 时交给随机 recipient。触手按
`index % bodyChunkCount` 轮流锚定，二维偏好是 `index/tentacleCount` 绕圆一周。

`DaddyTentacle : Tentacle`，不是 `BodyPart` 或 `Limb`。当前构造参数为：

```text
stiff=false, rope=true, shorten=false,
massDeteriorationPerChunk=0.5, pullAtConnectionChunk=0,
goalAttractionSpeedTip=0, goalAttractionSpeed=0, alignToSegmentSpeed=0,
backtrackSpeed=3.2, chunkVelocityCap=10,
tileTentacleUpdateSpeed=0.25, tileTentacleSnapWithPathDistance=5,
segmentPhaseThroughTerrainFrames=15, tileTentacleRecordFrames=60,
maxPullTilesPerTick=12, terrainHitsBeforePhase=20
```

段数为 `floor(length/40 px)`；启用 MMF 时下限为 3。每段半径 `3 px`。链内所有段对（包括
相邻段）都会调用 `PushChunksApart`，触手以独立 `tChunks[]`、地形路径、回溯与 rope 约束工作。

`PlaceInRoom` 会把触手沿当前理想方向伸到完整 `idealLength`，途中首次遇到实心 tile 才提前
截断；不是只在身体旁生成一小截。普通段更新先统一乘 `.9` 阻尼。只有存在 `grabDest`、且该段到
落点的距离严格小于 `200 px`（本项目单位为严格 `<5m`）时，`StickToTerrain` 才会维持主动贴附
并把该段计入 `chunksGripping`；普通地形碰撞本身不会增加 `chunksGripping`。主动贴附还会在
切向再乘一次 `.9`，所以原作被动碰撞与主动抓握的 tick 末切向保留率约为 `.90/.81`。

原作 `Tentacle` 每 tick 先把 `backtrackFrom` 重置为 `-1`，再按根到梢更新 `tChunks`：第一个无法
看见自己所对应 tile path segment 的 chunk 成为回溯边界。Daddy 的 `Climb` 从该索引起停止
`StickToTerrain` 和朝 grab/guide 的拉动；基类再以 `backtrackSpeed=3.2` 把远端拉向上一个仍可见
的路径格或前一 chunk。`rope=true` 还会为相邻 chunk 保存绕地形后的连接点和实际绳长，所以原作
判断的是“chunk 相对显式 tile path 是否走丢”，不是简单的 Base→grabDest 直线可见性；累计撞地
达到 `terrainHitsBeforePhase=20` 后还允许 phase-through 作为最后退化。

本项目没有 tile path、`Rope` 或穿墙 phase，因此不能逐项照搬。3D 等价物以真实粒子链为真相：
最终约束/自避后补 temporal sweep，最终 residual 后再逐条审计 `Anchor→segment0` 与相邻段链边；
首个阻断索引写入 `BacktrackFrom`，立即把该边断成无张力边并截断其后的 guide servo、adhesion、
抓附和运动支撑。任一阻断边沿链迁移时仍累计为同一条触手的连续阻塞 episode；达到 6 tick 后，
控制器只在整段后缀的球壳、相邻边和链内自避全部通过时才一次性重建并重新接回。它保留原作
“从第一处遮挡起停止远端行为”的可观察语义，但以连续 3D 碰撞拓扑代替 tile path/Rope；为什么
不能改成整条直线 LOS 见 §4。

### 1.3 支撑、推进与职责

每条触手的 `chunksGripping` 按执行 `StickToTerrain` 的主动抓握段逐一增加
`1/segmentCount`，不是尖端布尔值；仅仅因碰撞落在地形上的段不计入。
原作总支撑为：

```text
perTentacle = sqrt(chunksGripping)
if atGrabDest && grabDest exists:
    perTentacle = Lerp(perTentacle, 1, 0.75)
support = sum(perTentacle / tentacleCount)
```

`atGrabDest` 会逐段检查：任一 `tChunk` 到自己的落点小于 `20 px` 即成立，并非只检查末端。
身体运动段再对总支撑取 `support^0.3`。`PlaceInRoom` 把 `unconditionalSupport` 置 1；直接读取
当前 DLL 还可见，静止时它会被持续刷新为 1，恢复移动后才每 tick 减 `.025`，约 40 tick 衰减
完毕。`Act` 取它与真实支撑中的较大值，并把同一值同时用于抗重力和全团推进，因此原作确有一段
可由间歇输入反复刷新的“支撑 + 推进”托底；它不是独立的输入上升沿状态。基础更新已经施加完整
重力，`Act` 随后再加 `-gravity * support * 1.2`，所以满支撑不是“恰好关掉重力”，而是留下
`.2g` 净向上增量。同一连续值还把身体速度从乘 1 平滑到乘 `.95`（受 squeeze 时最低到 `.8`）。

方向推进先统计已到落点的触手中，抓点位于移动方向一侧的比例，再与总支撑相乘并取幂。
这个方向比例不再乘单条触手的 `chunksGripping`；贴面多少已经通过总支撑进入耦合。最后同一
推进速度加到所有身体球，不建立头尾差分。方向映射下限会随卡住量从 `-0.1` 降到 `-1`；最终
耦合指数则在 stuck 100→200 期间从 `0.8` 降到 `0.1`，让退化期较弱的方向支撑也能产生位移。

`neededForLocomotion` 是独立于触手任务的预算标记。若支撑低于
`1 - locomotionTentacleCount/tentacleCount`，原作用近地分数选未征用触手；Grabbing / Hunt /
ExamineSound 仍可被选，只是分数分别乘 `0.01 / 0.1 / 0.6`。总支撑高于 `0.85` 时随机释放一条，
并不保证留下一条空闲。移动中抓稳触手超过总数一半，或卡住计数超过 100 时，
会释放 `ReleaseScore` 最大的一条重新找点；该分数是触手后半段到理想落点的最小距离。

理想落点位于锚点沿“固定偏好与移动方向的球面插值”方向、约 `0.7*idealLength` 处。
卡住时插值逐渐偏向移动方向；搜索失败会扩大搜索范围并加入更多随机偏移。

原作用最近 80 个位置、与 40 tick 之前的历史比较；超过 30 个旧位置仍处于 4 tile 内时
`stuckCounter++`，否则每 tick `-2`，并钳在 0–200。超过 100 后每个身体球加入随机抖动。

### 1.4 单触手打断与非运动任务

命中 appendage 时，原作先按 `SizeClass` 把 damage 除以 `2.2/1.7`、stun bonus 除以
`2.0/0.9`，再以 `int(scaledDamage*48 + scaledStunBonus)` 更新该条 `DaddyTentacle.stun`。
触手 limp 时
额外下坠，并放弃抓住的动态目标；其余触手仍由职责分配器接管。空闲触手还可执行 Hunt、
ExamineSound、Grabbing，真正伤害、猎物选择、吞入和消化属于 gameplay/AI。

当前 DLL 有一个对本项目契约不合适的更新顺序：limp 分支在清空 `atGrabDest` 与
`chunksGripping` 之前提前返回，因而可能暂留上一 tick 的支撑。本项目明确修正为“被打断触手
立即提供 0 支撑”，与本轮宿主要求一致；这是有意差异，不把原作的陈旧缓存当成手感特征。

### 1.5 所有有意差异汇总

| 原作 | 本项目 | 原因 |
|---|---|---|
| 临时改写 Unity 全局 RNG，再恢复 | `seed/domain/index` 的无状态 64-bit hash 采样 | 不触碰全局状态；同 seed 逐位复现，各形态域互不串流 |
| 出生球位于各自半径的 2D 圆周，触手偏好也沿 2D 圆周 | 出生球位于各自半径的 3D 球面，偏好用 seed 相位 Fibonacci sphere | 3D 各向同性，不能偷偷选一个世界平面 |
| 偏好是世界二维方向 | 偏好依附出生球团的三 landmark 材料 frame | 无 forward 仍能让触手长在固定身体区域，并随真实身体转动 |
| 完整图为 Normal connection，`elasticity=1`、`weightSymmetry=-1`；构造时 `-1` 立即换算为 `chunk2.mass/(massSum)`，每 tick 单轮求解 | Rigid 连接显式写入同一个质量权重，另启用 `TerrainCoupled`、Daddy/Brother 7 轮、Terror 9 轮和碰撞后恢复，出生后把质心移回原点 | 质量分配语义保持；额外轮数与碰撞耦合防止 3D 完整图被多面 MTD 撕开，质心平移去掉球面出生采样的净偏置 |
| Hunter Daddy 的 HDmode：固定 4 触手、身体预算 4、前两球质量 20、总长 `Lerp(600px,4*115px,.5)` | 本轮没有 HD 预设 | 用户要求的体型档用 Brother/Daddy/Terror 表达；HD 的特殊 AI/失能语义不应混入普通 Daddy 参数 |
| Brother 在不晚于 Artificer 的时间线上 `SizeClass=true`，使用 Daddy 的 4–7 球/预算 12、5–12 触手与 `3000/400px` 长度分支；较晚才使用小分支 | `daddy-long-legs/brother` 固定采用较晚时间线的小分支：4–6 球/预算 8、5–9 触手与 `1600/300px` | 纯运动内核没有故事时间线输入；稳定 ID 必须只由参数和 seed 决定，早期大分支可由 Daddy 预设表达 |
| squeeze、shortcut、phase-through-terrain、water/buoyancy 和 safari 特例 | 均不实现；Daddy 的所有身体球 `TerrainSqueeze` 必须始终为 `1`，宿主不得改写 | 这些属于格子寻路、房间切换、水体或玩家控制；尤其 squeeze 会破坏“身体 Anchor 可行 ⇒ 不大于它的触手段也可行”的回溯前提 |
| `PlaceInRoom` 令 `unconditionalSupport=1`；静止时持续刷新为 1，恢复移动后每 tick 减 `.025`，约 40 tick 内同时托底支撑与推进；间歇输入可反复刷新 | Create/Teleport 置 1，之后只衰减而不因静止续杯；Shift 保留、Launch 清 0；`EnableMoveStartAssist=false` 为默认 | 连续 3D 中可续杯推进会让点按与长按产生不同驱动力历史；项目保留出生/瞬移展开保护，但不把松键变成重新充能 |
| `PlaceInRoom` 沿理想方向伸到完整 `idealLength`，遇到首个实心 tile 才截短 | 构造器先用 `.34L` 给 sweep 一个非重合暂态；Create/Teleport 后首个未 stun 的 locomotion tick 沿材料偏好伸到全长，或由一次 `ITerrainQuery.Raycast` 截在首个障碍前；Launch 不重新出生展开 | 保留原作“出生即张开”的可观察尺度，同时让构造过程不访问场景树或地形；真正进入模拟后不会长期停留在 `.34L` |
| Terror 身体 4–15、触手 5–12，总长 `Lerp(75m,N*25m,.5)` | 身体 8–12、触手 9–12，总长 `Lerp(75m,N*16m,.5)`；段半径/质量由普通 `.075m/.04` 调为 `.0825m/.052` | 保留大型密集档，同时把 3D 查询和求解成本钳在可回归范围；加粗段链避免缩限后失去体量感 |
| tile path、terrain snap、phase-through-terrain | 每段 sweep + adhesion ray + sphere MTD；最终约束/自避后再从 `LastPos` 做 temporal sweep，residual 后审计裁掉两端球壳的每条相邻链边 | 只用连续 3D `ITerrainQuery`，不重建格子，也不允许穿墙 phase；点状段球可行并不能证明两球之间的直链边没有穿过薄墙 |
| `backtrackFrom` 相对显式 tile path 截断 distal 行为，`Rope` 保存绕地形连接与实际绳长 | 当前相邻直链边一旦阻断就立即截断 distal servo/adhesion/支撑，并把阻断边变成无张力边；同一触手连续阻塞 6 tick 后，以世界材料 frame 驱动的有限候选原子重建整个后缀 | 保留“第一处遮挡以后停止拉墙”的手感；连续 3D 没有可复用的 tile segment/Rope，恢复必须同时证明整段后缀的球、边和自避可行，且不能用会误杀合法绕角的 Anchor→Landing LOS |
| 普通地形碰撞不增加 `chunksGripping`；只有存在落点且段到落点严格 `<200px` 时，`StickToTerrain` 才算主动抓握 | 段显式分为 `TerrainContact` 与 `ActiveGrip`；前者只表示被动碰撞，后者还要求 locomotion 落点、严格 `<5m` 及 adhesion 命中；`GripFraction`、到达奖励和运动支撑只读 `ActiveGrip` | 3D sphere MTD 很容易让长链被动摊在平地上；若把这些段也算支撑，就会把“碰到地”误成“主动抓住”，产生跪行和粘滞 |
| 段数为 `floor(length/40px)`；只有启用 MMF 时才额外钳到最少 3 段 | 本项目无条件使用 `MinimumSegmentsPerTentacle=3`，再受单条/总段数硬上限 | 短触手也必须保留可观察的整链贴面比例；给变长触手和完整图身体的查询成本可证明上限，Terror 的具体缩限见 §2 |
| 段速度上限 `10px/tick=.25m/tick`、公共阻尼 `.9`、半径 `.075m`；主动 `StickToTerrain` 的切向再乘 `.9`；链内所有段对以 `.25m` 排斥；根部前 20% 最多外推 `5px=.125m/tick` | 上限 `.28m/tick`、公共阻尼 `.9`；被动碰撞不再额外耗散切向，主动抓握再乘 `.9`，有效保留率同为 `.90/.81`；普通段半径 `.075m`、段质量 `.04`；全部段对固定两轮分离，接触段修正投影到最多三法线的可行锥；根部外推 `.018m/tick`；另有 4 轮 pull-only 链约束与有限路径 servo；参数强制 `SegmentRadius<=BodyRadiusBase` | 保留原作主动/被动阻尼差和全对自分离；三法线固定 8 轮投影覆盖 3D 非正交内角，仍不可行时放弃该次自避修正；半径不变量保证回溯到最小身体 Anchor 时不会因触手球更大而嵌入；原作没有可直译的段质量 |
| 换步清除旧抓点后直接进入下一轮 Climb/找点；没有独立 Peeling phase 或 6–12 tick 剥离计时 | `Planted → Peeling → Reaching`：从尖端向根部逐步释放到链长 `.45` 处，6 tick 后且远端物理接触比例不高于 `.20` 才转入 Reaching，最迟 12 tick 强制转入；剥离期间远端沿抓面法线抬起并向锚点回收 | 这是明确的 3D 手感偏离：连续球段若同 tick 改落点，旧地面 MTD 会让整段立刻重新贴住，形成超过 30% 的长时间“跪行”；渐进剥离让换腿先真实离面，同时保留近根支撑 |
| 原作由 tile `grabPath`、rope 与回溯决定多余长度形状，没有固定“24% 贴面带” | 3D landing guide 的指令性贴面带不超过 `min(5m,.24L,.55×slack,.24×锚点直距)`；`.985L` 只是 guide 容量上限，桥段只取可用余长的 `.60` 鼓成端点连续的确定性外法线弧，并以 `.45` 法线权重浅角入面 | 避免把所有余长压成地面直尺，也避免“竖直段 + 水平段”的直角；没有被 guide 消耗的长度交还 pull-only 物理链自然展开，实际支撑仍由逐段 `ActiveGrip` 决定 |
| Daddy/Brother 重力 `.9px/tick²`，Terror `.75`；移动档满支撑按 `1.2×` 回补，因而可净向上 `.2g`；基础 air/surface friction 为 `.999/.4`，`Act` 当前 tick 再把速度乘到 `.95` | 接受宿主 `GravityPerTick`；`Body.GravityScale` 恒 1；出生建立高站姿及 movement episode 内仍以 `ContinuousSupport*1.2` 同 tick 回补；air friction `.999→.95`、surface friction 的切向保留倍率 `.4→.72` 由下一次 `Body.Tick` 消费 | 移动期抗重力与净抬升保持原作；阻尼因共享 `Body` 零改动而晚一物理步消费，并保留更多贴面切向速度，避免 3D 棱角粘死——这是时序与 surface friction 的明确项目差异 |
| safari 直控无输入时每 tick 把 `unconditionalSupport` 刷到 1，并把 `num3` 降到至多 1；非 HD 公式里的 `legs/(N/2)` 是整数除法，故少于一半时实际为 `.5`、达到一半即为 `1` | 不续杯 `UnconditionalSupport`；至少一次 movement episode 真结束后，重力回补钳到 `IdleGravityCancellationMaximum=1`，并把质量质心共同速度乘 `IdleBodyVelocityRetention=.8`；出生高站姿和 24 tick 点按宽限不进此档 | 原作的无输入档证明静止不应继续吃 `1.2×` 净抬升；项目仍保留“支撑不足按比例下坠”，且补偿共享 `Body` 在约束/碰撞前消费阻尼、3D 完整图/Jolt 在其后重新写共同速度的执行序差；同量质心修正不改变内部形变或制造力偶 |
| `max(raw^.3,unconditionalSupport)` 直接参与 `Act` | 抗重力与职责使用 `ContinuousSupport=max(raw^.21,unconditionalSupport)`；`.32` 低通后的 `EffectiveSupport` 只供观测；推进只使用真实的 `DirectionalSupport×RawSupport`，默认不读 `unconditionalSupport` | `.21` 是明确的 3D 偏离：整链逐段接触、显式 Peeling 与稀疏长腿使同等可用抓附对应的平均 raw 更低；它仍连续单调且保留 0/1 端点。推进排除出生托底，避免把静止或点按历史变成隐形助推；消融恢复原作 `.30` 时多 seed 高度门会变红 |
| 常规 AI 基础推进 `.6px=.015m/tick`；safari Brother/Daddy/Terror 为 `.35/.25/.15px` | Brother/Daddy/Terror 基础推进 `.024/.020/.017m/tick`，质心沿向速度上限 `.13/.115/.095m/tick` | 原作由 20px 格路径点与碰撞自然限速；连续 3D carrot 需要显式上限，长链也需要更高捕获余量 |
| 理想落点以 `FloatBase` 为根，方向用两次 `Vector3.Slerp`，移动侧评分相对 `mainBodyChunk` | 以真实 Anchor 为根、质量加权 `BodyCenter` 评分；一般角度用 normalize(lerp)，精确反向走固定正交大圆弧 | 不指定“主身体球”；nlerp 更便宜且确定，反向退化显式处理 |
| `neededForLocomotion` 与 `task` 正交；false 后 Locomotion/Climb 任务仍会更新理想落点、搜索、贴地和参与换步 | `Task=Locomotion|ExternalReach` 与 `NeededForLocomotion` 分开存储；`Role` 只是 HUD 派生色。free-duty Locomotion 仍完整工作，只有 ExternalReach/Stunned 脱附且不计运动支撑 | 原先把 free duty 当成停用肢体会让换步候选不足并保留不可达旧锚；正交状态既对齐原作，也保留宿主可征用接口 |
| 忙于 Grabbing/Hunt/ExamineSound 的未征用触手只是降权，理论上可以全部变成 needed；释放项随机 | ExternalReach/Stunned 不可征用，并始终保留 1 条可接外部任务的 free-duty Locomotion；最低 needed 为 2 条（Terror 3 条）、5–7 tick cooldown；按近地距离征用、按最小贡献释放 | 宿主必须随时能指派空闲触手；确定性替代随机，并避免阈值附近职责抖动 |
| 满足条件时每 tick 都可能换步 | 释放后有 8 tick cooldown；起步与普通换腿共用唯一在途槽，上一条必须 `Planted+AtGrabDestination` 或被打断后才能再释放 | 防止 3D 多段链尚未离开旧面时并发抽走多条支撑；仍按同一 ReleaseScore 选腿，不枚举步态模式 |
| 原作换步不预测释放后的重力余量 | 非卡住换步先预测移除候选贡献后的连续抗重力，只有 `cancellationAfter >= 1.00` 的候选可进入 `ReleaseScore` 竞争；stuck 强制换步绕过此门 | 低余量形态会保留抓稳腿，并只让已经失落的腿重搜；强迫它为了凑步数击穿 1g 门会在真实 Jolt 平地重新造成持续下沉 |
| 原作没有“移动意图上升沿就专门换一条腿”的分支；起步托底来自静止刷新后的 `unconditionalSupport` | 默认不启用推进助推；新运动 episode 或大幅改向且方向支撑 `<.40` 时，只从释放后抗重力 `>=.90`、落点侧 `dot<=.25` 的触手中确定性选一条重落点，搜索时移动方向混合至少 `.88`；它与普通换腿共用串行槽 | 这是明确的 3D 偏离：起步只允许一次有界短暂支撑赤字，先建立移动侧抓点再靠真实方向支撑推进；该赤字不进入 `DriveScale`，不能由输入边沿续杯 |
| 原作清点后直接按 tile path 重新找点，没有 3D 抓面跨度概念 | 搜索 ray 0 保留旧抓面法线跨度：把 Anchor 投影到该面，沿理想点或面内移动方向前移，前移不超过 `.25L`、总可及半径 `.985L`；命中法线须与旧面 `dot>=.45`，其余 11 条射线仍负责换面/棱角 | 避免固定 45° 斜射把高站姿腿重种到身体近旁；构造只依赖旧抓面和 Anchor，不使用 world Up，故地面、墙面、天花板同式 |
| `grabPath` 的当前 tile 有 incumbent 加权，同 tile 目标会锁存；常规换点还要求“超过半数触手抓稳且 moving”或 stuck>100 | movement episode 结束时提交全部仍有效的落点并清起步方向；静止时单 tick `AtGrabDestination=false` 不触发重搜，复验需连续 3 次失败才清点；真越出 `1.22L`、落点确实失效或本来没有落点仍可搜索 | 连续 3D 没有 tile 自带的迟滞；显式 incumbent 锁只消除墙边无输入时的隐式换点，不把失效触手冻死，也不增加悬停/墙/顶模式枚举 |
| 80 位历史统计 40 tick 以前、4 tiles 内的旧位置，超过 30 项算卡住 | 直接比较 40 tick 前；位移小于 `.32m` 才累计，`+1/-2` 仍保留 | 连续 3D 世界没有 tile；更小距离避免正常慢速攀附被误报 |
| stuck>100 后每球每 tick 独立 `RNV()*Random.value*3px`；搜索本身也逐次随机 | 每次 episode 由 seed/attempt 锁定一个真实支撑面侧向；`move + side*.75` 同时喂落点、方向支撑、推进和换步，并给全团共同 `.01m/tick` 侧向增量；80–400 tick 内以身体内缘前向 probe 连续 3 次 miss 才解除；超时后的连续 attempt 使用成对精确反向侧向 | 原作随机游走在 3D 会给无轴球团持续转矩且可能左右抵消；锁定绕行可复现、无力偶，成对反向保证第一次绕错侧时下一次真正试另一侧 |
| 找点失败把 Slerp 方向逐渐混向每次新 `RNV` | 12 根扇扫；ray 0 保留 coherent 基向，ray 1..11 才按 seed/search serial 扰动；半径 `.7L→1.2L`、扇角 `62°→168°`，180 tick 展开 | 保留确定性探索，同时避免全部候选在满恢复量时一起丢失运动方向 |
| stun 段阻尼 `.9`，合计额外下坠 `1.2px=.03m/tick`，并逐段每 tick 加 `RNV()*10px`；Violence 先按 SizeClass 把 damage 除 `2.2/1.7`、stun bonus 除 `2/.9`，再取 `int(damage*48+stunBonus)` | 宿主直接给 stun tick；段阻尼 `.9`、额外宿主重力 `1.35×`，不加逐段随机爆速；stun 当下清零该条支撑 | 战斗换算归宿主；按真实重力下垂在 3D 更稳定，也不向球团注入随机转矩 |
| limp 提前返回可能保留旧 `chunksGripping/atGrabDest` | stun 当下即清零该条支撑 | 本轮明确要求被打断触手不再提供支撑 |
| 内部扫描、抓取、伤害与吞入直接操作游戏对象 | 宿主选择 Idle 触手并回喂纯值 target；核心只输出 reach/pull effect | AI、战斗、进食和实体权威不属于动画内核 |

### 1.6 原作没有一一对应项的项目数值

下面这些不是“忘记换算”的原作常量，而是连续 3D 接缝或宿主 API 新增值；原作对应物是 tile
path、对象引用或房间逻辑，因此没有同单位数字可抄。把它们集中列出，避免以后误称为原作值：

| 项目扩展 | 当前值 | 为什么需要 |
|---|---:|---|
| 构造暂态 / 首 tick 展开 / 身体 skin / 触手约束轮数 | 构造 `.34L`；首个可用 locomotion tick 为全 `L` 或首个 ray 命中；`.02m / 4` | 构造器不访问地形且不能让全段重合；首 tick 再通过现有查询接缝恢复原作完整展开尺度 |
| 被动碰撞 / 主动抓握切向系数 | `SegmentCollisionFriction=1.00` / `SegmentSurfaceFriction=.90`；连同公共 `.90` 阻尼，有效为 `.90/.81` | 连续球碰撞需要显式区分“只是碰到”与“主动粘住”；有效阻尼对齐当前 DLL |
| 路径 / 外部 reach servo | Brother `.038/.062`；Daddy `.032/.055`；Terror `.029/.050 m/tick` | 第一项驱动默认 landing guide；第二项用于 ExternalReach 及旧 guide 消融，不再给默认 locomotion 尖端额外直线伺服；大型链更慢，短链更快 |
| adhesion/落点几何 | surface offset `.02m`、probe extra `.035m`、主动抓握目标范围严格 `<5m`、落点复验 `6 tick`、落点至少提交 `20 tick` | 保持球段与面有有限净空；严格范围对齐原作 `<200px`，提交窗避免落点在 3D 连续碰撞中逐 tick 跳换 |
| 渐进剥离 | 6 tick 标称、12 tick 硬上限；远端从 `1.0L→.45L` 逐步释放；离面速度 `.040m/tick`、回收 servo `.018`、转段接触门 `.20` | 原作没有对应计时；这是为连续 3D 换步增加的确定性离面过程，修复远端长时间铺地 |
| slack guide | 贴面带 `min(5m,.24L,.55×slack,.24×锚点直距)`；容量上限 `.985L`，只用可用余长 `.60` 塑成弧；浅角入面法线权重 `.45`；24 个固定弧长样点、12 轮固定二分，最大 bend 幅度 `1.25L` | 不把全部材料强塞进 guide，兼顾高站姿；把被引导的余量收进平滑外法线弧，不再生成 L 形或地面直尺；固定样本/轮数不引入随机数或额外地形查询 |
| 支撑响应 / 普通换步余量 | 原作 `raw^.30`；项目 `SupportResponseExponent=.21`。非 stuck 普通候选预测抗重力不得低于 `1.00`；stuck 强制换步例外 | `.21` 补偿 3D 稀疏整链接触但不增加阈值或模式；1g 门确保移动不靠牺牲身体高度凑换腿次数；两项都有独立消融 |
| movement episode | `MoveEpisodeGraceTicks=24`；`MoveEpisodeDirectionResetDot=.25` | 松键宽限只保留触手规划方向；把短点按序列视为同一次重落点过程。公开输入仍已停止，身体推进、普通换步、stuck 累计/抖动不会借该方向继续运行 |
| 起步重落点 | `StartReplantDirectionalSupportThreshold=.40`；`StartReplantReleaseDotMaximum=.25`；释放后抗重力 `>=.90`；`StartReplantMoveBlend=.88`；与普通换腿共用唯一在途槽 | 连续 3D 平地允许一次有界短暂赤字，先释放一条背向/侧后向触手并在移动侧建立抓点；它不进入推进强度，抓稳或被打断前不再释放第二条 |
| 同面跨度落点 | `SurfaceSpanReachRatio=.985`；面内前移最多 `.25L`；同面法线门 `.45` | 复用清点前抓面或仍有效 incumbent 的法向高度，只沿面推进一小段；避免新落点总落到 Anchor 正下方而逐步降身，且不妨碍其余扇扫射线跨棱换面 |
| 可选起步助推对照 | `EnableMoveStartAssist=false`；`MoveStartAssistTicks=40`；`MoveStartDriveFloor=.36`，只在显式开启时生效 | 用于消融和历史对照，不属于三个稳定预设的默认运动机制；默认路径不能通过点按反复续杯 |
| 空闲近地评分 | 每 `8 tick`，最长 `2.5m` | 原作用 AImap terrain proximity；本项目只能经 `ITerrainQuery` 探测 |
| 3D 扇扫 | 每次 `12` 射线、每 `4 tick`，锥角 `62°→168°`；扰动上限 `.92`，失败展开 `180 tick` | 原作沿单条 2D tile ray；3D 需要有限方位采样，且保留 ray 0 的 coherent 方向 |
| 落点/外部到达 | MoveTarget `.38/.45/.58m`（Brother/Daddy/Terror）；ExternalReach `.20m` | 连续 3D 没有 20px tile center 的隐式容差 |
| 外部拉扯 | 到达半径=`target.Radius+.20m+tip.Radius`；质量倍率=`1/max(.25,target.Mass)`；速度增量 gain `.12`、cap `.10m/tick`；仅距离超过 `.45*tentacle.Length` 的部分按 `.35` 位置修正 | 原作直接改猎物 chunk；本项目只向宿主输出有界纯值效果 |
| detour | 侧向权重 `.75`、最短/最长 `80/400 tick`、4 tick 探一次、3 miss、最短 `1.5m`、体缘余量 `.5m`、共同增量 `.01m/tick`、质心速帽 `1.5×`；超时 attempt 成对精确反向 | 可复现地代替每球随机抖动，并以真实 3D 净空决定恢复；重试不能连续选回同一条坏侧路 |
| Daddy 身体 snag | `SnagStretchRatio=4`、`SnagReleaseTicks=120` | 只保护完整图身体连接的极端拉伸；独立触手段链不进入共享 `Body.Connections`，其墙卡由下一行处理 |
| 触手地形回溯 | `TerrainBacktrackReleaseTicks=6`、断开期回收 servo `.080m/tick`、`TerrainBacktrackCandidatePhases=12`；首阻断段以后立即停止抓附/guide/支撑，阻断边不再传递约束或自避张力 | 短暂外角换面可自然恢复；持续阻断按整条触手 episode 触发原子后缀重建。phase 0 沿阻挡面的安全外法线，后续 phase 在安全半球用确定性 Fibonacci 方位展开；候选存在时至多 12 个 phase 内接回，不存在时保持断开并循环重试，不把后缀硬塞进封闭几何 |
| 最终残余地形恢复 | body 与 tentacle 都最多 `K=4` 次 sphere MTD；body 二周期回上一 tick `LastPos`；触手二周期/耗尽先复验并恢复该段上一 tick 的可行 `LastPos`、保留整条腿落点，只有 `LastPos` 也不可行才收回 Anchor 并清点；tick-end 穿透门 `2mm` | Jolt 共面接缝的单段 MTD 歧义不应升级为整腿换点和乱甩；真正无可行历史时仍有确定的 Anchor 终极回退，额外复验也计入查询硬上限 |
| 查询硬上限 | 按 Jolt primitive units 计，Ray=`1`、Sphere=`2`；公式下限 Brother/Daddy/Terror `1658/2870/4021`，预算 `1700/2900/4050` units/tick | 原作 tile 查询不等价于 Jolt ray/shape query；除 post-constraint sweep 与最终相邻链边审计外，预算还覆盖所有触手同 tick 各验证一个原子恢复候选的前驱球、逐段球壳和链边查询 |

其余相同语义常量已经逐项保留：5 轮出生排斥、`.85/.9` 推开收拢、5N 次最多 30% 长度转移、
100px donor 下限、40px/段、3px 段半径、`.9` 段阻尼、`.7L→1.2L` 搜索距离、20px 到达半径、
`sqrt(grip)`、到达权重 `.75`、重力补偿 gain `1.2`、职责释放阈值 `.85`、方向门
`-.1→-1/.85`、耦合指数 `.8→.1` 以及 stuck 历史 `80/40/+1/-2/0..200`。
原作的支撑响应指数 `.30` 是本轮明确没有照抄的数值；项目取 `.21`，原因和验证见上表与 §5。

## 2. 核心类型与稳定预设

- `DaddyLongLegsParams`：独立出生/运动参数表；工厂构造时冻结快照。
- `DaddyLongLegsMorphology`：某一稳定 seed 生成后冻结的球规格、完整图静息距离、触手规格和
  材料 frame landmark。
- `DaddyLongLegsFactory`：稳定预设查找、无状态 seed 采样、出生装配入口。
- `DaddyTentacle` / `DaddyTentacleSegmentState`：物种自有的独立段链、贴面状态、落点、
  `Task` 与 `NeededForLocomotion` 两个正交职责轴、打断和外部目标效果；不复用其它物种的腿或
  触手实现。
- `DaddyLongLegsLocomotionController`：完整图身体、全部触手、连续支撑、职责预算、推进、卡住
  退化、生命周期和宿主输出的唯一所有者。
- `DaddyLongLegsTargetSnapshot` / `DaddyLongLegsTargetEffect`：非 Node 的纯值目标输入与效果输出。

当前三个稳定 ID：

| 稳定 ID | 身体球 / 预算 | 触手 / 项目长度预算 | 3D 段上限 | tick Jolt units 硬上限 |
|---|---:|---:|---:|---:|
| `daddy-long-legs/brother` | 4–6 / 8 | 5–9 / `Lerp(40m,N*7.5m,.5)` | 单条 14、总计 64 | 1700 |
| `daddy-long-legs/daddy` | 4–7 / 12 | 5–12 / `Lerp(75m,N*10m,.5)` | 单条 18、总计 120 | 2900 |
| `daddy-long-legs/terror` | 8–12 / 18 | 9–12 / `Lerp(75m,N*16m,.5)` | 单条 20、总计 144 | 4050 |

项目统一用 `1 px = 0.025m`。Daddy 的身体/触手数量与总长，以及 Brother 的较晚时间线小分支，
都直接换算当前 DLL；Brother 稳定 ID 固定选用该小分支，
每条与总段数的硬上限是 3D 连续查询扩展，原作没有这些上限。Brother/Daddy 表中的长度公式是
原作的逐米直译；Terror 是有意缩限后的项目公式。它在原作中的大型范围更极端：
身体 4–15、触手 5–12，长度预算为 `Lerp(75m,N*25m,.5)`。项目改为上表的 8–12 / 9–12、
每触手项 16m 与 144 段总上限，保留“大、密、长”的档位差异，同时避免 3D 每段查询无界增长。

未知稳定 ID 快速失败；不会静默落回 Daddy。相同预设、相同 `ulong stableSeed` 的形态和运行
状态必须逐位一致，不同 seed 必须改变形态与最终哈希。

这里还有两个必须由装配与宿主共同保持的恢复不变量。参数校验强制最短可能生成的 `LinkLength`
严格大于 `max(SegmentSelfSeparation, 2*SegmentRadius)`，也会验证最大触手数下的最短初始长度分配
足以生成合法段链；因此原子候选能在相邻长度约束内留出真实球壳与自避净空。Daddy 不使用共享层
为其它生物提供的地形 squeeze，宿主也不得直接把任何 Daddy 身体球的 `TerrainSqueeze` 改成非
`1`；恢复候选会显式复验当前前驱球、每个后缀球与相邻边，不借被 squeeze 改小的体积作可行性
背书。

## 3. 无前向轴的 3D 形态

### 3.1 出生材料 frame

二维的圆周偏好不能直接扩为某个世界平面，否则墙面、天花板和全向运动仍会偷偷依赖世界 Up。
项目用 seed 相位偏移的 Fibonacci sphere 给 `N` 条触手生成近似等面积方向；这些方向先存为
材料局部向量，不是世界 forward。

身体出生布局完成后，选距离最远的一对球为 landmark A/B；再选离 A–B 线最远的第三球 C。
运行时用 A→B 与 C 到其中垂面的方向做 Gram–Schmidt，得到随球团一起运动的材料 frame；与
上一 tick 的第三轴做符号连续性检查，避免数值退化时整套偏好突然镜像。这个 frame 只回答
“出生时长在身体哪一块”，不参与路径朝向、转身或推进评分。

驱动力与锁存 detour 的共同增量对每个身体球加入完全相同的速度，不产生力偶；支撑阻尼也对
整团统一应用。
因此控制器不会为了追随 MoveDir 主动旋转球团。地形碰撞造成的真实被动转动仍会带着材料偏好
一起转动，这是身体形态的连续运动，而不是一根虚构 forward 在追目标。

### 3.2 “上”只属于外部重力

搜索、职责、理想落点和推进都不用 world Up。`TickContext.GravityPerTick` 仍是宿主提供的外力，
所以“下坠”有明确方向；idle 近地探针会把重力方向及其反向作为有限候选，但不把它们当身体
姿态轴。`SupportNormal` 通常是真实 `ActiveGrip` 段法线的加权和；同条触手的主动抓握法线若
数值抵消，才回退到已验证的 `LandingNormal`。被动 `TerrainContact` 法线不进入运动支撑。
全体都无支撑贡献时保留上一次有效法线，只有出生、Teleport 或 Launch 重置时才回到 Up。
这个 Up 只是不可消费的调试初值，不参与姿态或推进。

## 4. 整条触手贴面与查询成本

Create/Teleport 后，构造态只把各段沿材料偏好排到 `.34L`，首个未 stun 的 locomotion tick 会用
一根有界 ray 把整链展开到完整可及长度；若中途命中地形，则停在首个命中点前并把它登记为当前
落点。这个步骤对应原作 `PlaceInRoom` 的“全长或首个实心格”语义；`.34L` 不是运行时休息长度。
Launch 是已有身体的击飞，不重新执行出生展开。

最终 residual 分为 body 与 tentacle 两层，二者都使用 `K=4`，且都只走现有
`ITerrainQuery.SpherePenetration`：

- `Body.Tick` 完成后，控制器对每个身体球最多做 4 次 residual MTD；若连续修正形成相反方向
  二周期，球回到上一 tick 的 `LastPos`。触手 Anchor 因而来自这层已证明可行的 body 位置。
- 每个触手段随后完成三类常规接触操作，并在最终链约束后补 temporal sweep、有限 residual 与
  相邻链边拓扑审计：

1. `Raycast(LastPos, Pos + normalize(Pos-LastPos)*radius)` 做运动扫掠；
2. 沿上一接触法线或当前落点法线做短 adhesion probe；
3. `SpherePenetration(Pos, radius)` 做球体 MTD 去穿透。
4. 最后一次 pull-only 段长松弛和全对自避之后，再从 tick-start `LastPos` 到最终候选位置做一次
   temporal sweep。它专门覆盖“首轮 sweep 被接缝漏报”以及“后续约束把已解碰撞的段重新送过薄墙”；
   命中后把段球中心放回表面外，并累计 `PostConstraintSweepSerial`。
5. 每段最多再做 `K=4` 次 `SpherePenetration`。若连续修正构成相反方向二周期，或第 4 次修正
   后仍命中，先复验该段上一 tick 的 `LastPos`：仍可行则只回滚这一段并保留整腿落点；它也不可行
   才收回已由 body residual 背书的 `Anchor`，同步 `Pos/LastPos`、令 `Vel=Anchor.Vel`，并清除接触
   和旧落点。地形可行性优先于留到下一 tick 收敛的细小绳长误差；smoke 在 tick 结束直接钉死
   穿透不超过 `2mm`。
6. residual 可能回滚单段，所以最后才按根到梢审计 `Anchor→segment0` 与每条相邻链边。射线两端
   只裁去真实 `endpointRadius`；`TerrainSkin` 是碰撞响应容差，不是实体球壳，若也裁掉就会漏过
   厚度不大于两倍 skin 的薄墙。裁剪区间内的任何命中（包括 HitFromInside 零法线）都是直链边
   穿过实体的证据。首阻段以后的 guide servo、adhesion、`ActiveGrip` 和运动支撑立即失效；阻断边
   在段长约束与自避中成为无张力边，使墙另一侧后缀不能继续拖拽身体。

阻断索引可能随近端和身体运动逐段迁移，所以恢复阈值按**整条触手连续存在任一拓扑阻断**累计，
不是要求某一条边连续命中 6 tick。达到阈值后，当前落点或 ExternalReach 任务先失效，再每 tick
验证一个完整后缀候选：当前前驱球必须无穿透；每个候选段球必须无穿透；每条实际相邻边必须通过
上述物理半径裁边审计；候选还必须与全部近端段、以及同一候选中的其他后缀段满足球壳和
`SegmentSelfSeparation`。所有检查全过才一次性提交整段后缀的 `Pos/LastPos/Vel`；任一末端失败，
本 phase 不改变任何段的这三项状态，避免留下半条新链。

phase 0 沿阻挡面安全外法线伸直，后续 phase 以出生 seed 和缓存的世界材料 frame 为参考，在安全
半球按确定性 Fibonacci 方位展开；没有 world-up、运行时随机数或随输入旋转的虚构 forward。
若 `P=TerrainBacktrackCandidatePhases` 个候选中存在可行项，恢复至多在 P 次尝试内完成；封闭夹层、
过窄通道或其它没有可行候选的几何不承诺强行重连，而是保持无张力断开并确定性循环重试。

guide obstruction 与实际拓扑阻断是两套状态：guide 目标落到碰撞面另一侧只会在相同滞回后让旧
landing/ExternalReach 失效重搜，不设置 `BacktrackFrom`、不拆边也不重建几何。拓扑阻断从首个
命中 tick 起就禁止 `Reached/Held` 与拉扯输出；若它中止 ExternalReach，只排队一次旧 StableId 的
`Released`，下一 tick 独占发布，后续重试不会重复事件，触手恢复空闲后可以正常接新任务。

链边审计刻意不检查 `Anchor→Landing` 或 `Anchor→tip` 整条直线。触手绕过凸角时，这条长直线
可以穿过实体，而每条短相邻边仍完全位于可活动空间；把整条 LOS 当可达门会错误释放合法绕角。
本项目审计的是实际渲染/约束链的局部拓扑，这也是它与点式 Centipede 足端可见性门的关键区别。

碰撞层与抓握层显式分开：`TerrainContact` 表示 sweep/MTD 发现该段与地形发生物理接触；只有
`Task=Locomotion`、存在落点、段到落点严格 `<5m`，且 adhesion probe 实际命中时，才置
`ActiveGrip`。公共 `.90` 段阻尼之后，被动碰撞的切向系数为 `1.00`，主动抓握再乘 `.90`，
最终有效切向保留约为 `.90/.81`。支撑比例、支撑法线和到达落点奖励只读取 `ActiveGrip`；
被动摊在地上的段可以显示碰撞、接受 MTD，但不会凭接触本身托住身体。

命中落点后，默认 guide 先求锚点到落点的平滑桥接，再把指令性贴面带钳为
`min(5m, .24L, .55×slack, .24×锚点直距)`。`.985L` 是整条 guide 的容量上限，不是必须填满的
目标长度；桥段只取容量内可用余长的 `.60` 沿真实落点外法线鼓成确定性 Hermite 弧，其余材料交给
pull-only 链与真实支撑自然展开。桥段以 `.45` 法线权重浅角入面，避免在面附近拖出额外直线段。
24 个固定样点按弧长分配段目标，固定 12 轮二分调节弧长；端点位置和一阶切线连续。因此 `.24L`
只钳 guide 的期望贴面上限，不把真实物理接触硬裁成 24%；实际支撑仍完全由逐段 `ActiveGrip` 汇总。

每节只受有限 servo，最终位置仍由 pull-only 相邻约束、真实碰撞和链内**全部段对**的固定两轮
分离共同决定；相邻段不例外。每段固定收集最多三条不共线接触法线，自分离修正按固定 8 轮投影到
这些半空间的共同可行锥；若最终仍对任一法线有超过 `1e-5m` 的入面分量，就保守放弃该次修正。
这比把内角多面平均成一条“切平面”稳定，随后仍由 temporal sweep、residual 和链边审计收尾。
到达已验证落点的 `.75` 奖励是独立的原作语义，不会因为只有一条理想 guide 就成立。

这个表达不重建原作 tile path，也不要求网格拆格。它保留可观察的“整链铺在表面比尖端一点
支撑更强”，同时把复杂度约束为身体球 `B`、触手段总数 `S`、触手数 `T` 和扇扫射线 `R` 的
固定上界按 Jolt primitive units 计算：Raycast=`1`，SpherePenetration=`2`。令完整图连接数
`C=B(B-1)/2`、body 与 tentacle 共用的最终残余恢复上限 `K=4`，保守式为：

```text
(5+2K)B + 14C + (11+2K)S + T(R+5) + 1
```

`(5+2K)B` 包含身体球 sweep/基础 MTD 与最多四次 residual sphere MTD，`14C` 包含完整图连接的碰撞后结构恢复与极端 snag 校验，
`(11+2K)S` 覆盖逐段首轮 sweep、adhesion、基础 MTD、最多四次残余 sphere MTD、
post-constraint temporal sweep、相邻链边审计，以及极端情况下所有触手同时各验证一个原子恢复
候选时每段一次 sphere 与一次 link ray；`T(R+5)` 覆盖初始展开、落点复验、R 条搜索、idle
proximity probe 和恢复前驱球复验；末项是卡住 detour 的身体内缘净空探针。没有无界修复循环或
全对地形查询；正常 tick 通常不会走满。
Daddy 专属的极端身体连接释放阀为
`SnagStretchRatio=4`、`SnagReleaseTicks=120`。物种内部的
计数适配器包住同一个 `ITerrainQuery`，超过预设硬上限后保守返回 miss，并公开
`TickQueryCount / PeakQueryCount / QueryBudgetExceeded`。预算耗尽不会绕开接缝直接访问场景树。
代入三个预设的最坏 `B/C/S/T/R` 后，公式要求的下限分别为 `1658/2870/4021` units/tick；
实际硬预算取 Brother/Daddy/Terror `1700/2900/4050`，只保留明确的小幅计费余量。

## 5. 运动、职责与卡住退化

单条 `Task=Locomotion` 的触手（无论当前是否被运动预算标记为 needed）的贴面比例和支撑为：

```text
gripFraction = activeGripSegments / segmentCount
tentacleSupport = gripFraction^0.5
if any segment reached its landing point:
    tentacleSupport = Lerp(tentacleSupport, 1, 0.75)
```

`activeGripSegments` 只包含距当前落点严格 `<5m` 且 adhesion probe 实际命中的段；
`TerrainContact=true && ActiveGrip=false` 的被动碰撞段不进入 `GripFraction`。到达判定同样要求该段
是主动抓握，并至少经过 2 tick，避免刚扫到地形的被动段在同 tick 领取 `.75` 到达奖励。

ExternalReach 和 stunned 触手的运动支撑恒为 0；进入这两种状态时会清除旧落点、段接触记忆和
adhesion，不会一边显示“失能”一边仍被旧墙面粘住。`Role=Idle` 只是
`Task=Locomotion && !NeededForLocomotion` 的 HUD/预算派生值：这类 free-duty 触手仍持续更新
理想落点、验证可达性、搜索、贴面、贡献支撑并参与换步，所以换向后不会被不可达旧锚拖住。

本项目的总支撑先按全部触手数归一化并取 `rawSupport^0.21`，再与 Create/Teleport 后从 1 每 tick
衰减 `.025`、但静止时不刷新的 `UnconditionalSupport` 取最大，得到直接用于当前 tick 抗重力和
职责阈值的 `ContinuousSupport`。原作指数是 `.30`；项目改为 `.21`，因为连续 3D 的逐段主动抓握、
显式 Peeling 与可变长腿让同等有效支撑对应的平均 raw 更稀疏。映射仍连续单调、0/1 端点不变，
不是抓稳/失抓模式开关；把它恢复为 `.30` 会让多 seed 的真实 Jolt 高度保持断言变红。
另以 `0.32` 低通发布 `EffectiveSupport`，它只作为稳定观测量，不滞后物理反馈。`Body.Tick` 始终先施完整
重力。出生建立高站姿以及 movement episode 内，控制器随后给每个身体球同量加入
`-GravityPerTick * ContinuousSupport * 1.2`，满支撑可净向上 `.2g`；至少一次 episode 真结束后，
同一连续映射改为至多 `1g`，不再让静止球团靠净升力爬出旧锚可及圈。该静止档还只衰减质量质心
共同速度到 `.8`，每球施加同一个 Δv，故内部相对速度、形变和角动量不变。`Body.GravityScale`
始终为 1，失去支撑仍按 `ContinuousSupport` 比例下坠；这里仍没有抓稳/坠落二态开关。
空气/表面阻尼按连续值发布给下一次 `Body.Tick`，静止质心衰减补偿其后 3D 完整图/Jolt 投影写回
共同速度的执行序差，原作值与项目偏离理由见 §1.5。

方向支撑统计已经凭主动抓握到达落点、且抓点位于 MoveDir 一侧的触手占总触手数比例；
ExternalReach / Stunned 因无运动落点自然不进入：

```text
dotFloor = lerp(-0.1, -1.0, clamp(stuck/100,0,1))
side = inverseLerp(dotFloor, 0.85, dot(normalize(landing-bodyCenter), moveDir))^0.8
directionalSupport = sum(side) / tentacleCount
couplingExponent = lerp(0.8,0.1,clamp((stuck-100)/100,0,1))
driveScale = (directionalSupport * rawSupport)^couplingExponent
```

抓点全在身后时 `directionalSupport` 接近 0。推进对所有身体球同向注速并受 `MaxMoveSpeed` 钳制，
不借身体轴推断方向。默认 `DriveScale` 不再与 `UnconditionalSupport` 取最大；出生/Teleport 的
展开保护和松键历史只能影响抗重力，不能成为隐藏的水平起步助推。只有显式打开、正式预设默认关闭的
`EnableMoveStartAssist` 才会加入历史对照用的推进下限。

DLL 直证纠正了早期文档中的一句错误结论：原作并非“没有约 40 tick 的起步托底”。它在静止时
把 `unconditionalSupport` 刷回 1，恢复移动后才按 `.025/tick` 衰减，而且该值同时进入支撑和推进；
因此间歇输入可以反复刷新。原作没有的是“输入上升沿专门释放一条触手”的换腿分支。

本项目有意不照搬这条可续杯路径。三个稳定预设默认 `EnableMoveStartAssist=false`；保留下来的
40 tick / `.36` 字段只供显式消融和历史对照。原因是连续 3D 平地上，旧抓点和整链接触可长时间
留在身后：若每次点按都重新抬高推进，点按与长按会形成不同的力历史，表现为起步粘滞、随后被旧
落点拖低甚至塌身。

替代机制先修抓点几何。真实输入开始一个 movement episode；明显改向（新旧方向 `dot<.25`）
也重建 episode。若 `DirectionalSupport<.40`，控制器只考察已经抓稳、落点相对移动方向
`dot<=.25` 的背向/侧后向触手，先过滤“释放后抗重力抵消 `<.90`”的候选，再按落点最背向、
归一化 `ReleaseScore` 最大、索引最小的固定顺序选一条。该触手沿原有 `Peeling→Reaching→Planted`
重落点；其固定重落点方向在理想点与搜索中占比至少 `.88`。`.90` 只允许这一次起步重落点有
有界短暂支撑赤字，它不参与 `DriveScale`。起步与普通换腿共用唯一在途槽；在它重新抓稳前，
普通换步被抑制，没有安全候选时 pending 保留而不是随便释放另一条；stuck 只扩大在途腿的搜索，
不会绕过槽位再并发释放第二条。

松键后的最多 24 tick 只保留这次 episode 的触手规划方向，使正在剥离/伸够的腿不会在每次点按
间隙把理想落点翻回材料偏好。`HasMoveIntent` 仍立即为 false；无真实输入时全团推进、普通换步、
stuck 累计/抖动都停止，已锁存的 stuck 记忆只冻结。到达直喂目标会硬结束 episode。因而这段
宽限不是隐藏输入、速度缓冲或助推，也不能被松键拿来继续移动身体。

episode 真结束的那个 tick 会提交所有仍有效的 locomotion 落点、解除起步专用方向锁。之后
`AtGrabDestination` 因 adhesion/guide 单 tick 闪断不会再隐式触发搜索；短复验连续 3 次失败、
Anchor 距落点超过 `1.22L`，或本来没有落点时仍按原搜索路径恢复。最终段 MTD 若在 Jolt 接缝
二周期，先复验并回到该段上一 tick 的可行 `LastPos`，保留整条腿落点；只有该历史位置也不可行
才收回 Anchor 并清点。它们共同消除静止墙边反复甩腿，但不会把失效腿永久冻结。

职责分配器至少把 2 条（Terror 为 3 条）`Task=Locomotion` 触手标记为 needed，并硬保留至少
1 条 free-duty Locomotion 给宿主。支撑不足时，只从 free-duty、未 stun、未执行 external reach
的触手中选择；优先使用
最近一次近地探针距离最小者，完全相同时用 seed 与 tick 决定固定扫描起点。支撑高于 `0.85`
且超过最低预算时，释放当前贡献最小的一条。改变职责后有 5 tick cooldown（Terror 为 7），
避免阈值附近来回切换；这项保留名额是项目宿主契约，不是原作事实。

当到达落点的触手超过总数一半且有移动意图，或 stuck>100 时，从全部 `Task=Locomotion`
触手（不仅是 needed 子集）考虑换步。非 stuck 路径先计算移除候选贡献后的
`max(rawAfter^0.21, unconditionalSupport) × 1.2`，低于 `1.00` 的候选不进入竞争，再从安全候选中
选择 `ReleaseScore` 最大的一条；stuck 强制换步不受此门限制。这样不会在移动中反复释放当前最强
支撑腿，也没有提高全局升力。低余量形态可以让已经失去到达状态的触手继续重搜，但不会为了凑
显式步数而抽走仍在承重的腿。起步和普通换腿还共用唯一在途槽：前一条必须进入
`Planted+AtGrabDestination` 或被 stun/外部任务/地形恢复打断，才允许下一次释放。
若当前有有效抓面法线，
它不会同 tick 直接追新落点，而是进入项目新增的 `Peeling`：6 tick 内从尖端向根部逐步取消
主动抓握并沿面法线抬起，释放范围最终覆盖 `.45L` 之后的远端；只有标称 6 tick 已到且这部分的
真实 `TerrainContact` 比例不高于 `.20` 才进入 `Reaching`，12 tick 时无条件转入，避免坏地形
永远锁死。近根主动抓握仍可继续贡献支撑，其余触手会自然接管。该阶段是连续 3D 为避免“膝行”
新增的确定性机制；原作没有对应的 6–12 tick 状态机。

进入 `Reaching` 后才开始下一轮搜索。ray 0 会优先保留清点前抓面或当前有效 incumbent 的法向
跨度：先把 Anchor 投影到该面，再沿理想点的面内方向（退化时沿移动切向）前移，前移不超过
`.25L`，并受 `.985L` 总可及半径约束；命中法线与旧面 `dot>=.45` 才接受。它不读取世界 Up，
所以同一式覆盖地面、墙面与天花板；其余 11 条扇扫仍可跨棱或换面。到达新落点后转为 `Planted`。
连续找不到落点时，该条触手
独立累计失败量：搜索半径从 `0.7L` 放宽到
`1.2L`、扇角从 62° 放宽到 168°，并增加 seed 决定的三维扰动；找到合法落点立即清零。
若全身 detour 已激活，这三个搜索量会立即取满档，不再等待单条失败计数渐增。

身体卡住检测保存 80 tick 环形位置历史，并与 40 tick 前比较。stuck>100 时开启一次锁存 attempt：
优先从与 MoveDir 不平行的真实触手支撑法线求切向侧移；退化时从材料三轴中选正交投影最长者，
最后才由 seed/attempt 补符号。世界空间侧向以 `.75` 权重共同传给理想落点、搜索 ray 0、方向支撑、
推进、换步和全团 `.01m/tick` 共同增量；共同增量只钳质量质心速度，逐球应用完全相同的实际 Δv，
所以不产生控制转矩。attempt 至少 80 tick；每 4 tick 从身体内侧体缘沿原移动方向探测，连续 3 次
miss 才解除；单次 attempt 最迟 400 tick 结束，若届时 stuck 仍高则同 tick 立即重启，且连续
attempt 的锁存侧向成对精确反向，不再重新抽到同一坏侧。宿主明显改向、Teleport 或 Launch 会
立即清除；Shift 保留锁存的世界方向。搜索失败仍独立放宽 `.7L→1.2L` 与 `62°→168°`，但 ray 0
不吃随机扰动。smoke 的 `[DADDY-CORE-STUCK-RETRY]` 会强制首个 attempt 超时，并真断言后继 attempt
已取精确反向且最终脱困。
整个过程不使用 `System.Random`、Godot RNG、wall/floor/ceiling 模式枚举或隐藏的 world forward。

## 6. 宿主输入、效果与生命周期

移动输入与其它可移动并列后端同名同义：

- `MoveDir` / `RunSpeed`：方向与强度；方向不要求平面化。松键会立即停止身体推进，并让
  `HasMoveIntent=false`；内部最多 24 tick 的 movement episode 只让触手完成同方向重落点，
  不把短暂松键伪装成仍在移动。
- nullable `MoveTarget` / `MoveTargetArriveRadius`：宿主直喂的邻近可达点。
- `AtMoveTarget`、`HasMoveIntent`、`LastMoveTarget`、`LastMoveTargetKind`：到达与输入观测；
  `RunSpeed=0` 或已经到点时 kind 为 `None`，有效直喂点为 `External`，有效 MoveDir 因本物种没有
  表面投影 carrot 而记为 `Fallback`。

核心不做 AI、寻路或房间切换。`MoveTarget` 只是优先于 `MoveDir` 的当前 carrot；到达该点会立即
结束 movement episode，不保留旧方向。短间隔点按不会重置默认关闭的起步助推，大幅改向则会
建立新的 episode，并重新判断是否需要安全重落点。

按触手编号的额外接口：

- `StunTentacle(index,ticks)`：立即清旧落点/外部任务、接触记忆与支撑，指定 tick 内软化下垂；其余触手
  会由职责分配器补位。
- `FindIdleTentacle()`：返回第一条当前可接外部任务的触手，若无则为 `-1`；刚清除旧任务、等待
  下一 tick 输出 Released 的触手暂不返回。
- `TryAssignExternalTarget(index,snapshot)`：只允许 free-duty Locomotion 接新目标；已负责同一 StableId 的触手
  可逐 tick 更新快照。
- `ClearExternalTarget(index)`：解除任务，并在下一 tick 输出一次 Released；当前没有外部任务时为
  幂等 no-op，不会清掉该触手已有的运动支撑或落点。

`DaddyLongLegsTargetSnapshot` 包含 `StableId / Position / VelocityPerTick / Radius / Mass /
PullTowardBody`。`DaddyLongLegsTargetEffect` 按触手输出
`TentacleIndex / TargetId / Reached / Held / Released / PositionCorrection / VelocityDelta`。
核心只负责伸够和生成拉向身体的纯值建议，不直接改 Node、扣血、吞入或销毁目标。
`Reached` 与 `Held` 都是本 tick 尖端处于到达半径内的电平值，本轮没有 capture 边沿或持有锁存；
`Released` 才是清除任务后下一 tick 独占一次的事件。Snapshot 要求非零 StableId、全部向量/标量
finite，且 Radius/Mass 非负。
ExternalReach 不计入运动支撑，也不会被职责分配器抢占。`VelocityPerTick` 会随 Shift 保留并折入
确定性状态，但当前拉扯公式只读目标位置、半径、质量和 `PullTowardBody`；目标预测由宿主先更新
`Position` 后回喂，核心不会暗自外推两次。

生命周期：

- `Shift(delta)`：世界 rebase；平移身体、触手、落点、直喂移动目标、外部目标快照、卡住历史，
  以及正在恢复的阻断点/候选参考点；保留恢复 phase、速度、职责、支撑、seed 与连续运动。
- `Teleport(delta)`：地形不随生物移动；保留同一个出生形态，清旧落点、外部任务、MoveTarget、
  单条 stun、支撑、卡住历史与未完成的拓扑恢复；触手先回到材料偏好的 `.34L` 构造暂态，下一可用 locomotion tick
  再伸到全长或首个障碍，恢复最低 locomotion 预算与 `UnconditionalSupport=1`；旧外部任务仍在
  下一 tick 发一次 `Released`。
- `Launch(velocityPerTick)`：身体球和全部触手段同速击飞，立即释放地形落点并让重力全开；
  清除未完成的拓扑恢复，`UnconditionalSupport` 清 0，MoveTarget 与外部目标输入保留，之后由同一
  贴附/职责循环恢复。`StunTentacle`、重新指派或显式清除 ExternalReach 也会清该条触手的暂态恢复。

每 tick 的固定顺序为：`Body.Tick` 施完整重力并消费上一 tick 阻尼 → `UnconditionalSupport-.025` →
body `K=4` residual → 更新材料 frame → 解析原始移动 carrot与只供触手规划的 movement episode
（可选起步助推默认关闭）→ 更新或冻结卡住历史 → 锁存/验证 detour，
得到本 tick locomotionMove → 固定触手索引顺序逐条积分/查地形 → 最终链约束后的 tentacle
全对自分离、post-constraint temporal sweep、`K=4` residual（可行 `LastPos` 优先、Anchor 终极
回退），再逐相邻链边审计、无张力断边与至多一个原子后缀候选 → 更新主动抓握和剥离阶段 →
聚合 `ContinuousSupport` → 同 tick 全团抗重力、episode 后静止质心衰减、推进和确定性共同增量 →
职责分配 → 优先处理起步重落点，再处理普通换步 → 发布 `EffectiveSupport` 观测量及下一 tick 阻尼。
`AtMoveTarget` 与卡住检测始终看原始 carrot，不把绕行方向误当目标；宿主不得拆开或重排阶段。

## 7. 沙盒与回归

独立入口为：

```bash
dotnet run --project core/daddy_long_legs_smoke --no-restore
godot --path . scenes/daddy_long_legs_sandbox.tscn
./tools/run_daddy_long_legs_matrix.sh
```

专项 smoke 与 Godot 矩阵的稳定哈希、配置数量和已验证指标只以脚本当前 PASS 输出为真相源。
起步门不再用“打印起步速度”代替断言：无引擎 smoke 会分别真断言默认助推关闭；低方向
支撑只释放一条满足 `.25/.90` 门的触手；新落点进入移动侧；抓稳前没有第二次普通释放；1/4/10/20
tick 的松键间隙仍属同一 episode，超过 24 tick 才重建；长按与四种等占空比点按都前进、身体不贴地、
且点按不会靠重复 episode 获得超额推进。关闭 `EnableStartReplant` 时对应机制门必须退出 1。
Godot 的 `tap` 路线再以真实 Jolt 地形断言同一 episode、一次安全重落点、支撑/离地净空和有限前进，
并直接拒绝默认误开 `EnableMoveStartAssist`。

移动高度另由 `height-retention` 路线把同一 seed 的 900 tick 高站姿与随后 900 tick 水平移动直接
对照，取各自末窗 P10/中位数而不是单帧峰值。多 seed 必须同时满足高站姿、有限高度损失、持续
前进、至少一次完成的起步重落点、多个真实落点更新、串行在途上限、有限查询与 finite 状态。
低余量形态允许保留抓稳腿、仅让已失落腿重搜；回归不会为了凑普通 Peeling 次数而要求击穿 1g
支撑门。分别关闭 `.21` 支撑响应、同面跨度落点或串行在途槽时，对应高度/推进/并发真门必须变红。
串行槽另有 1-tick stun 时序门：分别在普通换腿与起步重落点占槽期间打断当前腿，要求宿主 API
同步清槽、当次安全窗口不再松第二条腿，下一可释放窗口由另一条腿接管；起步路径还必须重新置位
pending。该门真实运行单条触手 tick，使 `StunTicks` 在控制器检查前从 1 归零，不能靠长眩晕掩盖事件。

静止墙边另有两层真门：无引擎三 seed 在运动结束后观察 480 tick，要求正式换步、起步重落点和
落点变化均为 0，并注入一次“落点仍有效但到达信号单 tick 闪断”验证 incumbent 不重搜；真实 Jolt
`wall-idle` 在墙上撤掉输入后给 240 tick 收敛窗，再观察 906 tick 的落点、身体高度和支撑量。
`EnableIdleLandingStability` 与 `EnableIdleSupportNeutrality` 任一单独关闭时，对应 smoke 都必须退出 1。

本轮地形拓扑专项不是只打印计数：无引擎 smoke 构造 post-constraint sweep 后才出现的跨墙、
合法但会在 residual 回滚后跨墙的非最小 MTD、比 `TerrainSkin` 更薄的墙、阻断索引逐段迁移、
正交双墙、末段球查询失败和候选自避失败等夹具。断言覆盖：阻断第一 tick 即卸力；触手级连续
episode 达阈值；失败候选对整个后缀的 `Pos/LastPos/Vel` 零部分提交；成功候选的全部球、链边与
自避距离合法；ExternalReach 从阻断起不再 Held/拉扯、随后 exactly-once `Released` 且可重新指派；
guide obstruction 只清任务不重建；Shift 保留并平移恢复 phase，Teleport/Launch/Stun 清除；合法
凸角绕行仍保留。关闭 `EnableTerrainBacktrack` 时对应组合门必须退出 1，证明验证门本身有效。

Godot `wall/corner/outer/ceiling/stuck` 路线继续以真实 Jolt 统计每条边和每条触手阻塞 episode，
要求连续阻塞不超过“释放滞回 + 完整有限候选集”的证明上限、终态没有阻断边，且查询预算与
tick-end 残余穿透门同时成立。三个正式预算为 `1700/2900/4050`，公式下限为
`1658/2870/4021`。2026-08-04 完整实跑的无引擎 760 tick 基线为
`C6AE88A2B807488E`，40/400Hz 同值，1mm 微扰为 `9BA981099FC6999D`；Godot flat 基线为
`B8F1A06E5BBEBB7C`，微扰为 `115F1B2121F377AA`。

39 项 Godot 配置与 27 个预期红灯（24 个无引擎 + 3 个 Godot 高度机制消融）全部通过。
`height-retention` 的 seed 1/33/93 站立→移动 P10 分别为
`7.443→7.741 / 7.545→8.331 / 8.991→8.639m`，900 tick 水平推进
`23.052 / 24.584 / 17.920m`；三者都完成起步重落点、由 7/8/7 条不同触手更新落点，串行在途
峰值均为 1。seed 93 的总支撑不足以安全普通 Peeling，故只做一次起步释放并让失落腿重搜，仍在
高位持续推进；把普通门降到 `.95` 的实测反而造成 `1.114m` 累计下沉，故没有采用。
关闭同面跨度后 seed 1 推进降为 `17.207m`；关闭串行槽时并发在途升为 2；恢复原作 `.30`
响应时移动 P10 从站立 `7.094m` 降到 `4.371m`，三项均按退出码真红。
全矩阵查询峰值为 12-body Terror seed 7 的 `1629/4050`；地形路线最长相邻边/整触手阻断
episode 为 10 tick（seed 3 在内角用了第 4 个有限候选后恢复），全部终态阻断边为 0，全部
tick-end 穿透为 `0m`。这些数字来自本次脚本实际输出；后续行为改变仍以脚本钉死常量为真相源。

Godot `course` 的斜坡/台阶/平台整体移到 `z=-48m`，与平地、墙面等路线完全隔离；课程斜坡为
约 `17.2°`，出生点位于其上表面。这样 flat/idle-start 的长触手不会提前抓到课程 collider，
course 也不会从 Ramp/Floor 体积交叠形成的封闭夹层里出生。

哈希和配置数量的可执行真相源仍是 smoke 与矩阵脚本；文档数字改变时必须先由它们实际输出。
沙盒调试渲染只显示身体球、每段触手、逐段被动碰撞/主动抓握、落点、剥离阶段、职责、支撑与
查询量，不承担正式美术职责。拓扑阻断期间不绘制那条已卸力的边，也不再绘制无效 landing/ideal/
external 连线；逐触手 HUD 以 `B<BacktrackFrom>/R<phase>` 显示恢复边界和候选 phase。交互运行时按
`F1` 隐藏/恢复整块调试面板，按 `F2` 在默认精简视图
与完整逐触手明细间切换；面板内也有同名按钮，隐藏后左上角只保留一个小型恢复按钮。

## 8. 明确边界

- 不实现 AI、路径搜索、伤害、进食、消化、水中运动、房间切换或动态实体权威。
- 不把 Daddy 塞进 Lizard/Spider/Deer 的参数或模式分支。
- 不把材料 frame 当头尾方向；它只让出生偏好随同一团身体连续转动。
- 不保证宿主给出的不可达 `MoveTarget` 一定通过；卡住机制只在有限半径内重新分配触手和搜索。
- 触手之间当前不做全局互斥；每条触手内部对包括相邻段在内的全部段对做固定两轮球分离。
  正式表面网格与更严格的连续线段自交处理留给后续视觉/几何轮，本轮的物理断言以段球不深穿插、
  有限约束和不穿静态地形为界。
- 原作的 `backtrackFrom` 依附显式 tile `grabPath`，`Rope` 还保存绕过地形的折点与实际绳长；本项目
  已实现的是连续 3D 下的拓扑等价物，而不是逐公式移植：最终 residual 后逐条审计当前相邻直链边，
  记录首个阻断段并立即截断 distal guide/adhesion/支撑及跨边张力；整条触手连续阻塞 6 tick 后，
  以有限确定性候选原子重建整个后缀。这里不保存完整地形折线，也不维护 Rope 弧长；链仍由实际
  段球和相邻约束表达。
  因此合法凸角绕行只要求每条局部相邻边可行，刻意不做会把有效绕角误判为穿墙的
  `Anchor→Landing` / `Anchor→tip` 全链 LOS。原作超过地形命中阈值后允许 phase-through，本项目为
  保持 3D 静态 collider 不穿透而不提供该退化；恢复保证是条件性的：有限候选集合内存在可行项时
  才保证在候选数次尝试内接回，封闭或过窄的不可行几何会保持无张力断开并继续确定性重试。动态
  地形拓扑变化也只按当 tick 的 `ITerrainQuery` 结果恢复，不访问场景树、不保存引擎对象引用。
