using Godot;

namespace ProcAnim.Core;

/// <summary>
/// 地形射线命中结果。Normal 为零向量表示射线起点已陷入 collider
/// （Godot HitFromInside 命中）——调用侧必须特判，直接归一化会产生 NaN。
/// </summary>
public readonly struct TerrainHit
{
    public readonly Vector3 Point;
    public readonly Vector3 Normal;

    /// <summary>命中的 collider 实例 id（M2 预留：脚抓着哪块地形）。</summary>
    public readonly ulong ColliderId;

    public TerrainHit(Vector3 point, Vector3 normal, ulong colliderId)
    {
        Point = point;
        Normal = normal;
        ColliderId = colliderId;
    }
}

/// <summary>
/// 地形查询抽象：物理内核只认这一条原语，与 Godot 物理服务器解耦（M5 回迁边界）。
/// M2 的落脚点搜索、M3 的换向支撑射线都由调用侧用不同 from/to 组合出来。
/// </summary>
public interface ITerrainQuery
{
    bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit);
}
