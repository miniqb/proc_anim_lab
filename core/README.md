# ProcAnim.Core —— 3D 雨世界式程序化生物运动内核

独立程序集（`ProcAnim.Core.csproj`）。**只使用 GodotSharp 的纯托管数学结构**
（`Vector3`/`Mathf`）——不引 `Godot.NET.Sdk`（挡住场景树源生成器；GodotSharp 包里
`GD`/`Node` 仍编译期可达，真正的强制是 `smoke/` 的 TypeRef 边界扫描：允许清单之外的
`Godot.*` 引用即回归 FAIL），脱离引擎可运行（`smoke/` 即证明）。完整边界契约见
[`docs/porting_contract.md`](../docs/porting_contract.md)。

## 文件

- 共享内核：`BodyChunk` / `ChunkConnection` / `Body`（chunk 物理）、`SphereTerrain`
  （球-地形共用解算）、`MoveTargetKind`（并列控制器共用的只读目标来源）
- 蜥蜴后端：`Limb`（单点 plant-and-trail 腿）+ `LizardLocomotionController`；
  控制器负责重力开关、支撑系和推进，`BreedParams` + `BodyFactory` 提供四个预设
- 蜈蚣后端：`CentipedeLeg` + `CentipedeLocomotionController`（双端表面轨迹 + 每端持久切向
  + 逐节支撑 + 确定性行波/自避）+ `CentipedeParams` / `CentipedeFactory`
  （四个稳定 ID 预设）；
  完整契约见 [`docs/centipede_controller.md`](../docs/centipede_controller.md)
- 蜘蛛后端：`SpiderLeg`（足端粒子 + 派生两段 IK 膝点）+
  `SpiderLocomotionController`；`SpiderBreedParams` + `SpiderFactory` 提供
  `spider-small` / `spider-large`，并允许线性身体链的任意节挂任意多对腿；
  同侧横向反投影会在有限落点余量内寻找窄墙/柱体的相邻侧面；
  `TrailReleaseRatio` 控制抬腿时机，`TouchdownLeadRatio` 定义抬腿瞬间冻结的
  世界落脚前缘（AEP），左右交叉相位形成两组对角四足步态。长腿品种因此会完成
  一次完整前摆，而不是逐 tick 追着移动目标高频挪小步；急转后若旧抓点落到新的
  局部身体对侧，只在下一次抬腿捕获 AEP 时、且该腿仍与当前支撑面一致时镜像横向
  工作区。仍在本侧但被转弯压窄的工作区，则在同一支撑面上的每次正常抬腿中向该
  槽位的名义宽度回收 60%；已植脚不滑动，不逐 tick 拖动已冻结目标，也不干扰窄墙
  多面抱持
- 蝉后端：`CicadaLocomotionController`（双 chunk 差分飞行、停驻、起飞、Charge）+
  `CicadaParams` / `CicadaFactory` + 四翼四触须的固定 tick 表现状态
- 接缝：`ITerrainQuery`（射线 + 球体穿透 MTD 两原语；零法线 = HitFromInside）
  - `godot/RaycastTerrainQuery.cs`：Godot 适配器（**归宿主程序集编译**，core csproj 排除它；
    查询对象复用 + `CollisionMask`/`SetExclusions`）
  - `PlaneTerrainQuery`：纯解析平面（测试用）
- 回归：`DeterminismHasher`（FNV-1a 64 状态哈希）、`smoke/`（蜥蜴/蜈蚣无引擎冒烟）、
  `spider_smoke/`（蜘蛛拓扑、弯腿几何、生命周期、急转左右槽及站距平衡恢复与确定性）、
  `cicada_smoke/`（蝉专项无引擎冒烟）

`BodyChunk` / `ChunkConnection` / `Body` / 地形查询是跨生物共享层；
`Limb` + `LizardLocomotionController` + `BreedParams` 是蜥蜴式运动后端，
`CentipedeLeg` + `CentipedeLocomotionController` + `CentipedeParams` 是蜈蚣式运动后端，
`SpiderLeg` + `SpiderLocomotionController` + `SpiderBreedParams` 是蜘蛛式运动后端，
`CicadaLocomotionController` + `CicadaParams` 是蝉式飞行后端。四者是共享层之上的并列控制器，
不互相继承；后续物种也应沿这个边界增加后端，不把 locomotion 模式堆进一个万能类。
蜈蚣与蝉的完整契约分别见
[`docs/centipede_controller.md`](../docs/centipede_controller.md) 和
[`docs/cicada_controller.md`](../docs/cicada_controller.md)。

## 最小嵌入（宿主三件事：地形、输入、tick）

```csharp
var terrain = new PlaneTerrainQuery(0f);                  // 或宿主的射线适配器
LizardLocomotionController controller = BodyFactory.CreateLizardController(
    new Vector3(0, 0.6f, 0), BodyFactory.Default());
var gravityPerTick = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);

for (long tick = 1; ; tick++)                             // 固定 40 tick/s（宿主自备累加器）
{
    controller.MoveDir = new Vector3(1f, 0f, 0f);          // AI 的两个旋钮
    controller.RunSpeed = 1f;
    controller.Tick(new TickContext(gravityPerTick, terrain, tick));
    // 渲染帧另行读 chunk.LerpPos(t) / limb.LerpPos(t)，t = 物理插值分数
}
```

