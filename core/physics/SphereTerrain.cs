using Godot;
using ProcAnim.Core.Terrain;

namespace ProcAnim.Core.Physics;

/// <summary>
/// 球 vs 地形命中的共用解算：Body 的 chunk 碰撞与 Limb 的脚部出地形走同一份语义，
/// 保证「无反弹 + 切向摩擦」的手感只有一个实现。只推 Pos 不注入反向速度（静息防抖）。
/// </summary>
public static class SphereTerrain
{
    /// <summary>
    /// 解穿透 + 速度响应。返回是否真的产生了接触（normal 输出接触法线）。
    /// HitFromInside（零法线）直接返回 false：射线给不出脱离方向与深度，
    /// 嵌入恢复由 <see cref="ITerrainQuery.SpherePenetration"/> 的 MTD 负责
    /// ——旧版在这里回退 LastPos + 清速，出生嵌入时 LastPos 同样在内部，永久冻结。
    /// </summary>
    public static bool Resolve(in TerrainHit hit, float radius, float surfaceFriction,
        ref Vector3 pos, ref Vector3 vel, out Vector3 normal)
    {
        if (hit.Normal.LengthSquared() < 1e-12f)
        {
            normal = Vector3.Zero;
            return false;
        }

        normal = hit.Normal;
        float depth = radius - (pos - hit.Point).Dot(normal);
        if (depth <= 0f)
        {
            return false;
        }

        pos += normal * depth;
        RespondVelocity(normal, surfaceFriction, ref vel);
        return true;
    }

    /// <summary>接触的速度响应（Resolve 与 MTD 去穿透共用）：法向内向速度清零（无反弹，
    /// 雨世界手感）、切向乘 surfaceFriction。</summary>
    public static void RespondVelocity(in Vector3 normal, float surfaceFriction, ref Vector3 vel)
    {
        float vn = vel.Dot(normal);
        if (vn < 0f)
        {
            vel -= normal * vn;
        }
        Vector3 vt = vel - normal * vel.Dot(normal);
        vel -= vt * (1f - surfaceFriction);
    }
}
