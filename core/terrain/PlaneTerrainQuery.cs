using Godot;

namespace ProcAnim.Core.Terrain;

/// <summary>
/// ITerrainQuery 的纯解析实现：y = FloorY 的无限水平地面，零引擎依赖。
/// 语义与 RaycastTerrainQuery 对齐：起点已陷入地面 → HitFromInside 型命中
/// （Point=起点、Normal=零向量）——调用侧的零法线特判在无引擎回归里同样被走到。
/// 供 smoke/ 冒烟回归与回迁后目标仓库的纯单元测试使用。
/// </summary>
public sealed class PlaneTerrainQuery : ITerrainQuery
{
    public float FloorY;

    /// <summary>Raycast 调用计数（观测用：冒烟回归顺带产出每 tick 射线量级，供性能预算参考）。</summary>
    public long RayCount { get; private set; }

    /// <summary>SpherePenetration 调用计数（同上，形状查询与射线分开计）。</summary>
    public long ShapeQueryCount { get; private set; }

    public PlaneTerrainQuery(float floorY = 0f)
    {
        FloorY = floorY;
    }

    public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
    {
        RayCount++;
        hit = default;
        if (from.Y < FloorY)
        {
            hit = new TerrainHit(from, Vector3.Zero, 1UL);
            return true;
        }
        if (to.Y >= FloorY)
        {
            return false;
        }
        float t = (from.Y - FloorY) / (from.Y - to.Y);
        hit = new TerrainHit(from.Lerp(to, t), Vector3.Up, 1UL);
        return true;
    }

    /// <summary>半空间语义：地面以下无限厚，MTD 恒为竖直向上——深嵌入一步即出。</summary>
    public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth)
    {
        ShapeQueryCount++;
        pushDir = Vector3.Up;
        depth = FloorY - (center.Y - radius);
        return depth > 0f;
    }
}
