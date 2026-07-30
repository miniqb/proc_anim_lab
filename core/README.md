# ProcAnim.Core —— 3D 雨世界式程序化生物运动内核

独立程序集（`ProcAnim.Core.csproj`）。**只使用 GodotSharp 的纯托管数学结构**
（`Vector3`/`Mathf`）——不引 `Godot.NET.Sdk`（挡住场景树源生成器；GodotSharp 包里
`GD`/`Node` 仍编译期可达，真正的强制是 `smoke/` 的 TypeRef 边界扫描：允许清单之外的
`Godot.*` 引用即回归 FAIL），脱离引擎可运行（`smoke/` 即证明）。完整边界契约见
[`docs/porting_contract.md`](../docs/porting_contract.md)。

## 文件

- 内核：`BodyChunk` / `ChunkConnection` / `Body`（chunk 物理）、`SphereTerrain`
  （球-地形共用解算）、`MoveTargetKind`（并列控制器共用的只读目标来源）
- 蜥蜴后端：`Limb`（单点 plant-and-trail 腿）+ `LizardLocomotionController`；
  `BreedParams` + `BodyFactory` 提供四个预设
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
- 接缝：`ITerrainQuery`（射线 + 球体穿透 MTD 两原语；零法线 = HitFromInside）
  - `godot/RaycastTerrainQuery.cs`：Godot 适配器（**归宿主程序集编译**，core csproj 排除它；
    查询对象复用 + `CollisionMask`/`SetExclusions`）
  - `PlaneTerrainQuery`：纯解析平面（测试用）
- 回归：`DeterminismHasher`（FNV-1a 64 状态哈希）、`smoke/`（蜥蜴无引擎冒烟）、
  `spider_smoke/`（蜘蛛拓扑、弯腿几何、生命周期、急转左右槽及站距平衡恢复与确定性）

`BodyChunk` / `ChunkConnection` / `Body` / 地形查询是跨生物共享层；
蜥蜴与蜘蛛是共享层之上的两个并列控制器，不互相继承。后续物种也应沿这个边界增加后端，
不向任一现有控制器继续堆物种分支。

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
dotnet run --project core/smoke     # 退出码 0=PASS：双跑 bit-exact + 哈希对基线（ExpectedHash）
                                    # + 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 + Launch 恢复
                                    # + MoveTarget 直喂契约 + RotationChunk 拓扑 + wall-pose 顶死稳定性
                                    # + 边界扫描
dotnet run --project core/spider_smoke
                                    # 小/大独立哈希 + 通用拓扑 + 两段 IK + 完整生命周期
                                    # + 可达环/反折恢复 + 抓点背书/超时 + 180°/天花板重抓
                                    # + 小/大逐腿完整步幅、后腿拖步与抬脚高度门
                                    # + 左右 90°/180° 的足端/AEP 槽位与左右站距平衡恢复
```

改内核后先跑两个 smoke，再跑仓库根的 `./tools/run_spider_matrix.sh` 和
`./tools/run_matrix.sh` 两套全矩阵回归（断言化，见 CLAUDE.md §5）。
