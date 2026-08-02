# ProcAnim.Core —— 3D 雨世界式程序化生物运动内核

独立程序集（`ProcAnim.Core.csproj`）。**只使用 GodotSharp 的纯托管数学结构**
（`Vector3`/`Mathf`）——不引 `Godot.NET.Sdk`（挡住场景树源生成器；GodotSharp 包里
`GD`/`Node` 仍编译期可达，真正的强制是 `smoke/` 的 TypeRef 边界扫描：允许清单之外的
`Godot.*` 引用即回归 FAIL），脱离引擎可运行（`smoke/` 即证明）。完整边界契约见
[`docs/porting_contract.md`](../docs/porting_contract.md)。

## 目录分层

内核按**依赖方向**分层，命名空间跟随目录：上层可依赖下层，反向不行。

```
core/
├── physics/            ProcAnim.Core.Physics        BodyChunk / ChunkConnection / Body
│                                                    ContactManifold3D / SphereTerrain
├── terrain/            ProcAnim.Core.Terrain        ITerrainQuery(+TerrainHit) / PlaneTerrainQuery
├── host/               ProcAnim.Core.Host           TickContext / MoveTargetKind
├── diagnostics/        ProcAnim.Core.Diagnostics    DeterminismHasher
├── species/            ProcAnim.Core.Species        BodyFactory（跨物种装配枢纽）
│   ├── lizard/  humanoid/  spider/  centipede/  cicada/  vulture/  tentacle_plant/  deer/
│                        ProcAnim.Core.Species.<物种>
├── godot/              ProcAnim.Core.Terrain        引擎适配器（归宿主程序集，见下）
├── AssemblyInfo.cs                                  程序集属性（无命名空间）
└── smoke/ spider_smoke/ cicada_smoke/ tentacle_plant_smoke/ deer_smoke/  无引擎回归工程
```

**八个物种目录互为平级，只依赖 `physics`/`terrain`/`host`/`diagnostics` 四层底座。**
唯一的跨物种边是 **Humanoid → Lizard**：人形腿复用蜥蜴 `Limb` 的 opt-in `LookaheadTicks`
前瞻释放循环与 `MoveIntentDeadzone` 常量（其余物种各自声明自己的 deadzone，不共享）。
这条边和「没有别的边」都由 `smoke/` 的 `[CORE-MODULARITY]` 扫描钉死：
`species/<物种>/` 下出现白名单外的另一物种命名空间即回归 FAIL。

扫描走**源码**而非 IL，因为跨物种耦合最常见的形态是编译期常量（`MoveIntentDeadzone`
就是），它在 IL 里被内联得一干二净，元数据扫描看不见。同一断言另外堵死 `global using`
与 csproj `<Using>` 隐式导入——内核**不使用**它们，每个文件的 using 块就是它的真实依赖面，
读文件头即可知道它跨了哪几层；有了隐式导入，per-file using 就不再等于依赖图，
这道扫描会变成假绿。

两处**有意的目录 ≠ 命名空间**：`godot/` 的适配器实现 `Terrain` 的接口却不属于内核程序集
（core csproj 排除 `godot/**`，宿主程序集单独编入），留在顶层是回迁隔离区；
`AssemblyInfo.cs` 只承载程序集属性。

`species/BodyFactory.cs` 装配 lizard + humanoid + vulture 三个物种，是当前唯一的不对称点
（另五个物种各有自己的 `XFactory`）。它坐在 `ProcAnim.Core.Species` 而非任何物种目录里，
就是为了让这个不对称显式可见；按物种拆开是独立的后续工作。

## 文件

- 共享内核：`BodyChunk` / `ChunkConnection` / `Body`（chunk 物理）、`SphereTerrain`
  （球-地形共用解算）、`MoveTargetKind`（并列控制器共用的只读目标来源）
- 蜥蜴后端：`Limb`（单点 plant-and-trail 腿）+ `LizardLocomotionController`；
  控制器负责重力开关、支撑系和推进，`BreedParams` + `BodyFactory` 提供四个预设
- 人形后端（≙ 反编译 Scavenger）：`Arm`（三模式追猎手臂粒子：Dangle/HuntAbsolute/HuntRelative +
  臂长钳制 adaptVel/exaggerate + 腋窝排斥）+ `HumanoidLocomotionController`（清醒近地失重 +
  站立力偶伺服 + 髋高度伺服 + knuckle 撑点俯仰泵 + 手臂优先级链：昏迷→投掷→蓄力→指向→持物→
  撑地→闲置；`Conscious` 开关 = 瘫倒/爬起零状态机）+ `HumanoidParams` 三预设
  `AllHumanoids()`（与蜥蜴 `AllBreeds()` 路由表互不混装）；腿复用 `Limb` 的 opt-in
  `LookaheadTicks` 前瞻释放循环（默认 0 = 蜥蜴路径逐位不变）
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
- 拟态草后端：`TentacleChain`（独立多节触手、路径引导、地形回溯）+
  `TentaclePlantController`（锚定式三维游荡、目标蓄势、突刺、抓取回收）+
  `TentaclePlantParams` / `TentaclePlantFactory`（original/short/hunter 三个稳定预设）；
  完整契约见 [`docs/tentacle_plant_controller.md`](../docs/tentacle_plant_controller.md)
