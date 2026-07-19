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
/// 地形查询抽象：物理内核只认这两条原语，与 Godot 物理服务器解耦（M5 回迁边界）。
/// Raycast：M2 的落脚点搜索、M3 的换向支撑射线都由调用侧用不同 from/to 组合出来。
/// SpherePenetration：球体重叠的最小平移向量——射线全是「球心线」，擦边侵入看不见、
/// 整球嵌入给不出方向，球形碰撞的承诺必须由形状查询兜底（外部评审 P1-2/P1-3）。
/// </summary>
public interface ITerrainQuery
{
    bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit);

    /// <summary>
    /// 球体重叠查询：球（center, radius）与静态地形交叠时返回 true，并给出最小平移
    /// 向量（MTD）：沿 pushDir（单位向量）平移 depth（&gt;0）即完全脱离。深度嵌入
    /// （球心已在 collider 内部）也必须给出有效方向——这是嵌入状态的唯一恢复通道
    /// （Raycast 的 HitFromInside 零法线给不出方向，曾导致出生嵌入永久冻结）。
    /// 未交叠返回 false（恰好相切 depth=0 也算未交叠，静息接触不抖）。
    /// </summary>
    bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth);
}
