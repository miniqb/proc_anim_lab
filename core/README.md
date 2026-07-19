# ProcAnim.Core —— 3D 雨世界式程序化生物运动内核

独立程序集（`ProcAnim.Core.csproj`）。**唯一依赖是 GodotSharp 的纯托管数学结构**
（`Vector3`/`Mathf`）——不引 `Godot.NET.Sdk`，场景树/物理服务器/`GD` 编译期不可达，
脱离引擎可运行（`smoke/` 即证明）。完整边界契约见
[`docs/porting_contract.md`](../docs/porting_contract.md)。

## 文件

- 内核：`BodyChunk` / `ChunkConnection` / `Body`（chunk 物理）、`Limb`（plant-and-trail 腿）、
  `Walker`（重力开关 + 支撑系 + 推进）、`SphereTerrain`（球-地形共用解算）
- 配置：`BreedParams`（品种参数表，纯出生配置）+ `BodyFactory`（装配器 + 四预设）
- 接缝：`ITerrainQuery`（单射线原语；零法线 = HitFromInside）
  - `godot/RaycastTerrainQuery.cs`：Godot 适配器（**归宿主程序集编译**，core csproj 排除它）
  - `PlaneTerrainQuery`：纯解析平面（测试用）
- 回归：`DeterminismHasher`（FNV-1a 64 状态哈希）、`smoke/`（无引擎冒烟）

## 最小嵌入（宿主三件事：地形、输入、tick）

```csharp
var terrain = new PlaneTerrainQuery(0f);                  // 或宿主的射线适配器
Walker walker = BodyFactory.CreateWalker(new Vector3(0, 0.6f, 0), BodyFactory.Default());
var gravityPerTick = new Vector3(0f, -36f * 0.025f * 0.025f, 0f);

for (long tick = 1; ; tick++)                             // 固定 40 tick/s（宿主自备累加器）
{
    walker.MoveDir = new Vector3(1f, 0f, 0f);             // AI 的两个旋钮
    walker.RunSpeed = 1f;
    walker.Tick(new TickContext(gravityPerTick, terrain, tick));
    // 渲染帧另行读 chunk.LerpPos(t) / limb.LerpPos(t)，t = 物理插值分数
}
```

## 冒烟回归（秒级，无引擎）

```bash
dotnet run --project core/smoke     # 退出码 0=PASS：进程内双跑哈希 bit-exact、巡走里程、无 NaN
```

改内核后先跑它，再跑仓库根 CLAUDE.md §5 的 Godot 全矩阵回归。