- 鹿后端：`DeerLeg`（独立积分、约束、碰撞、持久有向 bend pole 与摆动内段双通道弯曲的多节腿链）+
  `DeerLocomotionController`（重力恒开；按四腿连续支撑量分配升力与推进，并连续处理
  plant-and-trail、犹豫、延迟休息资格、动态 reach、头/鹿角独立姿态轴和 COM 防倾覆）+
  `DeerParams` / `DeerFactory`
  （`deer/original|compact|strider` 三个稳定 ID）；完整取证、3D 取舍与验证契约见
  [`docs/deer_controller.md`](../docs/deer_controller.md)
- 秃鹫后端：`VultureWing`（段链翅膀：Flap 相位行波 / Grab 射线抓附，对身体零回传）+
  `VultureFlightController`（重力常开 + 拍翅相位同步 sin² 升力脉冲注入后脊柱；
  起飞/悬停/降落由 MoveTarget 几何与翅膀模式组合涌现，无 locomotion 状态机）+
  `VultureBreedParams` / `BodyFactory` 秃鹫四预设（vulture/king/swift/quad）
- 接缝：`ITerrainQuery`（射线 + 球体穿透 MTD 两原语；零法线 = HitFromInside）
  - `godot/RaycastTerrainQuery.cs`：Godot 适配器（**归宿主程序集编译**，core csproj 排除它；
    查询对象复用 + `CollisionMask`/`SetExclusions`）
  - `PlaneTerrainQuery`：纯解析平面（测试用）
- 回归：`DeterminismHasher`（FNV-1a 64 状态哈希；人形折叠序 chunks → legs → arms）、
  `smoke/`（蜥蜴/人形/蜈蚣/秃鹫无引擎冒烟）、
  `spider_smoke/`（蜘蛛拓扑、弯腿几何、生命周期、急转左右槽及站距平衡恢复与确定性）、
  `cicada_smoke/`（蝉专项无引擎冒烟）、
  `tentacle_plant_smoke/`（拟态草装配、三维游荡、攻击时序、命中/扑空、生命周期与确定性）、
  `deer_smoke/`（鹿拓扑、多节腿、常开重力支撑、完整步态、三预设 180° 换向弓向、
  精确 frame 运输消融、地形、生命周期与确定性）

`BodyChunk` / `ChunkConnection` / `Body` / 地形查询是跨生物共享层；
`Limb` + `LizardLocomotionController` + `BreedParams` 是蜥蜴式运动后端，
`Arm` + `HumanoidLocomotionController` + `HumanoidParams` 是人形运动后端，
`CentipedeLeg` + `CentipedeLocomotionController` + `CentipedeParams` 是蜈蚣式运动后端，
`SpiderLeg` + `SpiderLocomotionController` + `SpiderBreedParams` 是蜘蛛式运动后端，
`CicadaLocomotionController` + `CicadaParams` 是蝉式飞行后端，
`TentacleChain` + `TentaclePlantController` + `TentaclePlantParams` 是拟态草伏击后端，
`VultureWing` + `VultureFlightController` + `VultureBreedParams` 是秃鹫式飞行后端，
`DeerLeg` + `DeerLocomotionController` + `DeerParams` 是鹿式多节腿后端。
八者是共享层之上的并列控制器，
不互相继承；后续物种也应沿这个边界增加后端，不把 locomotion 模式堆进一个万能类。
蜈蚣、蝉、拟态草与鹿的完整契约分别见
[`docs/centipede_controller.md`](../docs/centipede_controller.md) 和
[`docs/cicada_controller.md`](../docs/cicada_controller.md)、
[`docs/tentacle_plant_controller.md`](../docs/tentacle_plant_controller.md) 和
[`docs/deer_controller.md`](../docs/deer_controller.md)。

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
                                    # + 引擎边界（TypeRef）与模块边界（跨物种命名空间）双扫描
dotnet run --project core/spider_smoke
                                    # 小/大独立哈希 + 通用拓扑 + 两段 IK + 完整生命周期
                                    # + 可达环/反折恢复 + 抓点背书/超时 + 180°/天花板重抓
                                    # + 小/大逐腿完整步幅、后腿拖步与抬脚高度门
                                    # + 左右 90°/180° 的足端/AEP 槽位与左右站距平衡恢复
dotnet run --project core/cicada_smoke
                                    # 飞行/停驻/起飞/Charge/3D 姿态与生命周期
dotnet run --project core/tentacle_plant_smoke
                                    # 三预设装配 + 三向安装 + idle/hit/miss/occluded
                                    # + 攻击阶段时序 + Shift/Remount/释放目标 + 双跑确定性
dotnet run --project core/deer_smoke
                                    # 三预设装配 + 多节腿/常开重力支撑 + 完整步态/滞回/犹豫
                                    # + 高站姿/身体净空/头角不侵入/动态休息 reach
                                    # + 休息延迟前不误放脚、收腿全过程同对不双空
                                    # + 斜坡/台阶 + MoveTarget/生命周期 + 八项消融与双跑确定性
```

哈希和配置数量以各 smoke / matrix 脚本的当前输出与钉死常量为唯一真相源；文档不复制
可能漂移的快照。改共享内核后先跑全部无引擎 smoke，再跑仓库根的
`./tools/run_matrix.sh`、`./tools/run_spider_matrix.sh`、`./tools/run_cicada_matrix.sh` 和
`./tools/run_tentacle_plant_matrix.sh`、`./tools/run_deer_matrix.sh`，保证既有后端基线不漂移。