蜈蚣使用同一宿主循环，装配入口改为：

```csharp
CentipedeLocomotionController controller =
    CentipedeFactory.CreateController(origin, CentipedeFactory.Long());
controller.RequestedLeadEnd = CentipedeLeadEnd.Start;
```

`RequestedLeadEnd` 是蜈蚣宿主显式写入并保持的领航端请求，在下一次 `Tick` 生效；
`LeadEnd` 是已应用状态；
`MoveDir`/`MoveTarget` 不自动推断或切换头尾。自动选端与去抖属于宿主/AI。
当输入在新表面上的投影退化时，控制器沿既定领航端平行运输该端保存的表面切线继续过角，
不会用世界 `Up/Right` 猜方向，也不要求宿主为了下墙临时补一个向下输入。
沙盒交互模式与 `--lead=start|end` 都显式锁定该请求，不自动换端；只有未传 `--lead` 的
无头 default 巡逻脚本演示宿主层方向评分 + 3 tick 去抖，并通过 `RequestedLeadEnd` 发命令。
该策略不在核心中。
`MoveTarget` 对这些控制器都只是宿主直喂的邻近可达点，不包含 AI 寻路。

蜘蛛的宿主输入与生命周期同形：

```csharp
SpiderLocomotionController spider = SpiderFactory.CreateSpiderController(
    new Vector3(0, 0, 0), SpiderFactory.SmallSpider());
spider.MoveDir = Vector3.Right;
spider.RunSpeed = 1f;
spider.Tick(new TickContext(gravityPerTick, terrain, tick));

// 渲染：身体读 chunk.LerpPos(t)，弯腿读 leg.LerpRoot(t) / LerpKnee(t) / LerpPos(t)。
```

## 冒烟回归（秒级，无引擎）

```bash
dotnet run --project core/smoke     # 退出码 0=PASS：双跑 bit-exact + 哈希对基线
                                    # + 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 + Launch 恢复
                                    # + MoveTarget 直喂契约 + RotationChunk 拓扑 + wall-pose 顶死稳定性
                                    # + 蜈蚣装配/显式头尾切换/课程/固定头下阶梯/自避/查询增长
                                    # + 蜈蚣脚跨薄墙恢复（扫掠/低速 MTD/停驶抓点/同侧对照）
                                    # + long 双端窄墙前向翻越、远侧抓足与停驶收敛
                                    # + 边界扫描
dotnet run --project core/spider_smoke
                                    # 小/大独立哈希 + 通用拓扑 + 两段 IK + 完整生命周期
                                    # + 可达环/反折恢复 + 抓点背书/超时 + 180°/天花板重抓
                                    # + 小/大逐腿完整步幅、后腿拖步与抬脚高度门
                                    # + 左右 90°/180° 的足端/AEP 槽位与左右站距平衡恢复
```

当前无引擎基线：Lizard `AAA0E4963668E5DC`、centipede/short
`655A21496C00E86A`、centipede/long `59CBCF993DF8ACD8`。改内核后先跑 smoke，再跑仓库根的
`./tools/run_matrix.sh`。当前 Godot 全矩阵共 **33 项 = 旧 20 项 Lizard + 新 13 项
Centipede**，已经全部通过。蜈蚣最终 Godot 哈希为：

- 巡逻 short/long/armored/ribbon：`0F040547BFD02043`、`B66DAAB5D006190E`、
  `A6EDF4704829C261`、`EB6011908D0FAA19`；
- course-short/course-long/step-down-armored：`BB6696619749832D`、
  `A2BE4857DB102C19`、`ECC5207E14979A28`；
- narrow-wall-long-end：`413E289A97ABD487`；
- embed-long/wallside-long：`2C8B2D67731F2B7E`、`501B7C44E06FA68B`。

short/long 课程的 `maxNoneRun=4/10`、`maxBlockedRun=0/0`、`maxConnectionRun=3/8`，
尾端通过为 `15/80`、`89/184` tick（实际/预算），穿透均为 `0m`。固定头下阶梯的
领/尾端落地为 tick `51/121`，终态非相邻间距 `1.917×` 半径和，严重成团连续 `0` tick。
固定 End 窄墙前向翻越后停驶 `381` tick，终态连接偏差 `7%`、穿透 `0m`。

蝉后端另跑：

```bash
dotnet run --project core/cicada_smoke
./tools/run_cicada_matrix.sh
```

改内核后先跑三个 smoke，再跑仓库根的 `./tools/run_spider_matrix.sh`、
`./tools/run_cicada_matrix.sh` 和 `./tools/run_matrix.sh` 三套全矩阵回归
（断言化，见 CLAUDE.md §5），保证已有 Lizard / Centipede / Spider / Cicada 基线保持不变。
