# ProcAnim.Core —— 3D 雨世界式程序化生物运动内核

独立程序集（`ProcAnim.Core.csproj`）。**只使用 GodotSharp 的纯托管数学结构**
（`Vector3`/`Mathf`）——不引 `Godot.NET.Sdk`（挡住场景树源生成器；GodotSharp 包里
`GD`/`Node` 仍编译期可达，真正的强制是 `smoke/` 的 TypeRef 边界扫描：允许清单之外的
`Godot.*` 引用即回归 FAIL），脱离引擎可运行（`smoke/` 即证明）。完整边界契约见
[`docs/porting_contract.md`](../docs/porting_contract.md)。

## 文件

- 内核：`BodyChunk` / `ChunkConnection` / `Body`（chunk 物理）、`Limb`（plant-and-trail 腿）、
  `LizardLocomotionController`（重力开关 + 支撑系 + 推进）、`SphereTerrain`（球-地形共用解算）
- 配置：`BreedParams`（品种参数表，纯出生配置）+ `BodyFactory`（装配器 + 四预设）
- 接缝：`ITerrainQuery`（射线 + 球体穿透 MTD 两原语；零法线 = HitFromInside）
  - `godot/RaycastTerrainQuery.cs`：Godot 适配器（**归宿主程序集编译**，core csproj 排除它；
    查询对象复用 + `CollisionMask`/`SetExclusions`）
  - `PlaneTerrainQuery`：纯解析平面（测试用）
- 回归：`DeterminismHasher`（FNV-1a 64 状态哈希）、`smoke/`（无引擎冒烟）

`BodyChunk` / `ChunkConnection` / `Body` / 地形查询是跨生物共享层；
`Limb` + `LizardLocomotionController` + `BreedParams` 是蜥蜴式运动后端。未来蜈蚣、人形等
应在共享层之上增加并列控制器，不向这个类继续堆物种分支。

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

## 冒烟回归（秒级，无引擎）

```bash
dotnet run --project core/smoke     # 退出码 0=PASS：双跑 bit-exact + 哈希对基线（ExpectedHash）
                                    # + 里程/约束收敛/无 NaN + 嵌入恢复 + Shift 连续性 + Launch 恢复
                                    # + MoveTarget 直喂契约 + RotationChunk 拓扑 + wall-pose 顶死稳定性
                                    # + 边界扫描
```

改内核后先跑它，再跑仓库根的 `./tools/run_matrix.sh` 全矩阵回归（断言化，见 CLAUDE.md §5）。
