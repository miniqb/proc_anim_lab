using Godot;
using ProcAnimLab.Physics;

namespace ProcAnimLab.Terrain;

/// <summary>
/// ITerrainQuery 的射线实现：包装 PhysicsDirectSpaceState3D 打真实 3D collider
/// （研究文档 §12：不重建格子碰撞）。这是物理内核与 Godot 物理服务器之间唯一的接缝。
/// DirectSpaceState 只在物理帧内合法——每 tick 开头由驱动节点 Bind 注入。
/// </summary>
public sealed class RaycastTerrainQuery : ITerrainQuery
{
    private PhysicsDirectSpaceState3D? _space;
    private uint _collisionMask = 1;

    /// <summary>每 tick 在 _PhysicsProcess 开头调用，注入当帧合法的空间状态。</summary>
    public void Bind(PhysicsDirectSpaceState3D space)
    {
        _space = space;
    }

    public bool Raycast(Vector3 from, Vector3 to, out TerrainHit hit)
    {
        hit = default;
        if (_space is null)
        {
            return false;
        }

        var query = PhysicsRayQueryParameters3D.Create(from, to, _collisionMask);
        query.HitFromInside = true; // 起点陷入 collider 时返回零法线命中，调用侧特判
        Godot.Collections.Dictionary result = _space.IntersectRay(query);
        if (result.Count == 0)
        {
            return false;
        }

        hit = new TerrainHit(
            (Vector3)result["position"],
            (Vector3)result["normal"],
            (ulong)result["collider_id"]);
        return true;
    }
}
