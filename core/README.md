# ProcAnim.Core —— 3D 雨世界式程序化生物运动内核

独立程序集（`ProcAnim.Core.csproj`）。**只使用 GodotSharp 的纯托管数学结构**
（`Vector3`/`Mathf`）——不引 `Godot.NET.Sdk`（挡住场景树源生成器；GodotSharp 包里
`GD`/`Node` 仍编译期可达，真正的强制是 `smoke/` 的 TypeRef 边界扫描：允许清单之外的
`Godot.*` 引用即回归 FAIL），脱离引擎可运行（`smoke/` 即证明）。完整边界契约见
[`docs/porting_contract.md`](../docs/porting_contract.md)。

## 文件

- 共享内核：`BodyChunk` / `ChunkConnection` / `Body`（chunk 物理）、
  `SphereTerrain`（球-地形共用解算）
- 蜥蜴后端：`Limb`（plant-and-trail 腿）+ `LizardLocomotionController`
  （重力开关 + 支撑系 + 推进）+ `BreedParams` / `BodyFactory`（四预设）
- 蜈蚣后端：`CentipedeLeg` + `CentipedeLocomotionController`（双端表面轨迹 + 每端持久切向
  + 逐节支撑 + 确定性行波/自避）+ `CentipedeParams` / `CentipedeFactory`
  （四个稳定 ID 预设）；
  完整契约见 [`docs/centipede_controller.md`](../docs/centipede_controller.md)
- 接缝：`ITerrainQuery`（射线 + 球体穿透 MTD 两原语；零法线 = HitFromInside）
  - `godot/RaycastTerrainQuery.cs`：Godot 适配器（**归宿主程序集编译**，core csproj 排除它；
    查询对象复用 + `CollisionMask`/`SetExclusions`）
  - `PlaneTerrainQuery`：纯解析平面（测试用）
- 回归：`DeterminismHasher`（FNV-1a 64 状态哈希）、`smoke/`（无引擎冒烟）

`BodyChunk` / `ChunkConnection` / `Body` / 地形查询是跨生物共享层。蜥蜴与蜈蚣是其上的
两个并列、物种专属后端；核心层不制造万能生物接口，也不把一个物种的分支堆进另一个控制器。

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
`MoveTarget` 对两类控制器都只是宿主直喂的邻近可达点，不包含 AI 寻路。

## 冒烟回归（秒级，无引擎）

```bash
dotnet run --project core/smoke     # 退出码 0=PASS：双跑 bit-exact + 哈希对基线
                                    # + 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 + Launch 恢复
                                    # + MoveTarget 直喂契约 + RotationChunk 拓扑 + wall-pose 顶死稳定性
                                    # + 蜈蚣装配/显式头尾切换/课程/固定头下阶梯/自避/查询增长
                                    # + 蜈蚣脚跨薄墙恢复（扫掠/低速 MTD/停驶抓点/同侧对照）
                                    # + 边界扫描
```

当前无引擎基线：Lizard `AAA0E4963668E5DC`、centipede/short
`4DAD09DE3CB81C31`、centipede/long `4E3DFC052BA4E74D`。改内核后先跑 smoke，再跑仓库根的
`./tools/run_matrix.sh`。当前 Godot 全矩阵共 **32 项 = 旧 20 项 Lizard + 新 12 项
Centipede**，已经全部通过。蜈蚣最终 Godot 哈希为：

- 巡逻 short/long/armored/ribbon：`BE58C639D59E1EA2`、`0D1D0D51D5E9C26B`、
  `D595C149C1C6B8EC`、`D834CFF4122082C3`；
- course-short/course-long/step-down-armored：`D6F99637C6D76EE1`、
  `30793ACEDD88F34C`、`3D2594F93BC2F009`；
- embed-long/wallside-long：`FE8E2E356129F7A2`、`E2837F5747FDFBFF`。

short/long 课程的 `maxNoneRun=1/9`、`maxBlockedRun=0/0`、`maxConnectionRun=4/7`，
尾端通过为 `15/80`、`89/184` tick（实际/预算），穿透均为 `0m`。固定头下阶梯的
领/尾端落地为 tick `46/116`，终态非相邻间距 `1.917×` 半径和，严重成团连续 `0` tick。
