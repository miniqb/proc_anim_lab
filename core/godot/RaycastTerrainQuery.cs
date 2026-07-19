using Godot;

namespace ProcAnim.Core;

/// <summary>
/// ITerrainQuery 的 Godot 实现：包装 PhysicsDirectSpaceState3D 打真实 3D collider
/// （研究文档 §12：不重建格子碰撞）。这是物理内核与 Godot 物理服务器之间唯一的接缝。
/// DirectSpaceState 只在物理帧内合法——每 tick 开头由驱动节点 Bind 注入。
/// 查询参数对象全部复用（≙ 主项目 ContactPlanner 规范；每射线 new 一个参数对象
/// 在多怪物场景是每秒数万次分配）；IntersectRay/GetRestInfo 返回的 Dictionary
/// 是引擎 API 固有分配，无非分配变体可用。
/// </summary>
public sealed class RaycastTerrainQuery : ITerrainQuery
{
    private PhysicsDirectSpaceState3D? _space;

    private readonly PhysicsRayQueryParameters3D _rayParams = new()
    {
        HitFromInside = true, // 起点陷入 collider 时返回零法线命中，调用侧特判
        CollisionMask = 1,
    };

    private readonly SphereShape3D _sphere = new();
    private readonly PhysicsShapeQueryParameters3D _shapeParams;

    public RaycastTerrainQuery()
    {
        _shapeParams = new PhysicsShapeQueryParameters3D
        {
            Shape = _sphere,
            CollisionMask = 1,
        };
    }

    /// <summary>碰撞掩码（默认层 1）。回迁时设为静态可走地形专层（主项目：层 20），
    /// 动态物/角色绝不能进掩码——脚会试图踩上去。</summary>
    public uint CollisionMask
    {
        get => _rayParams.CollisionMask;
        set
        {
            _rayParams.CollisionMask = value;
            _shapeParams.CollisionMask = value;
        }
    }

    /// <summary>排除的 RID（宿主自身的 CharacterBody/碰撞体等，≙ 主项目 ContactPlanner
    /// 的 host RID exclusion 规范）。传入的数组被两类查询共享持有。</summary>
    public void SetExclusions(Godot.Collections.Array<Rid> exclude)
    {
        _rayParams.Exclude = exclude;
        _shapeParams.Exclude = exclude;
    }

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

        _rayParams.From = from;
        _rayParams.To = to;
        Godot.Collections.Dictionary result = _space.IntersectRay(_rayParams);
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

    public bool SpherePenetration(Vector3 center, float radius, out Vector3 pushDir, out float depth)
    {
        pushDir = Vector3.Up;
        depth = 0f;
        if (_space is null)
        {
            return false;
        }

        _sphere.Radius = radius;
        _shapeParams.Transform = new Transform3D(Basis.Identity, center);
        Godot.Collections.Dictionary rest = _space.GetRestInfo(_shapeParams);
        if (rest.Count == 0)
        {
            return false;
        }

        Vector3 point = (Vector3)rest["point"];
        Vector3 normal = (Vector3)rest["normal"];
        if (normal.LengthSquared() < 1e-12f)
        {
            // 深嵌入时引擎可能给不出法线：用「接触点→球心」兜底，保证总有确定性的脱离方向。
            Vector3 away = center - point;
            normal = away.LengthSquared() < 1e-12f ? Vector3.Up : away.Normalized();
        }

        depth = radius - (center - point).Dot(normal);
        if (depth <= 0f)
        {
            return false;
        }
        pushDir = normal;
        return true;
    }
}
